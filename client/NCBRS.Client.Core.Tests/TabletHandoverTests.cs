using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NCBRS.Client.Network;
using NCBRS.Client.Storage;
using NCBRS.Client.Sync;
using NCBRS.Models;
using Xunit;

namespace NCBRS.Client.Tests;

/// <summary>
/// Handing a tablet over, against a centre that keeps real device records and
/// answers the way DevicesController does: 409 for any id it already holds,
/// whatever its state. The case these exist for was found on the emulator — a
/// tablet revoked from the web app "re-enrolled" into its dead identity,
/// because a 409 was read as success.
/// </summary>
public class TabletHandoverTests
{
    private static readonly Uri Centre = new("https://registry.ncbrs.ss/");
    private static readonly Uri Realm = new("https://id.ncbrs.ss/realms/ncbrs/");
    private static readonly FacilitySummary Juba = new(Guid.Parse("0199c000-0000-7000-8000-0000000f0001"), "Juba Teaching Hospital", FacilityTier.Hospital, "SS0101");
    private static readonly FacilitySummary Munuki = new(Guid.Parse("0199c000-0000-7000-8000-0000000f0002"), "Munuki Primary Health Care Centre", FacilityTier.Clinic, "SS0101");

    /// <summary>The centre's device registry, as far as a handover can see it.</summary>
    private sealed class FakeCentre : HttpMessageHandler
    {
        public Dictionary<string, (Guid Facility, DeviceStatus Status)> Devices { get; } = [];

        public List<string> Calls { get; } = [];

        public HttpStatusCode? RefuseEnrolment { get; set; }

        public HttpStatusCode? StaffAnswer { get; set; }

        public string StaffField { get; set; } = "facilityId";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Calls.Add($"{request.Method} {path}");
            var body = request.Content is null ? null : JsonDocument.Parse(await request.Content.ReadAsStringAsync(cancellationToken));

            if (request.Method == HttpMethod.Post && path == "/api/devices")
            {
                if (RefuseEnrolment is { } refusal)
                {
                    return Error(refusal, "facilityId", "You are not permitted to enrol devices for this facility.");
                }

                var data = body!.RootElement.GetProperty("data");
                var id = data.GetProperty("deviceId").GetString()!;
                if (Devices.TryGetValue(id, out var held))
                {
                    return Error(HttpStatusCode.Conflict, "data.deviceId", $"Device '{id}' is already enrolled ({held.Status}).");
                }

                Devices[id] = (data.GetProperty("facilityId").GetGuid(), DeviceStatus.Enrolled);
                return Device(id);
            }

            if (path.StartsWith("/api/devices/") && path.EndsWith("/revoke"))
            {
                var id = Uri.UnescapeDataString(path["/api/devices/".Length..^"/revoke".Length]);
                if (!Devices.TryGetValue(id, out var held))
                {
                    return Error(HttpStatusCode.NotFound, "deviceId", "No such device.");
                }

                if (held.Status == DeviceStatus.Revoked)
                {
                    return Error(HttpStatusCode.Conflict, "deviceId", $"Device '{id}' was revoked and cannot change status.");
                }

                Devices[id] = (held.Facility, DeviceStatus.Revoked);
                return Device(id);
            }

            if (request.Method == HttpMethod.Get && path.StartsWith("/api/devices/"))
            {
                var id = Uri.UnescapeDataString(path["/api/devices/".Length..]);
                return Devices.ContainsKey(id) ? Device(id) : Error(HttpStatusCode.NotFound, "deviceId", "No such device.");
            }

            if (path.EndsWith("/device-credentials"))
            {
                return StaffAnswer is { } status
                    ? Error(status, StaffField, "Not permitted.")
                    : CentralClientTests.FakeNetwork.Envelope($$"""
                        {"facilityId":"{{Juba.FacilityId}}","facilityName":"{{Juba.Name}}","issuedAtUtc":"2026-09-28T09:00:00Z",
                         "credentials":[{"registrarId":"0199c000-0000-7000-8000-0000000000b1","displayName":"Nurse Lado","role":"FacilityRegistrar","credentialHash":"{{ServerHash("246813")}}"}]}
                        """);
            }

            return Error(HttpStatusCode.NotFound, "path", path);
        }

        private HttpResponseMessage Device(string id)
            => CentralClientTests.FakeNetwork.Envelope($$"""
                {"deviceId":"{{id}}","facilityId":"{{Devices[id].Facility}}","status":"{{Devices[id].Status}}","label":null,
                 "enrolledAtUtc":"2026-09-28T09:00:00Z","lastSeenAtUtc":null,"statusChangedAtUtc":null,"statusReason":"testing"}
                """);

        private static HttpResponseMessage Error(HttpStatusCode status, string field, string message)
            => CentralClientTests.FakeNetwork.Envelope(
                $$"""{"status":{{(int)status}},"title":"Refused.","errors":[{"field":"{{field}}","message":"{{message}}"}]}""", status);

