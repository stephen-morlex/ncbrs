using NCBRS.Certificates;
using NCBRS.Client.Certificates;
using NCBRS.Client.Localization;
using NCBRS.Client.Sync;
using NCBRS.Models;

namespace NCBRS.Client.Network;

/// <summary>
/// The device state a connectivity window reads and changes, beyond what
/// <see cref="FacilityClient"/> holds. The shell persists it to the encrypted
/// store (B2) whenever <see cref="ConnectivityWindow"/> asks.
/// </summary>
public sealed class ClientSyncState
{
    /// <summary>
    /// The upload that has been sent, or is about to be, and not yet settled.
    /// Persisted <em>before</em> it is sent: if the app dies or the link drops
    /// mid-request, the next window sends these same bytes again rather than
    /// building a new batch, and the centre recognises the transaction.
    /// </summary>
    public SignedUpload? InFlight { get; set; }

    /// <summary>What the device verifies certificates against offline (B8).</summary>
    public CachedVerificationBundle Bundle { get; set; } = CachedVerificationBundle.Empty;

    /// <summary>
    /// The registry's transfer key, from the same bundle: what a USB transfer
    /// file is sealed to. Null until a bundle carrying one is fetched.
    /// </summary>
    public TransferKeyResponse? TransferKey { get; set; }
}

/// <summary>What one connectivity window achieved, for the shell to show and act on.</summary>
public sealed record WindowReport(
    /// <summary>How the last upload attempt ended, or null if there was nothing to send.</summary>
    CentralOutcome? Upload,
    /// <summary>Each settled upload: registered, already held, rejected, and any permanent BRNs assigned to provisional records.</summary>
    IReadOnlyList<OutboxSettlement> Settlements,
    BrnBlockResponse? BlockGranted,
    bool BundleRefreshed,
    /// <summary>Plain-language reasons anything did not happen, for the registrar or the district.</summary>
    IReadOnlyList<string> Problems);

