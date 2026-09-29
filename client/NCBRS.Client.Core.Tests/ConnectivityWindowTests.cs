using System.Net;
using System.Text.Json;
using NCBRS.Client.Network;
using Xunit;
using static NCBRS.Client.Tests.CentralClientTests;

namespace NCBRS.Client.Tests;

/// <summary>
/// One connectivity window: births first, then numbers, then the verification
/// bundle; state persisted before anything is sent; an unsettled upload kept
/// byte for byte for the next window.
/// </summary>
public class ConnectivityWindowTests
{
    private static readonly DateTime Now = new(2026, 9, 28, 9, 0, 0, DateTimeKind.Utc);

    private sealed class Store
    {
        public List<Guid?> InFlightAtEachSave { get; } = [];

        public Task Persist(ClientSyncState state, CancellationToken _)
        {
            InFlightAtEachSave.Add(state.InFlight?.TransactionId);
            return Task.CompletedTask;
        }
    }

    private static readonly string Bundle = JsonSerializer.Serialize(new
    {
        keyId = "moh-2026",
        algorithm = "ECDSA-P256-SHA256",
        publicKeyPem = "-----BEGIN CERTIFICATE-----",
        revocations = new
        {
            issuer = "NCBRS", version = "v1", keyId = "moh-2026", since = (DateTime?)null,
            issuedAtUtc = Now, nextUpdateUtc = Now.AddDays(7), count = 0, entries = Array.Empty<object>(), signature = "sig",
        },
        keys = new[] { new { keyId = "moh-2026", publicKeyPem = "-----BEGIN CERTIFICATE-----", active = true } },
        // As the registry sends it today. Without one, every window asks again
        // (SealedExportTests), which is right, but is not "nothing to do".
        transferKey = new { keyId = "moh-transfer-2026", algorithm = "ECDH-P256+HKDF-SHA256+AES-256-GCM", publicKeyPem = "-----BEGIN PUBLIC KEY-----" },
    });

    /// <summary>Answers each path the way the centre would.</summary>
    private static Func<HttpRequestMessage, HttpResponseMessage> Centre(Func<byte[], HttpResponseMessage>? sync = null)
        => request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/api/Sync/batches", StringComparison.Ordinal))
            {
                var body = request.Content!.ReadAsByteArrayAsync().Result;
                return sync?.Invoke(body) ?? RegisterAll(body);
            }

            if (path.EndsWith("/request-brn-block", StringComparison.Ordinal))
            {
                return FakeNetwork.Envelope("""{"facilityId":"0199c000-0000-7000-8000-0000000f0003","blockStart":400200,"blockEnd":400399}""");
            }

