using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using NCBRS.Devices;
using NCBRS.LoadTest;
using NCBRS.Models;

// The idempotency key header the API reads (NCBRS.Api's TransactionContext).
// A stable wire constant; inlined so this tool depends only on Contracts.
const string TransactionIdHeader = "X-Transaction-Id";

// WS-A7 load and soak driver.
//
// The A7 workload is not "average requests per second" — it is the burst that
// happens when a region's connectivity returns and many village posts upload
// their weeks-long outboxes at once. So this fires whole sync batches
// concurrently and reports the shape of the latency, not just the mean: a p99
// that falls over under a thundering herd is the failure A7 is looking for.
//
// It drives the real endpoint with the real wire DTOs (referenced from
// Contracts), authenticating as a provisioned registrar and uploading for an
// enrolled device — the same path a device takes. Records carry synthetic BRNs
// in a high range that no granted block covers, so they persist as unconfirmed
// registrations (a real write) without drawing down a facility's block or
// colliding with anything; each batch gets a fresh transaction id so the
// idempotency layer treats it as new work rather than replaying one answer.
//
// This measures whatever deployment it is pointed at. Run against the dev box
// it measures SQLite, which serialises writes — a floor, not the A7 figure.
// The number that goes to a Steering Committee is this same tool against
// Postgres at projected national volume (see README).

var cfg = Config.FromEnvironment(args);
Console.WriteLine($"""
    NCBRS load driver (WS-A7)
      API            {cfg.ApiBase}
      facility       {cfg.FacilityId}
      devices        {(cfg.Devices <= 1 ? cfg.DeviceId : $"{cfg.Devices} enrolled per run (fleet)")}
      signing        {(cfg.Sign ? "on — each batch signed with its device key" : "off")}
      concurrency    {cfg.Concurrency} in-flight batches
      batch size     {cfg.BatchSize} records
      measured       {cfg.Batches} batches  ({cfg.Batches * cfg.BatchSize} records)
      warmup         {cfg.Warmup} batches
    """);

var webJson = new JsonSerializerOptions(JsonSerializerDefaults.Web)
{
    Converters = { new JsonStringEnumConverter() },
};

using var handler = new SocketsHttpHandler
{
    // A burst needs the connections to actually open in parallel, not queue
    // behind the default per-server cap.
    MaxConnectionsPerServer = Math.Max(cfg.Concurrency, 16),
    PooledConnectionLifetime = TimeSpan.FromMinutes(10),
};
using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(100) };

Console.Write("Authenticating… ");
var token = await GetTokenAsync(http, cfg);
http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
Console.WriteLine("ok");

var runToken = Guid.NewGuid().ToString("N")[..8];
var batchUri = new Uri(new Uri(cfg.ApiBase), "api/sync/batches");
var enrolUri = new Uri(new Uri(cfg.ApiBase), "api/devices");

// The device fleet the load is spread across. A real burst is many *distinct*
// posts uploading at once; pinning every concurrent batch to one device would
// serialise them all on that one Device row's last-seen update — measuring
// row-lock contention, not the system. With Devices > 1 the driver enrols a
// fleet of fresh devices up front and round-robins batches across them.
FleetDevice[] fleet;
if (cfg.Devices <= 1)
{
    if (cfg.Sign)
    {
        // The seeded device's private key was discarded when it was enrolled,
        // exactly as a real device keeps its own. Nothing here can sign as it.
        throw new InvalidOperationException(
            "Signing needs a device key this process holds. Use fleet mode (NCBRS_LOAD_DEVICES > 1), which "
            + "enrols devices with fresh keys, or set NCBRS_LOAD_SIGN=false for the single seeded device.");
    }

    fleet = [new FleetDevice(cfg.DeviceId, null)];
}
else
{
    Console.Write($"Enrolling {cfg.Devices} devices… ");
    fleet = await EnrolFleetAsync(http, cfg, enrolUri, runToken, webJson);
    Console.WriteLine($"ok ({fleet.Length} enrolled)");
}

var deviceCursor = -1;
FleetDevice NextDevice() => fleet[(uint)Interlocked.Increment(ref deviceCursor) % fleet.Length];

