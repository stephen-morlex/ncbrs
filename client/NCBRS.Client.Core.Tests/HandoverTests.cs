using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NCBRS.Client.Auth;
using NCBRS.Client.Network;
using NCBRS.Client.Storage;
using NCBRS.Models;
using Xunit;
using static NCBRS.Client.Tests.CentralClientTests;

namespace NCBRS.Client.Tests;

/// <summary>
/// Handing a tablet over and unlocking it: the officer's one-off sign-in, the
/// facility it is handed to, the registrar's PIN set at the centre, and every
/// registrar's credential provisioned so any of them can unlock offline.
/// </summary>
public class HandoverTests
{
    private static readonly OidcEndpoints Realm = OidcEndpoints.ForKeycloakRealm(new Uri("https://id.ncbrs.ss/realms/ncbrs"));
    private static readonly Uri Redirect = new("ss.gov.ncbrs.client://auth");
    private static readonly Guid Facility = Guid.Parse("0199c000-0000-7000-8000-0000000f0003");
    private static readonly Guid Lado = Guid.Parse("0199c000-0000-7000-8000-0000000000b1");
    private static readonly Guid Kenyi = Guid.Parse("0199c000-0000-7000-8000-0000000000b2");

    /// <summary>A PIN hashed the way the centre stores it (DevicePinHasher.Hash).</summary>
    private static string ServerHash(string pin, int iterations = 1_000)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var derived = Rfc2898DeriveBytes.Pbkdf2(pin, salt, iterations, HashAlgorithmName.SHA256, 32);
        return $"pbkdf2-sha256${iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(derived)}";
    }

    private static DeviceState Staffed()
    {
        var state = new DeviceState();
        StaffUnlock.Provision(state, new DeviceCredentialBundle(Facility, "Terekeka PHCC", DateTime.UtcNow,
        [
            new DeviceCredentialEntry(Lado, "Nurse Lado", RegistrarRole.FacilityRegistrar, ServerHash("246813")),
            new DeviceCredentialEntry(Kenyi, "Dr Kenyi", RegistrarRole.FacilityRegistrar, ServerHash("975310")),
        ]));
        return state;
    }

    // --- signing in on a shared tablet --------------------------------------------------------

    /// <summary>
    /// A tablet's browser is shared. Without prompt=login the officer's session
    /// would sign the registrar straight in as the officer, or one registrar as
    /// another.
    /// </summary>
    [Fact]
    public void EverySignInAsksForTheAccountAgain()
    {
        var pkce = PkceChallenge.Create();
        var registrar = new OfflineTokenSession(new HttpClient(), Realm, null, (_, _) => Task.CompletedTask)
            .AuthorizationUrl(pkce, Redirect).AbsoluteUri;
        var officer = new InteractiveSignIn(new HttpClient(), Realm).AuthorizationUrl(pkce, Redirect).AbsoluteUri;

        Assert.Contains("prompt=login", registrar);
        Assert.Contains("prompt=login", officer);
    }

    /// <summary>An officer cannot hold an offline token, so theirs is an ordinary session.</summary>
    [Fact]
    public void TheOfficersSignInAsksForNoOfflineAccess()
    {
        var url = new InteractiveSignIn(new HttpClient(), Realm).AuthorizationUrl(PkceChallenge.Create(), Redirect).AbsoluteUri;

        Assert.Contains("scope=openid&", url);
        Assert.DoesNotContain("offline_access", url);
        Assert.Contains("code_challenge_method=S256", url);
    }

    private sealed class FakeKeycloak : HttpMessageHandler
    {
        public List<(string Path, string Form)> Calls { get; } = [];

        public HttpStatusCode TokenStatus { get; set; } = HttpStatusCode.OK;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls.Add((request.RequestUri!.AbsolutePath, await request.Content!.ReadAsStringAsync(cancellationToken)));
            var body = request.RequestUri.AbsolutePath.EndsWith("/token") && TokenStatus == HttpStatusCode.OK
                ? """{"access_token":"officer-access","expires_in":900,"refresh_token":"officer-refresh"}"""
                : """{"error":"invalid_grant"}""";
            var status = request.RequestUri.AbsolutePath.EndsWith("/token") ? TokenStatus : HttpStatusCode.OK;
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    /// <summary>
    /// Used for the handover, then ended at Keycloak, so the officer's session
    /// does not stay behind on a tablet that stays behind at the post.
    /// </summary>
    [Fact]
    public async Task TheOfficersSessionIsUsedThenEndedAtKeycloak()
    {
        var keycloak = new FakeKeycloak();
        var officer = new InteractiveSignIn(new HttpClient(keycloak), Realm);
        var pkce = PkceChallenge.Create();

        var result = await officer.RedeemAsync("the-code", pkce, Redirect);
        var during = await officer.AccessToken(CancellationToken.None);
        await officer.EndAsync();

        Assert.True(result.SignedIn);
        Assert.Equal("officer-access", during);
        Assert.Contains($"code_verifier={pkce.Verifier}", keycloak.Calls[0].Form);
        Assert.EndsWith("/revoke", keycloak.Calls[1].Path);
        Assert.Contains("token=officer-refresh", keycloak.Calls[1].Form);
        Assert.False(officer.SignedIn);
        Assert.Null(await officer.AccessToken(CancellationToken.None));
    }

    [Fact]
    public async Task ARefusedCodeLeavesNobodySignedIn()
    {
        var officer = new InteractiveSignIn(new HttpClient(new FakeKeycloak { TokenStatus = HttpStatusCode.BadRequest }), Realm);

        var result = await officer.RedeemAsync("stale-code", PkceChallenge.Create(), Redirect);

        Assert.False(result.SignedIn);
        Assert.Contains("400", result.Problem);
        Assert.False(officer.SignedIn);
    }

    // --- the handover calls ---------------------------------------------------------------------

    [Fact]
    public async Task TheFacilityListIsReadFromTheCentresPage()
    {
        var network = new FakeNetwork
        {
            Respond = _ => FakeNetwork.Envelope($$"""
                {"items":[{"facilityId":"{{Facility}}","name":"Terekeka PHCC","tier":"Clinic","countyCode":"SS0105",
                  "connectivityProfile":"Intermittent","brnBlockStart":300000,"brnBlockEnd":399999,"brnBlockNextAvailable":300200}],
                 "total":1,"nextCursor":null}
                """),
        };

        var result = await CentreClient(network, "officer-token").ListFacilitiesAsync("Terekeka");

        var facility = Assert.Single(result.Value!);
        Assert.Equal(new FacilitySummary(Facility, "Terekeka PHCC", FacilityTier.Clinic, "SS0105"), facility);
        var request = Assert.Single(network.Requests).Request;
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("/api/facilities", request.RequestUri!.AbsolutePath);
        Assert.Contains("name=Terekeka", request.RequestUri.Query);
        Assert.Equal("officer-token", request.Headers.Authorization!.Parameter);
    }

    [Fact]
    public async Task SettingAPinPutsItInTheCentresEnvelope()
    {
        var network = new FakeNetwork
        {
            Respond = _ => FakeNetwork.Envelope($$"""{"registrarId":"{{Lado}}","displayName":"Nurse Lado","updatedAtUtc":"2026-09-28T09:00:00Z"}"""),
        };

        var result = await CentreClient(network).SetOwnPinAsync("246813", currentPin: "975310");

        Assert.True(result.Succeeded);
        var (request, body) = Assert.Single(network.Requests);
        Assert.Equal(HttpMethod.Put, request.Method);
        Assert.Equal("/api/me/device-pin", request.RequestUri!.AbsolutePath);
        using var sent = JsonDocument.Parse(body);
        Assert.Equal("246813", sent.RootElement.GetProperty("data").GetProperty("pin").GetString());
        Assert.Equal("975310", sent.RootElement.GetProperty("data").GetProperty("currentPin").GetString());
    }

    /// <summary>The centre holds the PIN policy; its reason reaches the registrar.</summary>
    [Fact]
    public async Task APinTheCentreRefusesComesBackWithItsReason()
    {
        var network = new FakeNetwork
        {
            Respond = _ => FakeNetwork.Envelope(
                """{"status":400,"title":"PIN rejected.","errors":[{"field":"data.pin","message":"A PIN cannot be a run of consecutive digits."}]}""",
                HttpStatusCode.BadRequest),
        };

        var result = await CentreClient(network).SetOwnPinAsync("123456");

        Assert.Equal(CentralOutcome.Refused, result.Outcome);
        Assert.Contains(result.Errors!, error => error.Message.Contains("consecutive"));
    }

    [Fact]
    public async Task StaffCredentialsAreFetchedNamingTheDevice()
    {
        var network = new FakeNetwork
        {
            Respond = _ => FakeNetwork.Envelope($$"""
                {"facilityId":"{{Facility}}","facilityName":"Terekeka PHCC","issuedAtUtc":"2026-09-28T09:00:00Z",
                 "credentials":[{"registrarId":"{{Lado}}","displayName":"Nurse Lado","role":"FacilityRegistrar","credentialHash":"{{ServerHash("246813")}}"}]}
                """),
        };

        var result = await CentreClient(network).FetchStaffCredentialsAsync(Facility, "TABLET-TEREKEKA-01");

        Assert.Single(result.Value!.Credentials);
        var request = Assert.Single(network.Requests).Request;
        Assert.Equal($"/api/facilities/{Facility}/device-credentials", request.RequestUri!.AbsolutePath);
        Assert.Contains("deviceId=TABLET-TEREKEKA-01", request.RequestUri.Query);
    }

    /// <summary>A tablet handed to the wrong facility is revoked by the officer before it is handed over again.</summary>
    [Fact]
    public async Task RevokingNamesTheDeviceAndTheReason()
    {
        var network = new FakeNetwork
        {
            Respond = _ => FakeNetwork.Envelope($$"""
                {"deviceId":"TAB-0A1B2C3D4E5F","facilityId":"{{Facility}}","status":"Revoked","label":null,
                 "enrolledAtUtc":"2026-09-28T09:00:00Z","lastSeenAtUtc":null,"statusChangedAtUtc":"2026-09-28T10:00:00Z",
                 "statusReason":"Handed over to the wrong facility."}
                """),
        };

        var result = await CentreClient(network, "officer-token").RevokeDeviceAsync("TAB-0A1B2C3D4E5F", "Handed over to the wrong facility.");

        Assert.Equal(DeviceStatus.Revoked, result.Value!.Status);
        var (request, body) = Assert.Single(network.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/api/devices/TAB-0A1B2C3D4E5F/revoke", request.RequestUri!.AbsolutePath);
        using var sent = JsonDocument.Parse(body);
        Assert.Equal("Handed over to the wrong facility.", sent.RootElement.GetProperty("data").GetProperty("reason").GetString());
    }

    // --- unlocking ------------------------------------------------------------------------------

    [Fact]
    public void APinSetAtTheCentreUnlocksTheTablet()
    {
        var state = Staffed();

        var result = StaffUnlock.Attempt(state, Lado, "246813", DateTime.UtcNow);

        Assert.True(result.Unlocked);
        Assert.Equal(0, state.Attempts.FailedAttempts);
    }

    [Fact]
    public void AnotherRegistrarsPinDoesNotUnlockAsThem()
        => Assert.False(StaffUnlock.Attempt(Staffed(), Lado, "975310", DateTime.UtcNow).Unlocked);

    /// <summary>
    /// The counter is the device's. Moving to the next name on the list must
    /// not buy a fresh set of guesses.
    /// </summary>
    [Fact]
    public void WrongGuessesCountForTheDeviceNotThePerson()
    {
        var state = Staffed();
        var now = DateTime.UtcNow;

        for (var guess = 0; guess < 4; guess++)
        {
            StaffUnlock.Attempt(state, Lado, "000111", now);
        }

        var fifth = StaffUnlock.Attempt(state, Kenyi, "000111", now);
        var rightPinWhileLocked = StaffUnlock.Attempt(state, Kenyi, "975310", now);

        Assert.Equal(UnlockOutcome.LockedOut, fifth.Outcome);
        Assert.Equal(UnlockOutcome.LockedOut, rightPinWhileLocked.Outcome);
        Assert.NotNull(state.Attempts.LockedUntilUtc);
    }

    [Fact]
    public void ARefreshDoesNotForgiveGuesses()
    {
        var state = Staffed();
        StaffUnlock.Attempt(state, Lado, "000111", DateTime.UtcNow);

        StaffUnlock.Provision(state, new DeviceCredentialBundle(Facility, "Terekeka PHCC", DateTime.UtcNow,
            [new DeviceCredentialEntry(Lado, "Nurse Lado", RegistrarRole.FacilityRegistrar, ServerHash("246813"))]));

        Assert.Equal(1, state.Attempts.FailedAttempts);
        Assert.Single(state.Staff);
    }

    /// <summary>An entry the device cannot verify is someone who cannot unlock here, never someone who unlocks with anything.</summary>
    [Theory]
    [InlineData("bcrypt$10$abc$def")]
    [InlineData("pbkdf2-sha256$notanumber$c2FsdA==$aGFzaA==")]
    [InlineData("pbkdf2-sha256$1000$***$aGFzaA==")]
    [InlineData("pbkdf2-sha256$1000$c2FsdA==")]
    [InlineData("")]
    public void AnUnverifiableHashIsLeftOutNotTrusted(string hash)
    {
        var state = new DeviceState();

        var skipped = StaffUnlock.Provision(state, new DeviceCredentialBundle(Facility, "Terekeka PHCC", DateTime.UtcNow,
            [new DeviceCredentialEntry(Lado, "Nurse Lado", RegistrarRole.FacilityRegistrar, hash)]));

        Assert.Empty(state.Staff);
        Assert.Equal(["Nurse Lado"], skipped);
    }

    [Fact]
    public void SomeoneNotOnTheListCannotAttempt()
        => Assert.Throws<ArgumentException>(() => StaffUnlock.Attempt(Staffed(), Guid.NewGuid(), "246813", DateTime.UtcNow));
}

/// <summary>The device id a tablet enrols under.</summary>
public class DeviceIdTests
{
    [Fact]
    public void TheSameKeyAlwaysGivesTheSameIdSoAnInterruptedHandoverRetriesAsItself()
    {
        var signer = NCBRS.Client.Sync.DeviceSigner.Generate();
        var restored = NCBRS.Client.Sync.DeviceSigner.FromPrivateKey(signer.ExportPrivateKeyPem());

        Assert.Equal(DeviceIdentity.IdFor(signer.PublicKeyPem), DeviceIdentity.IdFor(restored.PublicKeyPem));
        Assert.Matches("^TAB-[0-9A-F]{12}$", DeviceIdentity.IdFor(signer.PublicKeyPem));
    }

    [Fact]
    public void DifferentKeysGiveDifferentIds()
        => Assert.NotEqual(
            DeviceIdentity.IdFor(NCBRS.Client.Sync.DeviceSigner.Generate().PublicKeyPem),
            DeviceIdentity.IdFor(NCBRS.Client.Sync.DeviceSigner.Generate().PublicKeyPem));
}
