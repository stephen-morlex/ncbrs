using System.Net.Http.Json;
using System.Text.Json;
using NCBRS.Client;
using NCBRS.Client.Brn;
using NCBRS.Client.Network;
using NCBRS.Client.Sync;
using NCBRS.Models;

/// <summary>
/// The device path against a real stack: what a tablet does from handover to
/// its second connectivity window, through the client network layer and
/// nothing else. Exits non-zero if any step does not do what it must.
///
/// Run by the TLS rehearsal, in Production against TLS Postgres, Kafka and
/// Keycloak with device signatures enforced — the configuration a tablet will
/// actually meet. Configured by environment:
///
///   NCBRS_CLIENT_CENTRE      the centre's base URL
///   NCBRS_CLIENT_SYNC_VIA    a District node's base URL (the first window goes through it)
///   NCBRS_CLIENT_TOKEN_URL   the realm's token endpoint
///   NCBRS_CLIENT_FACILITY_ID the facility the device is enrolled at
///   NCBRS_CLIENT_OFFICER / NCBRS_CLIENT_REGISTRAR   dev-realm fixture accounts (password "password")
///
/// The password grant here is the harness's way to sign in, not a design for
/// the tablet: how a registrar signs in on a device is still to be decided.
/// </summary>
internal static class OnlineRehearsal
{
    private static int _failures;