// Start each run at a fresh random point in the synthetic range, so re-running
// the tool doesn't replay BRNs already on file (which the centre would, rightly,
// report as duplicates rather than new registrations).
var brnCounter = cfg.BrnBase + Random.Shared.NextInt64(0, 80_000_000) * 1_000;

// A recent, in-window birth date base (well inside the 90-day statutory
// window, so records don't route through late-registration).
var dobBase = DateTime.UtcNow.Date.AddDays(-88);

ApiRequest<SyncBatchRequest> BuildBatch(string deviceId)
{
    var records = new SyncBirthRecord[cfg.BatchSize];
    for (var i = 0; i < cfg.BatchSize; i++)
    {
        var n = Interlocked.Increment(ref brnCounter);
        // Names and sexes the duplicate matcher cannot confuse -- see
        // SyntheticBirths for why, and for what the first version of this got
        // wrong. Dates stay spread evenly over 80 days, so the matcher's
        // date-window scan sees realistic density: each record is compared
        // against its neighbours and simply flags none of them.
        records[i] = new SyncBirthRecord
        {
            Birth = new RegisterBirthRequest
            {
                Brn = n.ToString(),
                FacilityId = cfg.FacilityId,
                DeviceId = deviceId,
                ChildFullName = SyntheticBirths.ChildName(Random.Shared),
                DateOfBirth = dobBase.AddDays(n % 80),
                Sex = SyntheticBirths.ChildSex(Random.Shared),
                BirthWeightGrams = 3200,
                GestationalAgeWeeks = 39.5m,
                Plurality = BirthPlurality.Singleton,
                BirthOrder = 1,
            },
        };
    }

    return new ApiRequest<SyncBatchRequest>
    {
        Data = new SyncBatchRequest
        {
            DeviceId = deviceId,
            FacilityId = cfg.FacilityId,
            Records = records,
        },
    };
}

async Task<(bool Ok, double Ms, int Registered, int Rejected, int Duplicates, string? Error)> SendOneAsync()
{
    var device = NextDevice();

    // Serialised once, and these exact bytes are what is signed and what is
    // sent. The centre verifies the raw request body byte for byte, so
    // re-serialising after signing -- or letting the HTTP layer do it -- would
    // produce a body the signature does not cover.
    var payload = JsonSerializer.SerializeToUtf8Bytes(BuildBatch(device.DeviceId), webJson);
    var content = new ByteArrayContent(payload);
    content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

    var request = new HttpRequestMessage(HttpMethod.Post, batchUri) { Content = content };

    if (cfg.Sign)
    {
        request.Headers.TryAddWithoutValidation(DeviceSignature.HeaderName, DeviceSignature.Sign(device.PrivateKeyPem!, payload));
    }
    // A fresh transaction id per batch: this is new work, not a replay of an
    // earlier answer.
    request.Headers.TryAddWithoutValidation(TransactionIdHeader, Guid.NewGuid().ToString());

    var sw = Stopwatch.StartNew();
    try
    {
        using var response = await http.SendAsync(request);
        sw.Stop();
        if (!response.IsSuccessStatusCode)
        {
            return (false, sw.Elapsed.TotalMilliseconds, 0, 0, 0, $"HTTP {(int)response.StatusCode}");
        }

        // Every response is wrapped in the meta envelope (MetaEnvelopeFilter):
        // { meta, data: <SyncBatchResponse> }.
        var body = (await response.Content.ReadFromJsonAsync<ApiResponse<SyncBatchResponse>>(webJson))?.Data;
        return (true, sw.Elapsed.TotalMilliseconds, body?.Registered ?? 0, body?.Rejected ?? 0, body?.Duplicates ?? 0, null);
    }
    catch (Exception ex)
    {
        sw.Stop();
        return (false, sw.Elapsed.TotalMilliseconds, 0, 0, 0, ex.GetType().Name);
    }
}

async Task RunPhaseAsync(int count, bool measured, ConcurrentBag<double> latencies, Counters counters)
{
    await Parallel.ForEachAsync(
        Enumerable.Range(0, count),
        new ParallelOptions { MaxDegreeOfParallelism = cfg.Concurrency },
        async (_, _) =>
        {
            var result = await SendOneAsync();
            if (!measured)
            {
                return;
            }

            latencies.Add(result.Ms);
            if (result.Ok)
            {
                Interlocked.Increment(ref counters.OkBatches);
                Interlocked.Add(ref counters.Registered, result.Registered);
                Interlocked.Add(ref counters.Rejected, result.Rejected);
                Interlocked.Add(ref counters.Duplicates, result.Duplicates);
            }
            else
            {
                Interlocked.Increment(ref counters.FailedBatches);
                counters.Errors.AddOrUpdate(result.Error ?? "unknown", 1, (_, n) => n + 1);
            }
        });
}