            return FakeNetwork.Envelope(Bundle);
        };

    private static HttpResponseMessage RegisterAll(byte[] body)
    {
        using var sent = JsonDocument.Parse(body);
        var brns = sent.RootElement.GetProperty("data").GetProperty("records").EnumerateArray()
            .Select(record => (record.GetProperty("birth").GetProperty("brn").GetString()!, "Registered")).ToArray();
        return FakeNetwork.Envelope(BatchResponse(brns));
    }

    private static int Records(byte[] body)
    {
        using var sent = JsonDocument.Parse(body);
        return sent.RootElement.GetProperty("data").GetProperty("records").GetArrayLength();
    }

    /// <summary>
    /// If the app dies or the link drops mid-request, the next window must find
    /// the upload it was sending — so it is saved before the request goes out.
    /// </summary>
    [Fact]
    public async Task TheUploadIsPersistedBeforeItIsSent()
    {
        var (facility, signer) = Facility_();
        facility.RegisterBirth(Birth());
        var store = new Store();
        var network = new FakeNetwork { Respond = Centre() };
        network.OnSend = request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/api/Sync/batches", StringComparison.Ordinal))
            {
                Assert.NotNull(store.InFlightAtEachSave.LastOrDefault());
            }
        };

        var report = await new ConnectivityWindow(facility, CentreClient(network), signer, store.Persist).RunAsync(new ClientSyncState(), Now);

        Assert.Equal(CentralOutcome.Succeeded, report.Upload);
        Assert.Equal(0, facility.PendingCount);
    }

    /// <summary>
    /// The upload in flight goes first, byte for byte; then one upload of what
    /// was registered since. Settled records leave the outbox.
    /// </summary>
    [Fact]
    public async Task TheUploadInFlightGoesFirstThenWhatWasRegisteredSince()
    {
        var (facility, signer) = Facility_();
        facility.RegisterBirth(Birth("Ayen Deng"));
        var state = new ClientSyncState { InFlight = facility.BuildSignedUpload() };
        var inFlight = state.InFlight.Body;
        facility.RegisterBirth(Birth("Deng Majok"));   // after the upload was built
        var network = new FakeNetwork { Respond = Centre() };

        var report = await new ConnectivityWindow(facility, CentreClient(network), signer, new Store().Persist).RunAsync(state, Now);

        var uploads = network.Requests.Where(r => r.Request.RequestUri!.AbsolutePath.EndsWith("/api/Sync/batches")).ToList();
        Assert.Equal(2, uploads.Count);
        Assert.Equal(inFlight, uploads[0].Body);
        Assert.Equal(1, Records(uploads[1].Body));
        Assert.Equal(2, report.Settlements.Count);
        Assert.Equal(0, facility.PendingCount);
        Assert.Null(state.InFlight);
    }

    /// <summary>
    /// Held at a District node, the upload is kept and the next window sends
    /// exactly the same bytes: the node answers with the centre's result once
    /// it has one, and a family is not told anything is confirmed meanwhile.
    /// </summary>
    [Fact]
    public async Task HeldAtTheDistrictTheSameBytesGoAgainNextWindow()
    {
        var (facility, signer) = Facility_();
        facility.RegisterBirth(Birth());
        var state = new ClientSyncState();
        var network = new FakeNetwork
        {
            Respond = Centre(_ => FakeNetwork.FromDistrict("Queued", null, HttpStatusCode.Accepted, "Central tier unreachable")),
        };
        var window = new ConnectivityWindow(facility, CentreClient(network), signer, new Store().Persist);

        var first = await window.RunAsync(state, Now);
        var held = state.InFlight;
        network.Respond = Centre();
        var second = await window.RunAsync(state, Now);

        Assert.Equal(CentralOutcome.Held, first.Upload);
        Assert.Contains(first.Problems, problem => problem.Contains("not tell the family"));
        Assert.Equal(held!.Body, network.Requests.Last(r => r.Request.RequestUri!.AbsolutePath.EndsWith("/api/Sync/batches")).Body);
        Assert.Equal(CentralOutcome.Succeeded, second.Upload);
        Assert.Equal(0, facility.PendingCount);
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task AnUnansweredUploadIsKeptAsItIs(HttpStatusCode status)
    {
        var (facility, signer) = Facility_();
        facility.RegisterBirth(Birth());
        var state = new ClientSyncState();
        var network = new FakeNetwork { Respond = Centre(_ => new HttpResponseMessage(status)) };

        await new ConnectivityWindow(facility, CentreClient(network), signer, new Store().Persist).RunAsync(state, Now);

        Assert.NotNull(state.InFlight);
        Assert.Equal(1, facility.PendingCount);
    }

    /// <summary>
    /// A refused upload will be refused again byte for byte, so it is dropped —
    /// but its records stay queued: nothing leaves the outbox without the
    /// centre's answer for that record.
    /// </summary>
    [Fact]
    public async Task ARefusedUploadIsDroppedButItsRecordsAreKept()
    {
        var (facility, signer) = Facility_();
        facility.RegisterBirth(Birth());
        var state = new ClientSyncState();
        var network = new FakeNetwork
        {
            Respond = Centre(_ => FakeNetwork.Envelope("""{"status":403,"title":"Device refused.","errors":[{"field":"deviceId","message":"Not enrolled at this facility."}]}""", HttpStatusCode.Forbidden)),
        };

        var report = await new ConnectivityWindow(facility, CentreClient(network), signer, new Store().Persist).RunAsync(state, Now);

        Assert.Null(state.InFlight);
        Assert.Equal(1, facility.PendingCount);
        Assert.Contains(report.Problems, problem => problem.Contains("Not enrolled at this facility"));
    }

    /// <summary>Births first, then numbers, then the bundle — the order that matters on a short window.</summary>
    [Fact]
    public async Task BirthsThenNumbersThenTheBundle()
    {
        var (facility, signer) = Facility_(blockStart: 400_000, blockEnd: 400_001);
        facility.RegisterBirth(Birth());   // one number left: low
        var state = new ClientSyncState();
        var network = new FakeNetwork { Respond = Centre() };

        var report = await new ConnectivityWindow(facility, CentreClient(network), signer, new Store().Persist).RunAsync(state, Now);

        var paths = network.Requests.Select(r => r.Request.RequestUri!.AbsolutePath).ToList();
        Assert.EndsWith("/api/Sync/batches", paths[0]);
        Assert.EndsWith("/request-brn-block", paths[1]);
        Assert.EndsWith("/offline-bundle", paths[2]);

        Assert.Equal(400_200, report.BlockGranted!.BlockStart);
        Assert.False(facility.NeedsMoreNumbers);
        Assert.True(report.BundleRefreshed);
        Assert.True(state.Bundle.HasBundle);
        Assert.False(state.Bundle.RefreshDue(Now));
    }

    [Fact]
    public async Task NothingToDoSendsNothing()
    {
        var (facility, signer) = Facility_();
        var network = new FakeNetwork { Respond = Centre() };
        var state = new ClientSyncState();
        await new ConnectivityWindow(facility, CentreClient(network), signer, new Store().Persist).RunAsync(state, Now);   // fetches the bundle
        network.Requests.Clear();

        var report = await new ConnectivityWindow(facility, CentreClient(network), signer, new Store().Persist).RunAsync(state, Now);

        Assert.Null(report.Upload);
        Assert.Empty(network.Requests);
    }
}