    public static async Task<int> RunAsync()
    {
        string Setting(string name, string fallback)
            => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : fallback;

        var centre = new Uri(Setting("NCBRS_CLIENT_CENTRE", "http://localhost:5259/"));
        var district = Environment.GetEnvironmentVariable("NCBRS_CLIENT_SYNC_VIA") is { Length: > 0 } via ? new Uri(via) : null;
        var tokenUrl = Setting("NCBRS_CLIENT_TOKEN_URL", "http://localhost:8080/realms/ncbrs/protocol/openid-connect/token");
        var facilityId = Guid.Parse(Setting("NCBRS_CLIENT_FACILITY_ID", "0199c000-0000-7000-8000-0000000f0001"));
        var officer = Setting("NCBRS_CLIENT_OFFICER", "district.officer");
        var registrar = Setting("NCBRS_CLIENT_REGISTRAR", "nurse.lado");

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
        var deviceId = $"HARNESS-{Guid.NewGuid():N}"[..20].ToUpperInvariant();
        var signer = DeviceSigner.Generate();

        // 1. Handover: the district officer enrols the tablet's public key.
        var asOfficer = new CentralClient(http, new CentralEndpoints(centre), Token(http, tokenUrl, officer));
        var enrolled = await asOfficer.EnrolDeviceAsync(new EnrolDeviceRequest
        {
            DeviceId = deviceId, FacilityId = facilityId, PublicKeyPem = signer.PublicKeyPem, Label = "Client harness",
        });
        Check($"the officer enrolled device {deviceId}", enrolled.Succeeded, Describe(enrolled));

        // 2. First connectivity as the registrar: a real block of numbers.
        // The registrar signs in once and the tablet keeps an offline token. On a
        // real tablet that sign-in is a browser (code + PKCE); the harness uses
        // the dev realm's password grant for the same token response.
        var savedTokens = new List<string?>();
        var session = new NCBRS.Client.Auth.OfflineTokenSession(
            http,
            new NCBRS.Client.Auth.OidcEndpoints(new Uri(tokenUrl.Replace("/token", "/auth")), new Uri(tokenUrl), new Uri(tokenUrl.Replace("/token", "/revoke"))),
            offlineToken: null,
            (token, _) => { savedTokens.Add(token); return Task.CompletedTask; });
        var signIn = await session.AcceptAsync(await PasswordSignIn(http, tokenUrl, registrar, offline: true));
        Check("the registrar signed in once and the tablet holds an offline token", signIn.SignedIn, signIn.Problem);

        var officerOffline = await session.AcceptAsync(await PasswordSignIn(http, tokenUrl, officer, offline: true));
        Check("a district officer cannot hold a tablet's offline sign-in", !officerOffline.SignedIn);
        if (!signIn.SignedIn)
        {
            return 1;
        }

        var asRegistrar = new CentralClient(http, new CentralEndpoints(centre, district), session.AccessToken);
        var block = await asRegistrar.RequestBrnBlockAsync(facilityId, deviceId, signer, blockSize: 20);
        Check("the registrar drew a block of registration numbers, signed by the device", block.Succeeded, Describe(block));
        if (!block.Succeeded)
        {
            return 1;
        }

        // 2b. The registrar sets their offline PIN at the centre, the device
        //     provisions its facility's staff credentials, and that PIN unlocks
        //     the tablet with no network: the centre's hash and the tablet's lock
        //     agreeing, live. The current PIN is sent as well, so a re-run
        //     against a database where it is already set changes nothing.
        const string pin = "246813";
        var pinSet = await asRegistrar.SetOwnPinAsync(pin, currentPin: pin);
        Check("the registrar set their offline PIN at the centre", pinSet.Succeeded, Describe(pinSet));
        var staff = await asRegistrar.FetchStaffCredentialsAsync(facilityId, deviceId);
        var deviceState = new NCBRS.Client.Storage.DeviceState();
        if (staff.Value is { } bundle)
        {
            NCBRS.Client.Storage.StaffUnlock.Provision(deviceState, bundle);
        }

        var unlocker = deviceState.Staff.Find(person => person.RegistrarId == pinSet.Value?.RegistrarId);
        Check("the device provisioned the facility's staff credentials, the registrar among them",
            staff.Succeeded && unlocker is not null, Describe(staff));
        Check("the PIN set at the centre unlocks the tablet offline, and a wrong one does not",
            unlocker is not null
            && !NCBRS.Client.Storage.StaffUnlock.Attempt(deviceState, unlocker.RegistrarId, "975310", DateTime.UtcNow).Unlocked
            && NCBRS.Client.Storage.StaffUnlock.Attempt(deviceState, unlocker.RegistrarId, pin, DateTime.UtcNow).Unlocked);

        var facility = new FacilityClient(
            deviceId, facilityId,
            new DeviceBrnAllocator(deviceId, block.Value!.BlockStart, block.Value.BlockEnd,
                officeCode: block.Value.OfficeCode, year: block.Value.Year),
            new SyncOutbox(deviceId, facilityId),
            signer,
            lowBlockThreshold: 5);

        // 3. Offline: register births.
        var drafts = Enumerable.Range(0, 3).Select(_ => facility.RegisterBirth(Birth())).ToList();
        Check("three births registered offline from the granted block", drafts.All(draft => !draft.IsProvisional));

        // 4. A connectivity window, uploading through the District node when one is named.
        var state = new ClientSyncState();
        var saves = 0;
        var window = new ConnectivityWindow(facility, asRegistrar, signer, (_, _) => { saves++; return Task.CompletedTask; });
        var first = await window.RunAsync(state, DateTime.UtcNow);
        var settled = first.Settlements.SelectMany(settlement => settlement.Settled).ToList();
        Check($"window 1 ({(district is null ? "direct" : "through the District node")}) settled all three births",
            first.Upload == CentralOutcome.Succeeded && settled.Count == 3 && facility.PendingCount == 0,
            string.Join(" | ", first.Problems));
        Check("every number was confirmed against the block the centre granted", settled.All(outcome => outcome.BrnConfirmed));
        Check("the verification bundle was fetched and is current", first.BundleRefreshed && !state.Bundle.RefreshDue(DateTime.UtcNow));
        Check("state was persisted as the window went", saves >= 2);

        // 5. The app restarts, as a tablet's does between visits: the only sign-in
        //    it has is the offline token it saved. More births, then a second
        //    window straight to the centre, renewing from that saved token.
        session = new NCBRS.Client.Auth.OfflineTokenSession(
            http,
            new NCBRS.Client.Auth.OidcEndpoints(new Uri(tokenUrl.Replace("/token", "/auth")), new Uri(tokenUrl), new Uri(tokenUrl.Replace("/token", "/revoke"))),
            savedTokens.Last(),
            (token, _) => { savedTokens.Add(token); return Task.CompletedTask; });
        facility.RegisterBirth(Birth());
        facility.RegisterBirth(Birth());
        var direct = new ConnectivityWindow(
            facility,
            new CentralClient(http, new CentralEndpoints(centre), session.AccessToken),
            signer,
            (_, _) => Task.CompletedTask);
        var second = await direct.RunAsync(state, DateTime.UtcNow);
        Check("window 2 (direct to the centre) settled both new births",
            second.Upload == CentralOutcome.Succeeded
            && second.Settlements.Sum(settlement => settlement.Settled.Count) == 2
            && facility.PendingCount == 0,
            string.Join(" | ", second.Problems));

        // 6. The same upload twice: the centre recognises the transaction.
        facility.RegisterBirth(Birth());
        var upload = facility.BuildSignedUpload();
        var once = await asRegistrar.UploadAsync(upload);
        var again = await asRegistrar.UploadAsync(upload);
        Check("resending the same upload returns the same answer, not a second registration",
            once.Succeeded && again.Succeeded
            && again.Value!.Records.All(record => record.Status is SyncRecordStatus.Registered or SyncRecordStatus.Duplicate)
            && again.Value.SyncBatchId == once.Value!.SyncBatchId,
            $"{once.Outcome}/{again.Outcome}");
        if (again.Value is { } answer)
        {
            facility.Settle(answer);
        }

        // 7. Every window ran on the offline token, and Keycloak rotated it: each
        //    new one was saved, or the tablet would end up holding a dead sign-in.
        Check("every renewal saved the rotated offline token",
            savedTokens.Count >= 2 && savedTokens.All(token => NCBRS.Client.Auth.OfflineTokenSession.TokenType(token!) == "Offline")
            && savedTokens.Distinct().Count() == savedTokens.Count,
            $"{savedTokens.Count} saved");

        // 8. Sign-out revokes the offline token at Keycloak. The next window then
        //    says "sign in again" -- not "unreachable" -- and keeps the upload.
        facility.RegisterBirth(Birth());
        await session.SignOutAsync();
        var afterSignOut = await new ConnectivityWindow(
                facility,
                new CentralClient(http, new CentralEndpoints(centre), session.AccessToken),
                signer,
                (_, _) => Task.CompletedTask)
            .RunAsync(state, DateTime.UtcNow);
        Check("after sign-out the next window asks the registrar to sign in again, and keeps the births",
            afterSignOut.Upload == CentralOutcome.Unauthorized && state.InFlight is not null && facility.PendingCount == 1,
            $"{afterSignOut.Upload}; {string.Join(" | ", afterSignOut.Problems)}");

        var stolen = savedTokens[^2]!;   // the offline token as it stood before sign-out
        var reuse = new NCBRS.Client.Auth.OfflineTokenSession(
            http,
            new NCBRS.Client.Auth.OidcEndpoints(new Uri(tokenUrl.Replace("/token", "/auth")), new Uri(tokenUrl), new Uri(tokenUrl.Replace("/token", "/revoke"))),
            stolen,
            (_, _) => Task.CompletedTask);
        Check("a copy of the revoked offline token is dead at Keycloak too", await reuse.GetAccessTokenAsync() is null);

        Console.WriteLine(_failures == 0 ? "\nClient network layer: all steps passed" : $"\nClient network layer: {_failures} step(s) failed");
        return _failures == 0 ? 0 : 1;
    }

