using System.Net;
using System.Text;
using System.Text.Json;
using NCBRS.Client.Brn;
using NCBRS.Client.Network;
using NCBRS.Client.Sync;
using NCBRS.Devices;
using NCBRS.Models;
using Xunit;

namespace NCBRS.Client.Tests;

/// <summary>
/// The device's connection to the centre: the requests it makes, byte for
/// byte, and how it reads every answer the centre or a District node can give.
/// The end-to-end proof against the real services is the TLS rehearsal.
/// </summary>
public class CentralClientTests
{
    private const string Device = "TABLET-TEREKEKA-01";
    private static readonly Guid Facility = Guid.Parse("0199c000-0000-7000-8000-0000000f0003");
    private static readonly Uri Centre = new("https://registry.ncbrs.ss/");
    private static readonly Uri District = new("http://district-terekeka.lan:8080/");

    /// <summary>Plays the centre and the District node; records every request.</summary>
    internal sealed class FakeNetwork : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, byte[] Body)> Requests { get; } = [];

        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = _ => Envelope("{}");

        public Action<HttpRequestMessage>? OnSend { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            Requests.Add((request, body));
            OnSend?.Invoke(request);
            return Respond(request);
        }

        /// <summary>The centre's <c>{ meta, data }</c> answer.</summary>
        public static HttpResponseMessage Envelope(string data, HttpStatusCode status = HttpStatusCode.OK)
            => new(status)
            {
                Content = new StringContent(
                    $$"""{"meta":{"transactionId":"{{Guid.NewGuid()}}","clientId":null,"transactionIdGenerated":false,"timestampUtc":"2026-09-28T09:00:00Z"},"data":{{data}}}""",
                    Encoding.UTF8, "application/json"),
            };

        /// <summary>A District node's answer about a batch.</summary>
        public static HttpResponseMessage FromDistrict(string status, string? central, HttpStatusCode code, string? lastError = null)
            => new(code)
            {
                Content = new StringContent(
                    $$"""{"transactionId":"{{Guid.NewGuid()}}","status":"{{status}}","recordCount":1,"receivedAtUtc":"2026-09-28T09:00:00Z","forwardedAtUtc":null,"attempts":1,"central":{{central ?? "null"}},"lastError":{{(lastError is null ? "null" : JsonSerializer.Serialize(lastError))}}}""",
                    Encoding.UTF8, "application/json"),
            };
    }

    internal static string BatchResponse(params (string Brn, string Status)[] records)
        => JsonSerializer.Serialize(new
        {
            syncBatchId = Guid.NewGuid(),
            status = "Reconciled",
            submitted = records.Length,
            registered = records.Count(r => r.Status == "Registered"),
            duplicates = records.Count(r => r.Status == "Duplicate"),
            rejected = records.Count(r => r.Status == "Rejected"),
            records = records.Select(r => new { brn = r.Brn, status = r.Status, brnConfirmed = true }),
        });

    internal static (FacilityClient Facility, DeviceSigner Signer) Facility_(long blockStart = 400_000, long blockEnd = 400_199)
    {
        var signer = DeviceSigner.Generate();
        return (new FacilityClient(Device, Facility, new DeviceBrnAllocator(Device, blockStart, blockEnd),
            new SyncOutbox(Device, Facility), signer, lowBlockThreshold: 2), signer);
    }

    internal static RegisterBirthRequest Birth(string name = "Ayen Deng") => new()
    {
        ChildFullName = name,
        Sex = Sex.Female,
        DateOfBirth = new DateTime(2026, 9, 20, 0, 0, 0, DateTimeKind.Utc),
    };

    internal static CentralClient CentreClient(FakeNetwork network, string? token = "registrar-token", Uri? syncVia = null)
        => new(new HttpClient(network), new CentralEndpoints(Centre, syncVia), _ => Task.FromResult(token));

    private static string Header(HttpRequestMessage request, string name)
        => request.Headers.TryGetValues(name, out var values) ? values.Single() : "";

    // --- what the device sends ---------------------------------------------------------------

    /// <summary>
    /// The exact bytes, signed; the transaction id in the header matching the
    /// one in the body; the registrar's token. This is the request the centre
    /// refused as "data is required" until the envelope was added.
    /// </summary>
    [Fact]
    public async Task AnUploadIsTheEnvelopedBatchSignedAndNamed()
    {
        var (facility, signer) = Facility_();
        facility.RegisterBirth(Birth());
        var upload = facility.BuildSignedUpload();
        var network = new FakeNetwork { Respond = _ => FakeNetwork.Envelope(BatchResponse(("400000", "Registered"))) };

        await CentreClient(network).UploadAsync(upload);

        var (request, body) = Assert.Single(network.Requests);
        Assert.Equal(new Uri(Centre, "api/Sync/batches"), request.RequestUri);
        Assert.Equal(upload.Body, body);
        Assert.True(DeviceSignature.Verify(signer.PublicKeyPem, body, Header(request, DeviceSignature.HeaderName)).Valid);
        Assert.Equal(upload.TransactionId.ToString(), Header(request, CentralClient.TransactionIdHeader));
        Assert.Equal("Bearer registrar-token", request.Headers.Authorization!.ToString());

        using var sent = JsonDocument.Parse(body);
        Assert.Equal(upload.TransactionId, sent.RootElement.GetProperty("meta").GetProperty("transactionId").GetGuid());
        var birth = sent.RootElement.GetProperty("data").GetProperty("records")[0].GetProperty("birth");
        Assert.Equal("Female", birth.GetProperty("sex").GetString());   // enums as names, as the API documents them
    }

    /// <summary>A District node carries sync batches and nothing else.</summary>
    [Fact]
    public async Task OnlyUploadsGoThroughTheDistrictNode()
    {
        var (facility, signer) = Facility_();
        facility.RegisterBirth(Birth());
        var network = new FakeNetwork();
        var client = CentreClient(network, syncVia: District);

        await client.UploadAsync(facility.BuildSignedUpload());
        await client.RequestBrnBlockAsync(Facility, Device, signer);
        await client.FetchOfflineBundleAsync();

        Assert.Equal(District.Host, network.Requests[0].Request.RequestUri!.Host);
        Assert.Equal(Centre.Host, network.Requests[1].Request.RequestUri!.Host);
        Assert.Equal(Centre.Host, network.Requests[2].Request.RequestUri!.Host);
    }

    [Fact]
    public async Task ABrnBlockRequestIsSignedOverTheExactBytesSent()
    {
        var (_, signer) = Facility_();
        var network = new FakeNetwork
        {
            Respond = _ => FakeNetwork.Envelope($$"""{"facilityId":"{{Facility}}","blockStart":400200,"blockEnd":400399}"""),
        };

        var result = await CentreClient(network).RequestBrnBlockAsync(Facility, Device, signer, blockSize: 200);

        Assert.True(result.Succeeded);
        Assert.Equal(400_200, result.Value!.BlockStart);
        var (request, body) = Assert.Single(network.Requests);
        Assert.Equal(new Uri(Centre, $"api/BirthRecords/{Facility}/request-brn-block"), request.RequestUri);
        Assert.True(DeviceSignature.Verify(signer.PublicKeyPem, body, Header(request, DeviceSignature.HeaderName)).Valid);
        using var sent = JsonDocument.Parse(body);
        Assert.Equal(Device, sent.RootElement.GetProperty("data").GetProperty("deviceId").GetString());
        Assert.True(sent.RootElement.GetProperty("meta").TryGetProperty("transactionId", out _));
    }

    /// <summary>The verification bundle names nobody, so it needs no sign-in.</summary>
    [Fact]
    public async Task TheBundleIsFetchedWithoutASignIn()
    {
        var network = new FakeNetwork();

        await CentreClient(network, token: null).FetchOfflineBundleAsync();

        Assert.Null(Assert.Single(network.Requests).Request.Headers.Authorization);
    }

    [Fact]
    public async Task WithNobodySignedInAWriteIsNotSentAtAll()
    {
        var (facility, _) = Facility_();
        facility.RegisterBirth(Birth());
        var network = new FakeNetwork();

        var result = await CentreClient(network, token: null).UploadAsync(facility.BuildSignedUpload());

        Assert.Equal(CentralOutcome.Unauthorized, result.Outcome);
        Assert.Empty(network.Requests);
    }

    // --- how every answer is read --------------------------------------------------------------

    public static TheoryData<string, Func<HttpResponseMessage>, CentralOutcome> Answers => new()
    {
        { "centre registered it", () => FakeNetwork.Envelope(BatchResponse(("400000", "Registered"))), CentralOutcome.Succeeded },
        { "District forwarded it", () => FakeNetwork.FromDistrict("Forwarded", "{\"meta\":{},\"data\":" + BatchResponse(("400000", "Registered")) + "}", HttpStatusCode.OK), CentralOutcome.Succeeded },
        { "District is holding it", () => FakeNetwork.FromDistrict("Queued", null, HttpStatusCode.Accepted, "Central tier unreachable"), CentralOutcome.Held },
        { "District says the centre refused it", () => FakeNetwork.FromDistrict("Rejected", "{\"meta\":{},\"data\":{\"status\":403,\"title\":\"Device refused.\",\"errors\":[{\"field\":\"deviceId\",\"message\":\"Not enrolled.\"}]}}", HttpStatusCode.OK), CentralOutcome.Refused },
        { "already in progress", () => WithRetryAfter(FakeNetwork.Envelope("{\"status\":409,\"title\":\"In progress.\",\"errors\":[]}", HttpStatusCode.Conflict)), CentralOutcome.InProgress },
        { "a conflict to act on", () => FakeNetwork.Envelope("{\"status\":409,\"title\":\"Conflict.\",\"errors\":[]}", HttpStatusCode.Conflict), CentralOutcome.Refused },
        { "validation failed", () => FakeNetwork.Envelope("{\"status\":400,\"title\":\"Validation failed.\",\"errors\":[{\"field\":\"data\",\"message\":\"data is required.\"}]}", HttpStatusCode.BadRequest), CentralOutcome.Refused },
        { "device refused", () => FakeNetwork.Envelope("{\"status\":403,\"title\":\"Device refused.\",\"errors\":[]}", HttpStatusCode.Forbidden), CentralOutcome.Refused },
        { "sign-in not accepted", () => new HttpResponseMessage(HttpStatusCode.Unauthorized), CentralOutcome.Unauthorized },
        { "centre down", () => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable), CentralOutcome.Unreachable },
        { "slow down", () => WithRetryAfter(new HttpResponseMessage(HttpStatusCode.TooManyRequests)), CentralOutcome.Unreachable },
    };

    private static HttpResponseMessage WithRetryAfter(HttpResponseMessage response)
    {
        response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
        return response;
    }

    [Theory]
    [MemberData(nameof(Answers))]
    public async Task EveryAnswerIsReadAsWhatTheDeviceMustDo(string answer, Func<HttpResponseMessage> respond, CentralOutcome expected)
    {
        var (facility, _) = Facility_();
        facility.RegisterBirth(Birth());
        var network = new FakeNetwork { Respond = _ => respond() };

        var result = await CentreClient(network).UploadAsync(facility.BuildSignedUpload());

        Assert.True(expected == result.Outcome, $"{answer}: expected {expected}, got {result.Outcome}");
        if (expected == CentralOutcome.Succeeded)
        {
            Assert.Equal(SyncRecordStatus.Registered, Assert.Single(result.Value!.Records).Status);
        }
    }

    [Fact]
    public async Task ARefusalCarriesTheCentresReasons()
    {
        var (facility, _) = Facility_();
        facility.RegisterBirth(Birth());
        var network = new FakeNetwork
        {
            Respond = _ => FakeNetwork.Envelope(
                "{\"status\":400,\"title\":\"Validation failed.\",\"errors\":[{\"field\":\"data\",\"message\":\"data is required.\"}]}",
                HttpStatusCode.BadRequest),
        };

        var result = await CentreClient(network).UploadAsync(facility.BuildSignedUpload());

        var error = Assert.Single(result.Errors!);
        Assert.Equal("data", error.Field);
    }

    [Fact]
    public async Task NoAnswerAtAllIsUnreachable()
    {
        var (facility, _) = Facility_();
        facility.RegisterBirth(Birth());
        var network = new FakeNetwork { Respond = _ => throw new HttpRequestException("No route to host") };

        var result = await CentreClient(network).UploadAsync(facility.BuildSignedUpload());

        Assert.Equal(CentralOutcome.Unreachable, result.Outcome);
        Assert.Contains("No route to host", result.Detail);
    }
}
