using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NCBRS.Data;
using NCBRS.Devices;
using NCBRS.Events;
using NCBRS.Kafka;
using NCBRS.Middleware;
using NCBRS.Models;
using NCBRS.Services;

namespace NCBRS.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize(Policy = NcbrsRoles.CanRegisterBirths)]
[Produces("application/json")]
public class SyncController(
    NcbrsDbContext db,
    BirthRegistrationService registrations,
    CurrentRegistrarService currentRegistrar,
    IEventPublisher eventPublisher,
    ProvisionalRecordReconciler reconciler,
    DeviceEnrolmentService devices,
    IValidator<RegisterBirthRequest> recordValidator) : ControllerBase
{
    /// <summary>
    /// The body exactly as it arrived, for signature verification.
    ///
    /// Read back from the buffered stream rather than re-serialised from the
    /// bound model: a re-serialisation is a second opinion about what the
    /// device sent, and the signature is over the first.
    /// </summary>
    private async Task<ReadOnlyMemory<byte>> RawBodyAsync()
    {
        if (!Request.Body.CanSeek)
        {
            return ReadOnlyMemory<byte>.Empty;
        }

        Request.Body.Position = 0;

        using var buffer = new MemoryStream();
        await Request.Body.CopyToAsync(buffer, HttpContext.RequestAborted);

        return buffer.ToArray();
    }

    /// <summary>
    /// Receives a device's offline outbox. Section 6.3 of the NCBRS draft.
    ///
    /// Processing is per record, not per batch: a village post that spent two
    /// weeks offline must not lose forty-nine good registrations because the
    /// fiftieth has a typo. Each record succeeds, is recognised as already
    /// held, or is rejected with its own reasons, and the device gets the
    /// full breakdown so it knows exactly what to clear from its outbox.
    ///
    /// The batch as a whole is still guarded by the request's transaction id,
    /// so re-uploading an outbox the device wasn't sure had landed replays
    /// the original answer rather than reprocessing anything.
    /// </summary>
    [HttpPost("batches")]
    [ProducesResponseType(typeof(SyncBatchResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<SyncBatchResponse>> SubmitBatch(ApiRequest<SyncBatchRequest> envelope)
    {
        var batch = envelope.Data;
        var transactionId = TransactionContext.Get(HttpContext)?.TransactionId;

        var registrar = await currentRegistrar.GetAsync(HttpContext.RequestAborted);
        if (registrar is null)
        {
            return ApiErrors.Result(ApiErrors.Single(
                StatusCodes.Status403Forbidden, "Account not provisioned.",
                "registrar", "This account is not linked to a registrar in the registry."));
        }

        var facility = await db.Facilities.FindAsync([batch.FacilityId], HttpContext.RequestAborted);
        if (facility is null)
        {
            return ApiErrors.Result(ApiErrors.Single(
                StatusCodes.Status404NotFound, "Facility not found.",
                "data.facilityId", $"No facility exists with id '{batch.FacilityId}'."));
        }

        // Rejected whole rather than per record: a device uploading someone
        // else's facility's outbox is a misconfiguration, not a data problem,
        // and processing half of it would scatter records across facilities.
        if (!currentRegistrar.CanActForFacility(registrar, facility.FacilityId))
        {
            return ApiErrors.Result(ApiErrors.Single(
                StatusCodes.Status403Forbidden, "Not permitted for this facility.",
                "data.facilityId", $"You are not permitted to sync batches for facility '{batch.FacilityId}'."));
        }

        // WS-B9. Checked after the facility and the registrar, and before a
        // single record is touched: an unenrolled device is not a batch with
        // some bad rows in it, it is a batch the centre has no reason to
        // believe came from a device at all.
        var deviceCheck = await devices.CheckAsync(
            batch.DeviceId,
            batch.FacilityId,
            await RawBodyAsync(),
            Request.Headers[DeviceSignature.HeaderName],
            HttpContext.RequestAborted);

        if (!deviceCheck.Accepted)
        {
            db.AuditLogs.Add(new AuditLog
            {
                EntityType = nameof(SyncBatch),
                EntityId = batch.DeviceId,
                Action = $"DeviceRefused:{deviceCheck.Outcome}",
                UserId = registrar.RegistrarId,
                DeviceId = batch.DeviceId,
                TransactionId = transactionId
            });

            // Audited even though nothing is registered. A rejected batch
            // from an unknown device is the single most interesting thing
            // this endpoint sees, and discarding it would leave an attempted
            // upload from a stolen token invisible.
            await db.SaveChangesAsync(HttpContext.RequestAborted);

            return ApiErrors.Result(ApiErrors.Single(
                StatusCodes.Status403Forbidden, "Device not permitted to sync.",
                "data.deviceId", deviceCheck.Detail));
        }

        await devices.MarkSeenAsync(deviceCheck.Device, HttpContext.RequestAborted);

        var syncBatch = new SyncBatch
        {
            DeviceId = batch.DeviceId,
            FacilityId = batch.FacilityId,
            UploadedByRegistrarId = registrar.RegistrarId,
            SubmittedAtUtc = DateTime.UtcNow,
            RecordCount = batch.Records.Count,
            Status = SyncBatchStatus.Processing
        };
        db.SyncBatches.Add(syncBatch);
        await db.SaveChangesAsync(HttpContext.RequestAborted);

        var outcomes = new List<SyncRecordOutcome>(batch.Records.Count);

        foreach (var record in batch.Records)
        {
            outcomes.Add(await ProcessRecordAsync(record, batch, registrar, transactionId, syncBatch.SyncBatchId));
        }

        var registered = outcomes.Count(outcome => outcome.Status == SyncRecordStatus.Registered);
        var duplicates = outcomes.Count(outcome => outcome.Status == SyncRecordStatus.Duplicate);
        var rejected = outcomes.Count(outcome => outcome.Status == SyncRecordStatus.Rejected);

        // A record that rolled back cleared the change tracker, and that
        // detaches this batch along with it. Without re-attaching, the status
        // assigned below would never reach the database and the row would sit
        // at Processing forever while the response said Reconciled.
        if (db.Entry(syncBatch).State == EntityState.Detached)
        {
            db.SyncBatches.Attach(syncBatch);
        }

        // Reconciled means the batch was processed, not that every record was
        // perfect -- the per-record breakdown carries that. Failed is reserved
        // for a batch where nothing at all could be accepted.
        syncBatch.Status = registered + duplicates > 0 ? SyncBatchStatus.Reconciled : SyncBatchStatus.Failed;

        db.AuditLogs.Add(new AuditLog
        {
            EntityType = nameof(SyncBatch),
            EntityId = syncBatch.SyncBatchId.ToString(),
            Action = "SyncBatchProcessed",
            UserId = registrar.RegistrarId,
            DeviceId = batch.DeviceId,
            TransactionId = transactionId
        });

        // Chain of custody for the offline tier: which device delivered what,
        // and what became of it. Enqueued before SaveChanges so the audit
        // event commits with the batch it describes -- a sync that is
        // recorded as reconciled but never reaches the audit stream is
        // precisely the gap this topic exists to close.
        eventPublisher.EnqueueSyncBatchProcessed(
            new SyncBatchProcessedEvent(
                syncBatch.SyncBatchId,
                batch.DeviceId,
                batch.FacilityId,
                facility.DistrictId,
                registrar.RegistrarId,
                batch.Records.Count,
                registered,
                duplicates,
                rejected,
                syncBatch.Status.ToString(),
                DateTime.UtcNow,
                transactionId),
            facility.DistrictId);

        await db.SaveChangesAsync(HttpContext.RequestAborted);

        return new SyncBatchResponse(
            syncBatch.SyncBatchId,
            syncBatch.Status,
            batch.Records.Count,
            registered,
            duplicates,
            rejected,
            outcomes);
    }

    /// <summary>
    /// Runs one record inside its own savepoint so a failure discards only
    /// that record's partial writes. Without this, a record that failed after
    /// inserting its Person rows would leave them orphaned in the same
    /// transaction as everyone else's successful registrations.
    /// </summary>
    private async Task<SyncRecordOutcome> ProcessRecordAsync(
        SyncBirthRecord entry,
        SyncBatchRequest batch,
        Registrar uploader,
        Guid? transactionId,
        Guid syncBatchId)
    {
        var record = entry.Birth;

        var validation = await recordValidator.ValidateAsync(record, HttpContext.RequestAborted);
        if (!validation.IsValid)
        {
            return Rejected(record.Brn, validation.Errors.Select(failure =>
                new ApiError(CamelCase(failure.PropertyName), failure.ErrorMessage)));
        }

        // A record claiming a different facility than the batch it arrived in
        // means the device mixed up its outbox; accepting it would file a
        // birth against the wrong facility.
        if (record.FacilityId != batch.FacilityId)
        {
            return Rejected(record.Brn, [new ApiError(
                "facilityId",
                $"Record facilityId '{record.FacilityId}' does not match the batch facilityId '{batch.FacilityId}'.")]);
        }

        var (author, attributionError) = await ResolveAuthorAsync(entry, batch, uploader);
        if (attributionError is not null)
        {
            return Rejected(record.Brn, [attributionError]);
        }

        var ambient = db.Database.CurrentTransaction;
        var savepoint = $"sync_{Guid.NewGuid():N}";

        // Only needed when an outer transaction is in play (the idempotency
        // filter opens one for a caller-supplied transaction id). Without one,
        // each SaveChanges is already its own transaction.
        if (ambient is not null)
        {
            await ambient.CreateSavepointAsync(savepoint, HttpContext.RequestAborted);
        }

        try
        {
            var result = await registrations.RegisterAsync(
                record, author!, transactionId, HttpContext.RequestAborted, syncBatchId);

            if (result.Outcome == RegistrationOutcome.Duplicate)
            {
                return new SyncRecordOutcome(record.Brn, SyncRecordStatus.Duplicate);
            }

            if (!result.Succeeded)
            {
                await RollbackAsync(ambient, savepoint);
                return Rejected(record.Brn, [new ApiError("record", result.Detail ?? "Registration failed.")]);
            }

            // A record that arrived under a provisional identifier is given a
            // real BRN here (draft 6.3). Done on sync rather than left for a
            // human: assigning the next number from a range the facility
            // already holds is mechanical, and the act is fully audited, so
            // "not silently merged" is satisfied without leaving families
            // uncertificated while a queue is worked.
            var assigned = await reconciler.ReconcileAsync(
                result.Record!, author!.RegistrarId, transactionId, HttpContext.RequestAborted);

            if (ambient is not null)
            {
                await ambient.ReleaseSavepointAsync(savepoint, HttpContext.RequestAborted);
            }

            // The device needs this to know whether its number is now
            // permanent, which is the answer sync exists to bring back.
            return new SyncRecordOutcome(
                record.Brn,
                SyncRecordStatus.Registered,
                BrnConfirmed: assigned.Assigned || (result.BrnReconciliation?.Confirmed ?? false),
                BrnConfirmationDetail: assigned.Assigned
                    ? $"Provisional identifier '{result.Record!.ProvisionalIdentifier}' was reconciled to "
                      + $"BRN '{assigned.AssignedBrn}'."
                    : assigned.Detail ?? result.BrnReconciliation?.Detail,
                AssignedBrn: assigned.AssignedBrn);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await RollbackAsync(ambient, savepoint);
            db.ChangeTracker.Clear();

            return Rejected(record.Brn, [new ApiError("record", "This record could not be processed.")]);
        }
    }

    /// <summary>
    /// Decides who a synced record is credited to.
    ///
    /// The author is claimed by the device, not proved by a token, so it is
    /// constrained rather than trusted. Three checks bound what a compromised
    /// device can assert: the registrar must exist, must belong to the same
    /// facility as the batch, and must have an offline PIN set -- only
    /// someone who can actually unlock a device offline can plausibly have
    /// authored an offline registration.
    ///
    /// What remains is that an uploader could credit a colleague at their own
    /// facility. That residual is accepted deliberately: the alternative,
    /// crediting every record to the uploader, is not neutral -- it puts a
    /// name on a legal record that is affirmatively wrong. SyncBatch records
    /// who uploaded, so the two attributions stay distinguishable.
    /// </summary>
    private async Task<(Registrar? Author, ApiError? Error)> ResolveAuthorAsync(
        SyncBirthRecord entry,
        SyncBatchRequest batch,
        Registrar uploader)
    {
        if (entry.RegisteredByRegistrarId is not { } claimedId)
        {
            // A device that does not track per-user unlock falls back to the
            // uploader, which is at least verified.
            return (uploader, null);
        }

        if (claimedId == uploader.RegistrarId)
        {
            return (uploader, null);
        }

        var author = await db.Registrars.FindAsync([claimedId], HttpContext.RequestAborted);

        if (author is null)
        {
            return (null, new ApiError("registeredByRegistrarId",
                $"No registrar exists with id '{claimedId}'."));
        }

        if (author.FacilityId != batch.FacilityId)
        {
            return (null, new ApiError("registeredByRegistrarId",
                $"Registrar '{claimedId}' does not belong to facility '{batch.FacilityId}'."));
        }

        if (author.CredentialHash is null)
        {
            return (null, new ApiError("registeredByRegistrarId",
                $"Registrar '{claimedId}' has no offline PIN set and cannot have authored an offline registration."));
        }

        return (author, null);
    }

    private async Task RollbackAsync(Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction? ambient, string savepoint)
    {
        if (ambient is not null)
        {
            await ambient.RollbackToSavepointAsync(savepoint, HttpContext.RequestAborted);
        }

        // Drop whatever the failed record staged, so the next record in the
        // batch doesn't carry it along into its own SaveChanges.
        db.ChangeTracker.Clear();
    }

    private static SyncRecordOutcome Rejected(string brn, IEnumerable<ApiError> errors)
        => new(brn, SyncRecordStatus.Rejected, errors.ToList());

    private static string CamelCase(string propertyName)
        => string.IsNullOrEmpty(propertyName) || !char.IsUpper(propertyName[0])
            ? propertyName
            : char.ToLowerInvariant(propertyName[0]) + propertyName[1..];
}