        private static string ServerHash(string pin)
        {
            var salt = RandomNumberGenerator.GetBytes(16);
            var derived = Rfc2898DeriveBytes.Pbkdf2(pin, salt, 1_000, HashAlgorithmName.SHA256, 32);
            return $"pbkdf2-sha256$1000${Convert.ToBase64String(salt)}${Convert.ToBase64String(derived)}";
        }
    }

    private static CentralClient Officer(FakeCentre centre)
        => new(new HttpClient(centre), new CentralEndpoints(Centre), _ => Task.FromResult<string?>("officer-token"));

    private static string IdOf(DeviceState state)
        => DeviceIdentity.IdFor(DeviceSigner.FromPrivateKey(state.DevicePrivateKeyPem!).PublicKeyPem);

    private static Task<HandoverResult> Enrol(FakeCentre centre, DeviceState state, FacilitySummary facility, List<string?>? saves = null)
        => TabletHandover.EnrolAsync(Officer(centre), state, facility, Centre, Realm, null, "Maternity", () =>
        {
            saves?.Add(state.DevicePrivateKeyPem);
            return Task.CompletedTask;
        });

    [Fact]
    public async Task AFreshTabletIsEnrolledAndItsFacilityNamed()
    {
        var centre = new FakeCentre();
        var state = new DeviceState();

        var result = await Enrol(centre, state, Juba);

        Assert.True(result.Enrolled);
        Assert.False(result.Rekeyed);
        Assert.Equal(new DeviceIdentity(IdOf(state), Juba.FacilityId, Centre, null, Realm, Juba.Name), state.Identity);
        Assert.Equal((Juba.FacilityId, DeviceStatus.Enrolled), centre.Devices[IdOf(state)]);
    }

    /// <summary>
    /// The emulator case: the tablet's id was revoked from the web app, the
    /// centre answers 409, and the tablet must not take that as enrolled.
    /// </summary>
    [Fact]
    public async Task ARevokedIdIsNeverTakenAsEnrolledAndTheTabletIsGivenANewKey()
    {
        var centre = new FakeCentre();
        var state = new DeviceState { DevicePrivateKeyPem = DeviceSigner.Generate().ExportPrivateKeyPem() };
        var revokedId = IdOf(state);
        centre.Devices[revokedId] = (Munuki.FacilityId, DeviceStatus.Revoked);

        var result = await Enrol(centre, state, Juba);

        Assert.True(result.Enrolled);
        Assert.True(result.Rekeyed);
        Assert.NotEqual(revokedId, state.Identity!.DeviceId);
        Assert.Equal(IdOf(state), state.Identity.DeviceId);
        Assert.Equal((Juba.FacilityId, DeviceStatus.Enrolled), centre.Devices[state.Identity.DeviceId]);
        Assert.Equal((Munuki.FacilityId, DeviceStatus.Revoked), centre.Devices[revokedId]);
    }

    /// <summary>Live at another facility: withdrawn there first, then enrolled here under a new key. Both acts at the centre.</summary>
    [Fact]
    public async Task AnIdLiveAtAnotherFacilityIsWithdrawnThereBeforeTheTabletIsEnrolledHere()
    {
        var centre = new FakeCentre();
        var state = new DeviceState { DevicePrivateKeyPem = DeviceSigner.Generate().ExportPrivateKeyPem() };
        var oldId = IdOf(state);
        centre.Devices[oldId] = (Munuki.FacilityId, DeviceStatus.Enrolled);

        var result = await Enrol(centre, state, Juba);

        Assert.True(result.Enrolled);
        Assert.Equal(DeviceStatus.Revoked, centre.Devices[oldId].Status);
        Assert.Equal((Juba.FacilityId, DeviceStatus.Enrolled), centre.Devices[state.Identity!.DeviceId]);
        Assert.Contains($"POST /api/devices/{oldId}/revoke", centre.Calls);
    }

    /// <summary>An interrupted handover retried: the same id, enrolled here already. Confirmed by reading it back, not by the 409.</summary>
    [Fact]
    public async Task AnInterruptedHandoverIsConfirmedByReadingTheDeviceBack()
    {
        var centre = new FakeCentre();
        var state = new DeviceState { DevicePrivateKeyPem = DeviceSigner.Generate().ExportPrivateKeyPem() };
        var id = IdOf(state);
        centre.Devices[id] = (Juba.FacilityId, DeviceStatus.Enrolled);

        var result = await Enrol(centre, state, Juba);

        Assert.True(result.Enrolled);
        Assert.False(result.Rekeyed);
        Assert.Equal(id, state.Identity!.DeviceId);
        Assert.Contains($"GET /api/devices/{id}", centre.Calls);
    }

    [Fact]
    public async Task ASuspendedTabletIsNotEnrolledAndKeepsItsKey()
    {
        var centre = new FakeCentre();
        var key = DeviceSigner.Generate().ExportPrivateKeyPem();
        var state = new DeviceState { DevicePrivateKeyPem = key };
        centre.Devices[IdOf(state)] = (Juba.FacilityId, DeviceStatus.Suspended);

        var result = await Enrol(centre, state, Juba);

        Assert.False(result.Enrolled);
        Assert.Contains("suspended", result.Problem);
        Assert.Null(state.Identity);
        Assert.Equal(key, state.DevicePrivateKeyPem);
    }

    [Fact]
    public async Task AnEnrolmentTheCentreRefusesSaysWhy()
    {
        var centre = new FakeCentre { RefuseEnrolment = HttpStatusCode.Forbidden };
        var state = new DeviceState();

        var result = await Enrol(centre, state, Juba);

        Assert.False(result.Enrolled);
        Assert.Contains("not permitted", result.Problem);
        Assert.Null(state.Identity);
    }

    /// <summary>Every new key is saved before it is sent, so an interrupted handover retries as the device it last became.</summary>
    [Fact]
    public async Task ANewKeyIsSavedBeforeItIsSent()
    {
        var centre = new FakeCentre();
        var state = new DeviceState { DevicePrivateKeyPem = DeviceSigner.Generate().ExportPrivateKeyPem() };
        centre.Devices[IdOf(state)] = (Munuki.FacilityId, DeviceStatus.Revoked);
        var saves = new List<string?>();

        await Enrol(centre, state, Juba, saves);

        Assert.Equal(state.DevicePrivateKeyPem, saves[0]);
    }

    // --- handing over again -------------------------------------------------------------------

    [Fact]
    public async Task HandingOverAgainWithdrawsTheDeviceFirst()
    {
        var centre = new FakeCentre();
        centre.Devices["TAB-000000000001"] = (Munuki.FacilityId, DeviceStatus.Enrolled);

        var problem = await TabletHandover.RevokeForHandoverAsync(Officer(centre),
            new DeviceIdentity("TAB-000000000001", Munuki.FacilityId, Centre, null, Realm, Munuki.Name));

        Assert.Null(problem);
        Assert.Equal(DeviceStatus.Revoked, centre.Devices["TAB-000000000001"].Status);
    }

    [Fact]
    public async Task ADeviceRevokedAlreadyIsConfirmedAndMayBeHandedOverAgain()
    {
        var centre = new FakeCentre();
        centre.Devices["TAB-000000000001"] = (Munuki.FacilityId, DeviceStatus.Revoked);

        Assert.Null(await TabletHandover.RevokeForHandoverAsync(Officer(centre),
            new DeviceIdentity("TAB-000000000001", Munuki.FacilityId, Centre)));
    }

    [Fact]
    public async Task ADeviceTheCentreDoesNotHoldIsNotTakenAsWithdrawn()
    {
        var problem = await TabletHandover.RevokeForHandoverAsync(Officer(new FakeCentre()),
            new DeviceIdentity("TAB-000000000009", Munuki.FacilityId, Centre));

        Assert.NotNull(problem);
    }

    // --- the registrar's account ----------------------------------------------------------------

    private static DeviceState Enrolled() => new()
    {
        Identity = new DeviceIdentity("TAB-000000000001", Munuki.FacilityId, Centre, null, Realm, Munuki.Name),
    };

    [Fact]
    public async Task AnAccountAtTheFacilityIsPermittedAndItsStaffProvisioned()
    {
        var state = Enrolled();

        var check = await TabletHandover.CheckAccountAsync(Officer(new FakeCentre()), state, () => Task.CompletedTask);

        Assert.True(check.Permitted);
        Assert.Single(state.Staff);
    }

    /// <summary>The user's case: a Juba registrar on a tablet enrolled to Munuki. Refused, named, and signed out at once.</summary>
    [Fact]
    public async Task AnAccountFromAnotherFacilityIsRefusedNamedAndSignedOut()
    {
        var check = await TabletHandover.CheckAccountAsync(
            Officer(new FakeCentre { StaffAnswer = HttpStatusCode.Forbidden, StaffField = "facilityId" }), Enrolled(), () => Task.CompletedTask);

        Assert.False(check.Permitted);
        Assert.True(check.EndSignIn);
        Assert.Contains(Munuki.Name, check.Problem);
    }

    [Fact]
    public async Task ATabletTheCentreRefusesIsNamedAsTheProblemNotTheAccount()
    {
        var check = await TabletHandover.CheckAccountAsync(
            Officer(new FakeCentre { StaffAnswer = HttpStatusCode.Forbidden, StaffField = "data.deviceId" }), Enrolled(), () => Task.CompletedTask);

        Assert.False(check.Permitted);
        Assert.False(check.EndSignIn);
        Assert.Contains("does not accept this tablet", check.Problem);
    }

    /// <summary>No signal is not a refusal: the account is checked again next window.</summary>
    [Fact]
    public async Task NoSignalIsNotARefusal()
    {
        var check = await TabletHandover.CheckAccountAsync(
            Officer(new FakeCentre { StaffAnswer = HttpStatusCode.ServiceUnavailable }), Enrolled(), () => Task.CompletedTask);

        Assert.True(check.Permitted);
        Assert.False(check.EndSignIn);
    }
}