/// <summary>
/// One connectivity window, used in the order that matters when it may last
/// only seconds:
///
/// 1. <b>Births first.</b> Finish the upload already in flight, then send what
///    has been registered since. That is the legal record, and everything else
///    can wait for the next window.
/// 2. <b>Numbers next.</b> Top up the BRN block if it is low, so the post does
///    not fall back to provisional identifiers before the next window.
/// 3. <b>The verification bundle last,</b> when it is due — without it the
///    device answers Unknown to every certificate once its cache expires.
///
/// Each step that changes state calls <c>persist</c> before going on, so a
/// window cut short leaves nothing half-done that the next one cannot finish.
/// </summary>
public sealed class ConnectivityWindow(
    FacilityClient facility,
    CentralClient central,
    DeviceSigner signer,
    Func<ClientSyncState, CancellationToken, Task> persist)
{
    /// <summary>How far ahead of expiry the verification bundle is refreshed.</summary>
    public TimeSpan BundleRefreshLead { get; init; } = TimeSpan.FromDays(2);

    public int BrnBlockSize { get; init; } = 200;

    public async Task<WindowReport> RunAsync(
        ClientSyncState state, DateTime nowUtc, CancellationToken cancellationToken = default)
    {
        var settlements = new List<OutboxSettlement>();
        var problems = new List<string>();

        var upload = await SyncAsync(state, nowUtc, settlements, problems, cancellationToken);

        BrnBlockResponse? granted = null;
        if (facility.NeedsMoreNumbers)
        {
            var block = await central.RequestBrnBlockAsync(
                facility.FacilityId, facility.DeviceId, signer, BrnBlockSize, cancellationToken);

            if (block is { Succeeded: true, Value: { } range })
            {
                facility.GrantNextBlock(range.BlockStart, range.BlockEnd, range.OfficeCode, range.Year);
                granted = range;
                await persist(state, cancellationToken);
            }
            else
            {
                problems.Add(Describe(Strings.Window_Block, block));
            }
        }

        var refreshed = false;
        // Also when no transfer key is held: a tablet whose bundle is still
        // fresh would otherwise wait days for one — unable, meanwhile, to seal
        // an export, which is exactly what a post losing its signal needs.
        if (state.Bundle.RefreshDue(nowUtc, BundleRefreshLead) || state.TransferKey is null)
        {
            var bundle = await central.FetchOfflineBundleAsync(cancellationToken);
            // An answer missing its keys or its list is not a bundle. Taken as
            // one it crashed the window — the births already uploaded in it
            // included — so it is reported like any other failed fetch, and the
            // bundle held stays in use.
            if (bundle is { Succeeded: true, Value: { Keys: { Count: > 0 }, Revocations: not null } fetched })
            {
                var previous = state.Bundle;
                state.Bundle = CachedVerificationBundle.From(
                    fetched.Keys.Select(key => new VerificationKey(key.KeyId, key.PublicKeyPem, key.Active)),
                    [fetched.Revocations],
                    nowUtc);
                // Kept when an older registry sends none: a key already held
                // still seals, and losing it would stop exports for nothing.
                state.TransferKey = fetched.TransferKey ?? state.TransferKey;
                previous.Dispose();
                refreshed = true;
                await persist(state, cancellationToken);
            }
            else
            {
                problems.Add(Describe(Strings.Window_Bundle, bundle));
            }
        }

        return new WindowReport(upload, settlements, granted, refreshed, problems);
    }

    private async Task<CentralOutcome?> SyncAsync(
        ClientSyncState state, DateTime nowUtc, List<OutboxSettlement> settlements, List<string> problems, CancellationToken cancellationToken)
    {
        CentralOutcome? last = null;

        // At most two uploads a window: the one already in flight, then one of
        // whatever was registered since. Records the centre rejected stay queued
        // and go again next window — not round and round inside this one.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (state.InFlight is null)
            {
                if (facility.SendableCount == 0)
                {
                    break;
                }

                state.InFlight = facility.BuildSignedUpload();
                await persist(state, cancellationToken);
            }

            var result = await central.UploadAsync(state.InFlight, cancellationToken);
            last = result.Outcome;

            switch (result.Outcome)
            {
                case CentralOutcome.Succeeded when result.Value is { } response:
                    settlements.Add(facility.Settle(response, nowUtc));
                    state.InFlight = null;
                    await persist(state, cancellationToken);
                    continue;

                case CentralOutcome.Refused:
                    // The same bytes will be refused again, so they are not kept.
                    // The records are: nothing leaves the outbox without the
                    // centre's per-record answer.
                    state.InFlight = null;
                    await persist(state, cancellationToken);
                    problems.Add(Describe(Strings.Window_Upload, result));
                    return last;

                default:
                    // Held, in progress, unauthorised or unreachable: keep the
                    // upload exactly as it is for the next window.
                    problems.Add(Describe(Strings.Window_Upload, result));
                    return last;
            }
        }

        return last;
    }

    private static string Describe<T>(string what, CentralResult<T> result)
    {
        var errors = result.Errors is { Count: > 0 } list
            ? " " + string.Join("; ", list.Select(error => $"{error.Field}: {error.Message}"))
            : "";

        // The registry's own reasons (errors) stay in the registry's words.
        return result.Outcome switch
        {
            CentralOutcome.Held => Language.Format(Strings.Window_Held, what),
            CentralOutcome.InProgress => Language.Format(Strings.Window_InProgress, what),
            CentralOutcome.Unauthorized => Language.Format(Strings.Window_Unauthorized, what),
            CentralOutcome.Unreachable => Language.Format(Strings.Window_Unreachable, what),
            CentralOutcome.Refused => Language.Format(Strings.Window_Refused, what, result.StatusCode, errors),
            CentralOutcome.Succeeded => Language.Format(Strings.Window_Incomplete, what),
            _ => Language.Format(Strings.Window_Other, what, Language.Name(result.Outcome)),
        };
    }
}