if (cfg.Warmup > 0)
{
    Console.Write($"Warming up ({cfg.Warmup} batches)… ");
    await RunPhaseAsync(cfg.Warmup, measured: false, [], new Counters());
    Console.WriteLine("ok");
}

Console.WriteLine("Running measured phase…");
var lat = new ConcurrentBag<double>();
var ctr = new Counters();
var wall = Stopwatch.StartNew();
await RunPhaseAsync(cfg.Batches, measured: true, lat, ctr);
wall.Stop();

Report(cfg, wall.Elapsed, lat, ctr);
return ctr.FailedBatches == 0 ? 0 : 1;

// Enrol a fleet of fresh devices at the target facility. Only the public half
// is sent; the private half stays in this process, which is what lets the
// driver sign as a real device does. Needs a CanEnrolDevices identity (district
// officer or ministry admin) -- the same token used to sync.
static async Task<FleetDevice[]> EnrolFleetAsync(
    HttpClient http, Config cfg, Uri enrolUri, string runToken, JsonSerializerOptions json)
{
    var fleet = new FleetDevice[cfg.Devices];
    for (var i = 0; i < cfg.Devices; i++)
    {
        var deviceId = $"LOADTEST-{runToken}-{i:D3}";
        var (privateKeyPem, publicKeyPem) = DeviceSignature.GenerateKeyPair();

        var body = new ApiRequest<EnrolDeviceRequest>
        {
            Data = new EnrolDeviceRequest
            {
                DeviceId = deviceId,
                FacilityId = cfg.FacilityId,
                PublicKeyPem = publicKeyPem,
                Label = $"A7 load driver, run {runToken}",
            },
        };

        var request = new HttpRequestMessage(HttpMethod.Post, enrolUri)
        {
            Content = JsonContent.Create(body, options: json),
        };
        request.Headers.TryAddWithoutValidation("X-Transaction-Id", Guid.NewGuid().ToString());

        using var response = await http.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            var detail = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException(
                $"Enrolling '{deviceId}' failed: HTTP {(int)response.StatusCode}. The sync identity must have "
                + $"CanEnrolDevices (district officer / ministry admin). Response: {detail}");
        }

        fleet[i] = new FleetDevice(deviceId, privateKeyPem);
    }

    return fleet;
}

static async Task<string> GetTokenAsync(HttpClient http, Config cfg)
{
    using var response = await http.PostAsync(cfg.TokenUrl, new FormUrlEncodedContent(new Dictionary<string, string>
    {
        ["grant_type"] = "password",
        ["client_id"] = cfg.ClientId,
        ["username"] = cfg.Username,
        ["password"] = cfg.Password,
    }));

    var json = await response.Content.ReadAsStringAsync();
    if (!response.IsSuccessStatusCode)
    {
        throw new InvalidOperationException(
            $"Token request failed: HTTP {(int)response.StatusCode}. Is Keycloak up at {cfg.TokenUrl}? Response: {json}");
    }

    using var doc = JsonDocument.Parse(json);
    return doc.RootElement.GetProperty("access_token").GetString()
        ?? throw new InvalidOperationException("No access_token in the token response.");
}

