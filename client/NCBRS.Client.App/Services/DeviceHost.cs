using NCBRS.Client.Auth;
using NCBRS.Client.Localization;
using NCBRS.Client.Network;
using NCBRS.Client.Printing;
using NCBRS.Client.Storage;
using NCBRS.Client.Sync;
using NCBRS.Models;

namespace NCBRS.Client.App.Services;

/// <summary>Where the tablet is in its life, which decides the screen it shows.</summary>
public enum Stage
{
    /// <summary>The store is there and cannot be read. Nothing may overwrite it.</summary>
    Unreadable,
    /// <summary>Not yet enrolled: a district officer hands it over.</summary>
    Handover,
    /// <summary>Enrolled; no registrar has signed it in.</summary>
    RegistrarSignIn,
    /// <summary>Signed in; no block of numbers, or nobody who can unlock it.</summary>
    Provision,
    /// <summary>Ready, and locked.</summary>
    Unlock,
    /// <summary>Unlocked by a registrar.</summary>
    Ready,
}

/// <summary>
/// The tablet's one owner of state: the encrypted store, what was loaded from
/// it, the session rebuilt from that, and the sign-ins. Pages ask it what to
/// do and it saves after every act — the rule the core's state machines rely
/// on. It holds no registration logic; every act is a call into the core.
/// </summary>
public sealed class DeviceHost(ISignInBrowser browser, IDocumentPrinter? printer = null)
{
    /// <summary>Android's print system on the tablet; none on the Windows dev head.</summary>
    public IDocumentPrinter? Printer => printer;

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(120) };
    private EncryptedStateFile? _store;
    private OfflineTokenSession? _registrar;

    public DeviceState State { get; private set; } = new();

    public DeviceSession? Session { get; private set; }

    /// <summary>The registrar who unlocked the tablet, credited with what it registers.</summary>
    public StaffCredential? UnlockedAs { get; private set; }

    public string? UnreadableReason { get; private set; }

    public Stage Stage => UnreadableReason is not null ? Stage.Unreadable
        : State.Identity is null ? Stage.Handover
        : State.OfflineToken is null ? Stage.RegistrarSignIn
        : State.Brn is null || State.Staff.Count == 0 ? Stage.Provision
        : UnlockedAs is null ? Stage.Unlock
        : Stage.Ready;

    public async Task OpenAsync()
    {
        try
        {
            _store = await DeviceStorage.OpenAsync();
            State = await _store.LoadAsync() ?? new DeviceState();
            RestoreSession();
        }
        catch (StateFileUnreadableException exception)
        {
            // Stop here. Carrying on as a fresh device would overwrite births
            // that were never synced.
            UnreadableReason = exception.Message;
        }
    }

    /// <summary>Capture the session into the state and write it. Called after every act.</summary>
    public async Task SaveAsync()
    {
        if (UnreadableReason is not null || _store is null)
        {
            throw new InvalidOperationException("The device store is not open for writing.");
        }

        Session?.Capture(State);
        await _store.SaveAsync(State);
    }

    // --- handover -------------------------------------------------------------------------------

    /// <summary>The officer signs in on the tablet, for the handover only.</summary>
    public async Task<(InteractiveSignIn? Officer, string? Problem)> OfficerSignInAsync(Uri realm)
    {
        var officer = new InteractiveSignIn(_http, OidcEndpoints.ForKeycloakRealm(realm));
        var pkce = PkceChallenge.Create();
        var callback = await browser.SignInAsync(officer.AuthorizationUrl(pkce, browser.RedirectUri));
        if (callback is null)
        {
            return (null, Strings.Host_SignInClosed);
        }

        if (callback.State != pkce.State)
        {
            return (null, Strings.Host_SignInMismatch);
        }

        var result = await officer.RedeemAsync(callback.Code, pkce, browser.RedirectUri);
        return result.SignedIn ? (officer, null) : (null, result.Problem);
    }

    public Task<CentralResult<IReadOnlyList<FacilitySummary>>> ListFacilitiesAsync(InteractiveSignIn officer, Uri centre)
        => new CentralClient(_http, new CentralEndpoints(centre), officer.AccessToken).ListFacilitiesAsync();

    /// <summary>
    /// Enrol this tablet to the facility, checked against the centre's record
    /// (<see cref="TabletHandover"/>), then end the officer's session.
    /// </summary>
    public async Task<HandoverResult> EnrolAsync(
        InteractiveSignIn officer, FacilitySummary facility, Uri centre, Uri realm, Uri? syncVia, string? label)
    {
        try
        {
            return await TabletHandover.EnrolAsync(
                new CentralClient(_http, new CentralEndpoints(centre), officer.AccessToken),
                State, facility, centre, realm, syncVia, label, SaveAsync);
        }
        finally
        {
            await officer.EndAsync();
        }
    }

    /// <summary>What the tablet is enrolled to, as the officer chose it at handover.</summary>
    public string EnrolledTo => State.Identity?.FacilityName ?? State.Identity?.FacilityId.ToString() ?? "";

    /// <summary>
    /// Handing over again is only safe with nothing waiting to sync: the key
    /// is discarded, and births signed under it could then never be sent.
    /// </summary>
    public bool CanHandOverAgain => State.Outbox.Count == 0 && State.InFlight is null;

    /// <summary>
    /// A tablet handed to the wrong facility. The officer signs in and revokes
    /// this device at the centre; only once that is confirmed is the key
    /// discarded and the handover started again under a new one. Revoking
    /// first means the old identity can never be left enrolled and unused.
    /// </summary>
    public async Task<string?> HandOverAgainAsync()
    {
        if (!CanHandOverAgain)
        {
            return Strings.Host_HandOverBlocked;
        }

        var identity = State.Identity!;
        var (officer, problem) = await OfficerSignInAsync(identity.Realm!);
        if (officer is null)
        {
            return problem;
        }

        try
        {
            // Withdrawn at the centre, and confirmed withdrawn, before the key goes.
            if (await TabletHandover.RevokeForHandoverAsync(
                    new CentralClient(_http, new CentralEndpoints(identity.Centre), officer.AccessToken), identity) is { } refused)
            {
                return refused;
            }
        }
        finally
        {
            await officer.EndAsync();
        }

        if (State.OfflineToken is not null)
        {
            await Registrar().SignOutAsync();
        }

        Session?.Dispose();
        Session = null;
        _registrar = null;
        UnlockedAs = null;
        State = new DeviceState();
        await SaveAsync();
        return null;
    }

    /// <summary>End the registrar's sign-in, at Keycloak too, so another account can sign the tablet in.</summary>
    public async Task SignOutRegistrarAsync()
    {
        await Registrar().SignOutAsync();
        _registrar = null;
        UnlockedAs = null;
    }

    // --- the registrar ----------------------------------------------------------------------------

    private OfflineTokenSession Registrar()
        => _registrar ??= new OfflineTokenSession(
            _http,
            OidcEndpoints.ForKeycloakRealm(State.Identity!.Realm ?? throw new InvalidOperationException("No realm recorded.")),
            State.OfflineToken,
            async (token, _) =>
            {
                // Rotated on every renewal: the saved one must always be the latest.
                State.OfflineToken = token;
                await SaveAsync();
            });

    private CentralClient Centre()
        => new(_http, new CentralEndpoints(State.Identity!.Centre, State.Identity.SyncVia), Registrar().AccessToken);

    public async Task<string?> RegistrarSignInAsync()
    {
        var session = Registrar();
        var pkce = PkceChallenge.Create();
        var callback = await browser.SignInAsync(session.AuthorizationUrl(pkce, browser.RedirectUri));
        if (callback is null)
        {
            return Strings.Host_SignInClosed;
        }

        if (callback.State != pkce.State)
        {
            return Strings.Host_SignInMismatch;
        }

        var result = await session.RedeemAsync(callback.Code, pkce, browser.RedirectUri);
        if (!result.SignedIn)
        {
            return result.Problem;
        }

        // Checked at once: an account that may not act for this facility would
        // otherwise sit on the tablet, signed in and useless.
        var check = await TabletHandover.CheckAccountAsync(Centre(), State, SaveAsync);
        if (check.EndSignIn)
        {
            await SignOutRegistrarAsync();
        }

        return check.Permitted ? null : check.Problem;
    }

    /// <summary>
    /// The first block of numbers, the verification bundle and the staff who can
    /// unlock. Each saved as it arrives, so a dropped link keeps what came.
    /// </summary>
    public async Task<IReadOnlyList<string>> ProvisionAsync()
    {
        var problems = new List<string>();
        var identity = State.Identity!;
        var centre = Centre();

        if (State.Brn is null)
        {
            var block = await centre.RequestBrnBlockAsync(
                identity.FacilityId, identity.DeviceId, DeviceSigner.FromPrivateKey(State.DevicePrivateKeyPem!));
            if (block is { Succeeded: true, Value: { } range })
            {
                State.Brn = new BrnState(range.BlockStart, range.BlockEnd, range.BlockStart, 0, null, null, range.OfficeCode, range.Year);
                await SaveAsync();
                RestoreSession();
            }
            else if (block.RefusedTheDevice)
            {
                problems.Add(Language.Format(Strings.Host_DeviceRefused,
                    string.Join("; ", block.Errors!.Select(error => error.Message))));
            }
            else if (block.RefusedTheAccountHere)
            {
                problems.Add(Language.Format(Strings.Host_AccountNotHere, EnrolledTo));
            }
            else
            {
                problems.Add(Describe(Strings.Host_NoBlock, block));
            }
        }

        if (Session is not null)
        {
            var report = await new ConnectivityWindow(Session.Facility, centre, Session.Signer, (_, _) => SaveAsync())
                .RunAsync(Session.Sync, DateTime.UtcNow);
            problems.AddRange(report.Problems);
        }

        problems.AddRange(await RefreshStaffAsync(centre));
        return problems;
    }

    /// <summary>Set the signed-in registrar's PIN at the centre, then take the fresh staff list.</summary>
    public async Task<IReadOnlyList<string>> SetPinAsync(string pin, string? currentPin)
    {
        var centre = Centre();
        pin = PinPolicy.Normalise(pin);
        if (PinPolicy.Problem(pin) is { } refused)
        {
            return [refused];
        }

        currentPin = PinPolicy.Normalise(currentPin);
        var set = await centre.SetOwnPinAsync(pin, currentPin.Length == 0 ? null : currentPin);
        if (!set.Succeeded)
        {
            // Changing a PIN needs the current one, so a token alone cannot lock
            // a colleague out. Said in the registrar's terms, not the API's.
            if (set.Errors?.Any(error => error.Field == "data.currentPin") == true)
            {
                return [currentPin.Length == 0
                    ? Strings.Host_PinExists
                    : Strings.Host_PinCurrentWrong];
            }

            return [Describe(Strings.Host_PinNotSet, set)];
        }

        return await RefreshStaffAsync(centre);
    }

    private async Task<IReadOnlyList<string>> RefreshStaffAsync(CentralClient centre)
    {
        var staff = await centre.FetchStaffCredentialsAsync(State.Identity!.FacilityId, State.Identity.DeviceId);
        if (staff.Value is not { } bundle)
        {
            return [Describe(Strings.Host_StaffNotRefreshed, staff)];
        }

        var skipped = StaffUnlock.Provision(State, bundle);
        await SaveAsync();
        return skipped.Select(name => Language.Format(Strings.Host_StaffSkipped, name)).ToList();
    }

    // --- unlock, register, sync -------------------------------------------------------------------

    /// <summary>Saved after every attempt, right or wrong, so quitting between guesses resets nothing.</summary>
    public async Task<UnlockResult> UnlockAsync(StaffCredential person, string pin)
    {
        var result = StaffUnlock.Attempt(State, person.RegistrarId, PinPolicy.Normalise(pin), DateTime.UtcNow);
        await SaveAsync();
        if (result.Unlocked)
        {
            UnlockedAs = person;
        }

        return result;
    }

    public void Lock() => UnlockedAs = null;

    /// <summary>Register, then save before the result is shown: a number the store does not know can be handed out again.</summary>
    public async Task<RegistrationDraft> RegisterAsync(RegisterBirthRequest birth)
    {
        var draft = Session!.Facility.RegisterBirth(birth, UnlockedAs?.RegistrarId);
        await SaveAsync();
        return draft;
    }

    /// <summary>
    /// Seal the births waiting to sync into a transfer file for a USB stick or
    /// card, readable by the registry alone. Written to the app's cache, from
    /// where the share sheet carries it to removable media; the births stay
    /// queued until the registry confirms them, because a stick can be lost.
    /// </summary>
    public async Task<(string? Path, int Count, string? Problem)> ExportAsync()
    {
        if (Session!.Sync.TransferKey is not { } key)
        {
            return (null, 0, Strings.Host_NoTransferKey);
        }

        if (Session.Facility.SendableCount == 0)
        {
            return (null, 0, Strings.Host_NothingToExport);
        }

        var export = Session.Facility.BuildSealedTransferFile(key);
        var now = DateTime.UtcNow;

        // One export at a time in the cache: an older file carries fewer
        // births, and a copy lying around serves nobody.
        var directory = Path.Combine(FileSystem.CacheDirectory, "exports");
        Directory.CreateDirectory(directory);
        foreach (var old in Directory.GetFiles(directory))
        {
            File.Delete(old);
        }

        var path = Path.Combine(directory, $"ncbrs-{State.Identity!.DeviceId}-{now:yyyyMMdd-HHmm}.ncbrs-transfer.json");
        await File.WriteAllBytesAsync(path, export.File);

        State.LastExport = new ExportRecord(now, [.. export.Brns]);
        await SaveAsync();
        return (path, export.Brns.Count, null);
    }

    /// <summary>How many births from the last export the registry has not yet confirmed.</summary>
    public int ExportedAndUnconfirmed
        => State.LastExport is { } last && Session is { } session
            ? last.Brns.Count(brn => session.Facility.Refused.All(refused => refused.Record.Birth.Brn != brn)
                                     && State.Outbox.Exists(record => record.Birth.Brn == brn))
            : 0;

    /// <summary>Correct a refused birth and save it, released for the next sync.</summary>
    /// <summary>The slip for a birth just registered, as the family takes it away.</summary>
    public PrintedDocument SlipFor(RegistrationDraft draft, RegisterBirthRequest birth)
        => PrintedDocuments.Slip(draft, birth, EnrolledTo, UnlockedAs?.DisplayName);

    /// <summary>
    /// A birth's certificate from the registry, ready to print: online only,
    /// since only the registry can sign one. Saved after, because the sign-in
    /// may have renewed on the way.
    /// </summary>
    public async Task<CertificateToPrint> FetchCertificateAsync(string brn)
    {
        var result = await CertificateForPrint.FetchAsync(
            Centre(), brn.Trim(), State.Identity!.DeviceId, Session!.Signer, Session.Sync.Bundle, DateTime.UtcNow);
        await SaveAsync();
        return result;
    }

    public async Task CorrectAsync(string brn, RegisterBirthRequest corrected)
    {
        Session!.Facility.Correct(brn, corrected);
        await SaveAsync();
    }

    public async Task<(WindowReport Report, IReadOnlyList<string> StaffProblems)> SyncAsync()
    {
        var centre = Centre();
        var report = await new ConnectivityWindow(Session!.Facility, centre, Session.Signer, (_, _) => SaveAsync())
            .RunAsync(Session.Sync, DateTime.UtcNow);
        await SaveAsync();
        var staff = report.Upload is CentralOutcome.Unauthorized or CentralOutcome.Unreachable
            ? []
            : await RefreshStaffAsync(centre);
        return (report, staff);
    }

    private void RestoreSession()
    {
        if (Session is null && DeviceSession.CanRestore(State))
        {
            Session = DeviceSession.Restore(State);
        }
    }

    public static string Describe<T>(string what, CentralResult<T> result)
        => $"{what}: {Language.Name(result.Outcome)}"
           + (result.Errors is { Count: > 0 } errors ? " — " + string.Join("; ", errors.Select(error => error.Message)) : "")
           + (result.Detail is { Length: > 0 } detail ? $" ({detail})" : "");
}