    private static void Check(string what, bool ok, string? detail = null)
    {
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {what}{(ok || string.IsNullOrEmpty(detail) ? "" : $" — {detail}")}");
        if (!ok)
        {
            _failures++;
        }
    }

    private static string Describe<T>(CentralResult<T> result)
        => $"{result.Outcome} {result.StatusCode} {result.Detail} "
           + string.Join("; ", result.Errors?.Select(error => $"{error.Field}: {error.Message}") ?? []);

    private static RegisterBirthRequest Birth()
    {
        // Two random words, so no two births look like one child to the duplicate matcher.
        static string Word() => string.Concat(Enumerable.Range(0, 8).Select(_ => (char)('a' + Random.Shared.Next(26)))) is var w
            ? char.ToUpperInvariant(w[0]) + w[1..]
            : "";

        return new RegisterBirthRequest
        {
            ChildFullName = $"{Word()} {Word()}",
            DateOfBirth = DateTime.UtcNow.Date.AddDays(-Random.Shared.Next(1, 30)),
            Sex = Random.Shared.Next(2) == 0 ? Sex.Female : Sex.Male,
            BirthWeightGrams = 3200,
            GestationalAgeWeeks = 39.5m,
            Plurality = BirthPlurality.Singleton,
            BirthOrder = 1,
            RegisteredAtUtc = DateTime.UtcNow,
        };
    }

    /// <summary>
    /// The dev realm's password grant, standing in for the browser sign-in a
    /// tablet does. With <paramref name="offline"/>, asks for an offline token.
    /// </summary>
    private static async Task<JsonElement> PasswordSignIn(HttpClient http, string tokenUrl, string username, bool offline)
    {
        using var response = await http.PostAsync(tokenUrl, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "password", ["client_id"] = "ncbrs-device", ["username"] = username, ["password"] = "password",
            ["scope"] = offline ? NCBRS.Client.Auth.OfflineTokenSession.Scope : "openid",
        }));
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    /// <summary>A password-grant token for a dev-realm fixture account, refreshed a minute before it expires.</summary>
    private static AccessTokenProvider Token(HttpClient http, string tokenUrl, string username)
    {
        string? token = null;
        var expires = DateTime.MinValue;

        return async cancellationToken =>
        {
            if (token is not null && DateTime.UtcNow < expires.AddMinutes(-1))
            {
                return token;
            }

            using var response = await http.PostAsync(tokenUrl, new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "password", ["client_id"] = "ncbrs-device", ["username"] = username, ["password"] = "password",
            }), cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var body = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
            token = body.GetProperty("access_token").GetString();
            expires = DateTime.UtcNow.AddSeconds(body.TryGetProperty("expires_in", out var lifetime) ? lifetime.GetInt32() : 300);
            return token;
        };
    }
}