static void Report(Config cfg, TimeSpan wall, ConcurrentBag<double> latencies, Counters ctr)
{
    var sorted = latencies.OrderBy(x => x).ToArray();
    var totalBatches = ctr.OkBatches + ctr.FailedBatches;
    var seconds = wall.TotalSeconds <= 0 ? 1e-9 : wall.TotalSeconds;

    double Pct(double p)
    {
        if (sorted.Length == 0)
        {
            return 0;
        }

        var rank = (int)Math.Ceiling(p / 100.0 * sorted.Length) - 1;
        return sorted[Math.Clamp(rank, 0, sorted.Length - 1)];
    }

    Console.WriteLine($"""

        === Results (measured phase) ===
        Wall clock            {wall.TotalSeconds,8:F2} s
        Batches               {totalBatches,8}  ({ctr.OkBatches} ok, {ctr.FailedBatches} failed)
        Records registered    {ctr.Registered,8}
          rejected            {ctr.Rejected,8}
          duplicates          {ctr.Duplicates,8}
        Throughput            {totalBatches / seconds,8:F1} batches/s
                              {(ctr.Registered + ctr.Rejected + ctr.Duplicates) / seconds,8:F1} records/s
        Batch latency (ms)    mean {(sorted.Length > 0 ? sorted.Average() : 0),8:F1}
                              p50  {Pct(50),8:F1}
                              p90  {Pct(90),8:F1}
                              p95  {Pct(95),8:F1}
                              p99  {Pct(99),8:F1}
                              max  {(sorted.Length > 0 ? sorted[^1] : 0),8:F1}
        """);

    if (!ctr.Errors.IsEmpty)
    {
        Console.WriteLine("Errors:");
        foreach (var (kind, n) in ctr.Errors.OrderByDescending(e => e.Value))
        {
            Console.WriteLine($"  {n,6}  {kind}");
        }
    }

    if (ctr.FailedBatches > 0)
    {
        Console.WriteLine("\nExit 1: some batches failed. Check the API/Keycloak are up and the registrar/device/facility line up.");
    }
}

internal sealed class Counters
{
    public int OkBatches;
    public int FailedBatches;
    public int Registered;
    public int Rejected;
    public int Duplicates;
    public ConcurrentDictionary<string, int> Errors { get; } = new();
}

internal sealed record Config(
    string ApiBase,
    string TokenUrl,
    string ClientId,
    string Username,
    string Password,
    Guid FacilityId,
    string DeviceId,
    int Devices,
    bool Sign,
    int Concurrency,
    int BatchSize,
    int Batches,
    int Warmup,
    long BrnBase)
{
    // Dev defaults line up with the Development seed and imported Keycloak realm,
    // so the tool runs against a local dev stack with no arguments. Override any
    // of these with the matching NCBRS_LOAD_* environment variable to point at
    // staging or a Postgres tier.
    public static Config FromEnvironment(string[] args)
    {
        string S(string key, string fallback) =>
            Environment.GetEnvironmentVariable($"NCBRS_LOAD_{key}") is { Length: > 0 } v ? v : fallback;
        int I(string key, int fallback) => int.TryParse(S(key, ""), out var v) ? v : fallback;
        long L(string key, long fallback) => long.TryParse(S(key, ""), out var v) ? v : fallback;

        return new Config(
            ApiBase: S("API_BASE", "http://localhost:5259/"),
            TokenUrl: S("TOKEN_URL", "http://localhost:8080/realms/ncbrs/protocol/openid-connect/token"),
            ClientId: S("CLIENT_ID", "ncbrs-device"),
            Username: S("USERNAME", "nurse.lado"),
            Password: S("PASSWORD", "password"),
            FacilityId: Guid.TryParse(S("FACILITY_ID", ""), out var f) ? f : new Guid("0199c000-0000-7000-8000-0000000f0001"),
            DeviceId: S("DEVICE_ID", "TERMINAL-JUBA-01"),
            // A fleet spreads the burst across distinct devices, as a real region
            // does; 1 keeps the single seeded device (and hits its row-lock ceiling).
            Devices: I("DEVICES", 32),
            // On by default wherever the driver holds device keys (fleet mode),
            // so load tests run the production path. Note a server that does not
            // enforce signatures accepts batches without verifying them -- a clean
            // run proves the signing path only against RequireSignature: true.
            Sign: bool.TryParse(S("SIGN", ""), out var sign) ? sign : I("DEVICES", 32) > 1,
            Concurrency: I("CONCURRENCY", 16),
            BatchSize: I("BATCH_SIZE", 25),
            Batches: I("BATCHES", 200),
            Warmup: I("WARMUP", 20),
            // Well above any facility's 100k-wide block, so records persist as
            // unconfirmed without drawing from a real block.
            BrnBase: L("BRN_BASE", 9_000_000_000));
    }
}

/// <summary>A device the run uploads as. The key is null for the single seeded device.</summary>
internal sealed record FleetDevice(string DeviceId, string? PrivateKeyPem);
