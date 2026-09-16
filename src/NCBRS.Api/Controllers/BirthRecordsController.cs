using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NCBRS.Data;
using NCBRS.Events;
using NCBRS.Kafka;
using NCBRS.Middleware;
using NCBRS.Models;
using NCBRS.Services;

namespace NCBRS.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
[Produces("application/json")]
public class BirthRecordsController(
    NcbrsDbContext db,
    BirthRegistrationService registrations,
    AmendmentService amendments,
    CurrentRegistrarService currentRegistrar,
    DistrictLookup districts) : ControllerBase
{
    /// <summary>
    /// An account that authenticated but has no registrar record was never
    /// provisioned in the registry. It must not be able to write, and saying
    /// so plainly beats a foreign-key error later.
    /// </summary>
    private static ObjectResult NotProvisioned()
        => ApiErrors.Result(ApiErrors.Single(
            StatusCodes.Status403Forbidden, "Account not provisioned.",
            "registrar", "This account is not linked to a registrar in the registry."));

    /// <summary>
    /// Registers a live birth. Mirrors the workflow in Section 5.1/5.2 of the
    /// NCBRS draft: the BRN was already allocated *on the device* from its
    /// reserved block before this call ever happens (including fully
    /// offline) -- this endpoint validates and reconciles it centrally,
    /// it does not generate the BRN itself.
    /// </summary>
    [HttpPost("register", Name = "RegisterBirth")]
    [Authorize(Policy = NcbrsRoles.CanRegisterBirths)]
    [ProducesResponseType(typeof(BirthRecordResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<BirthRecordResponse>> Register(ApiRequest<RegisterBirthRequest> envelope)
    {
        var registrar = await currentRegistrar.GetAsync(HttpContext.RequestAborted);
        if (registrar is null)
        {
            return NotProvisioned();
        }

        var result = await registrations.RegisterAsync(
            envelope.Data,
            registrar,
            TransactionContext.Get(HttpContext)?.TransactionId,
            HttpContext.RequestAborted);

        if (!result.Succeeded)
        {
            return MapFailure(result, envelope.Data);
        }

        var record = result.Record!;

        return CreatedAtAction(nameof(GetByBrn), new { brn = record.Brn },
            new BirthRecordResponse(
                record.BirthRecordId, record.Brn, envelope.Data.ChildFullName,
                record.DateOfBirth, record.Sex, record.Status, result.LateRegistration,
                record.ConfirmedAtUtc,
                Annulment: null,
                RegisteredByRegistrarId: record.RegisteredByRegistrarId,
                RegisteredByRegistrarName: registrar?.DisplayName,
                ReceivedAtUtc: record.CreatedAtUtc));
    }

    /// <summary>
    /// Turns a registration outcome into the HTTP status that describes it.
    /// The sync endpoint maps the same outcomes to per-record results instead.
    /// </summary>
    private ObjectResult MapFailure(RegistrationResult result, RegisterBirthRequest request)
        => result.Outcome switch
        {
            RegistrationOutcome.FacilityNotFound => ApiErrors.Result(ApiErrors.Single(
                StatusCodes.Status404NotFound, "Facility not found.",
                "data.facilityId", result.Detail!)),

            RegistrationOutcome.FacilityNotPermitted => ApiErrors.Result(ApiErrors.Single(
                StatusCodes.Status403Forbidden, "Not permitted for this facility.",
                "data.facilityId", result.Detail!)),

            RegistrationOutcome.Duplicate => ApiErrors.Result(ApiErrors.Single(
                StatusCodes.Status409Conflict, "BRN already registered.",
                "data.brn", result.Detail!)),

            // The message names the days and the window, so a registrar sees
            // why the form changed under them rather than just that it did.
            RegistrationOutcome.LateRegistrationEvidenceRequired => ApiErrors.Result(ApiErrors.Single(
                StatusCodes.Status400BadRequest, "Late registration evidence required.",
                "data.lateRegistration", result.Detail!)),

            RegistrationOutcome.ImplausibleCaptureTime => ApiErrors.Result(ApiErrors.Single(
                StatusCodes.Status400BadRequest, "Implausible capture time.",
                "data.registeredAtUtc", result.Detail!)),

            _ => ApiErrors.Result(ApiErrors.Single(
                StatusCodes.Status400BadRequest, "Registration failed.",
                "data", result.Detail ?? "The registration could not be completed."))
        };

    [HttpGet("{brn}", Name = "GetBirthRecord")]
    [ProducesResponseType(typeof(BirthRecordResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<BirthRecordResponse>> GetByBrn(string brn)
    {
        // Resolves on the provisional identifier too: a family may be holding
        // the slip a device printed before the record had a real BRN, and a
        // clerk handed it months later must still find the record.
        var record = await db.BirthRecords
            .Include(b => b.ChildPerson)
            .Include(b => b.Annulment)
            // Who filed it. Loaded with the record rather than looked up
            // afterwards: a disputed record is read once, and the name is
            // part of what makes it answerable.
            .Include(b => b.RegisteredByRegistrar)
            .FirstOrDefaultAsync(b => b.Brn == brn || b.ProvisionalIdentifier == brn);

        if (record is null || record.ChildPerson is null)
        {
            return ApiErrors.Result(ApiErrors.Single(
                StatusCodes.Status404NotFound, "Birth record not found.",
                "brn", $"No birth record exists with BRN '{brn}'."));
        }

        return new BirthRecordResponse(
            record.BirthRecordId, record.Brn, record.ChildPerson.FullName,
            record.DateOfBirth, record.Sex, record.Status,
            ConfirmedAtUtc: record.ConfirmedAtUtc,
            Annulment: record.Annulment is null
                ? null
                : new AnnulmentSummary(
                    record.Annulment.Reason,
                    record.Annulment.Justification,
                    record.Annulment.AuthorityReference,
                    record.Annulment.AnnulledAtUtc),
            RegisteredByRegistrarId: record.RegisteredByRegistrarId,
            RegisteredByRegistrarName: record.RegisteredByRegistrar?.DisplayName,
            ReceivedAtUtc: record.CreatedAtUtc);
    }

    /// <summary>
    /// Corrects a registered birth (draft Section 6.5).
    ///
    /// PATCH rather than PUT, and every field optional, because a correction
    /// names only what was wrong. A field left out is untouched; a field sent
    /// with the value already on file records nothing.
    ///
    /// The BRN is not in the request body on purpose. It identifies the
    /// record being corrected and is never itself correctable -- it is the
    /// permanent identifier the whole offline block-allocation design exists
    /// to keep stable, and it may already be printed on a certificate.
    ///
    /// Corrections run on two tracks (draft 5.3). Fields the certificate
    /// signature does not cover take effect at once and come back under
    /// "applied"; the child's name, date of birth and sex wait for a reviewer
    /// and come back under "pendingApproval". A submission that is entirely
    /// pending answers <strong>202 Accepted</strong>, not 200 -- the record
    /// has not changed yet, and a device must not tell a family otherwise.
    /// </summary>
    [HttpPatch("{brn}", Name = "AmendBirthRecord")]
    [Authorize(Policy = NcbrsRoles.CanRegisterBirths)]
    [ProducesResponseType(typeof(AmendBirthRecordResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(AmendBirthRecordResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<AmendBirthRecordResponse>> Amend(
        string brn,
        ApiRequest<AmendBirthRecordRequest> envelope)
    {
        var registrar = await currentRegistrar.GetAsync(HttpContext.RequestAborted);
        if (registrar is null)
        {
            return NotProvisioned();
        }

        var result = await amendments.AmendAsync(
            brn,
            envelope.Data,
            registrar,
            TransactionContext.Get(HttpContext)?.TransactionId,
            HttpContext.RequestAborted);

        if (result.Succeeded)
        {
            return result.EverythingPending
                ? StatusCode(StatusCodes.Status202Accepted, result.Response)
                : Ok(result.Response);
        }

        return result.Result switch
        {
            AmendmentResult.BirthRecordNotFound => ApiErrors.Result(ApiErrors.Single(
                StatusCodes.Status404NotFound, "Birth record not found.", "brn", result.Detail!)),

            AmendmentResult.NotPermitted => ApiErrors.Result(ApiErrors.Single(
                StatusCodes.Status403Forbidden, "Not permitted for this facility.", "brn", result.Detail!)),

            AmendmentResult.RecordSuperseded => ApiErrors.Result(ApiErrors.Single(
                StatusCodes.Status409Conflict, "Record superseded.", "brn", result.Detail!)),

            AmendmentResult.RecordAnnulled => ApiErrors.Result(ApiErrors.Single(
                StatusCodes.Status409Conflict, "Registration annulled.", "brn", result.Detail!)),

            // Nothing differing from what is on file is a no-op, not a
            // failure of the caller's -- but it must not report an amendment
            // that did not happen.
            _ => ApiErrors.Result(ApiErrors.Single(
                StatusCodes.Status400BadRequest, "Nothing to amend.", "data", result.Detail!))
        };
    }

    /// <summary>
    /// The correction history of a record: what changed, from what, by whom
    /// and why. This is the view an auditor or a court needs, and the reason
    /// amendments store the previous value instead of overwriting it.
    ///
    /// Includes corrections that were refused. A change someone proposed and
    /// a reviewer turned down is part of a record's history too, and often
    /// the part a dispute turns on.
    /// </summary>
    [HttpGet("{brn}/amendments", Name = "GetBirthRecordAmendments")]
    [ProducesResponseType(typeof(IReadOnlyList<AmendmentHistoryEntry>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IReadOnlyList<AmendmentHistoryEntry>>> GetAmendments(string brn)
    {
        var record = await db.BirthRecords
            .FirstOrDefaultAsync(b => b.Brn == brn, HttpContext.RequestAborted);

        if (record is null)
        {
            return ApiErrors.Result(ApiErrors.Single(
                StatusCodes.Status404NotFound, "Birth record not found.",
                "brn", $"No birth record exists with BRN '{brn}'."));
        }

        return await db.BirthRecordAmendments
            .Where(amendment => amendment.BirthRecordId == record.BirthRecordId)
            .OrderBy(amendment => amendment.AmendedAtUtc)
            .Select(amendment => new AmendmentHistoryEntry(
                amendment.BirthRecordAmendmentId,
                amendment.AmendmentRequestId,
                amendment.Field,
                amendment.PreviousValue,
                amendment.NewValue,
                amendment.Reason,
                amendment.Status,
                amendment.AmendedByRegistrarId,
                amendment.AmendedByRegistrar!.DisplayName,
                amendment.AmendedAtUtc,
                amendment.AppliedAtUtc,
                amendment.ReviewedByRegistrarId,
                amendment.ReviewedByRegistrar != null ? amendment.ReviewedByRegistrar.DisplayName : null,
                amendment.ReviewedAtUtc,
                amendment.ReviewNote,
                amendment.TransactionId))
            .ToListAsync(HttpContext.RequestAborted);
    }

    private const int MaxBrnBlockRequestSize = 10_000;
    private const int MaxBrnBlockConcurrencyRetries = 5;

    /// <summary>
    /// Hands out the next block of BRNs to a facility device so it can keep
    /// registering births while offline. Section 6.3/6.6 of the NCBRS draft.
    ///
    /// BrnBlockNextAvailable is a [ConcurrencyCheck] column, so a concurrent
    /// grant for the same facility throws DbUpdateConcurrencyException here
    /// instead of both callers silently walking away with overlapping
    /// ranges; we retry with freshly-read data rather than surface that as
    /// an error to the caller. The grant is also capped at the facility's
    /// pre-approved BrnBlockEnd ceiling -- raising that ceiling is a
    /// separate, not-yet-implemented central-registry process (see
    /// CLAUDE.md's "central dedup check" item).
    /// </summary>
    [HttpPost("{facilityId:guid}/request-brn-block", Name = "RequestBrnBlock")]
    [Authorize(Policy = NcbrsRoles.CanRegisterBirths)]
    [ProducesResponseType(typeof(BrnBlockResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<BrnBlockResponse>> RequestBrnBlock(
        Guid facilityId,
        ApiRequest<BrnBlockRequest> envelope)
    {
        var registrar = await currentRegistrar.GetAsync(HttpContext.RequestAborted);
        if (registrar is null)
        {
            return NotProvisioned();
        }

        // BRNs are the registry's scarce, permanent identifiers -- a caller
        // must not be able to drain another facility's range.
        if (!currentRegistrar.CanActForFacility(registrar, facilityId))
        {
            return ApiErrors.Result(ApiErrors.Single(
                StatusCodes.Status403Forbidden, "Not permitted for this facility.",
                "facilityId", $"You are not permitted to request BRN blocks for facility '{facilityId}'."));
        }

        // blockSize range is enforced by BrnBlockRequestValidator.
        var blockSize = envelope.Data.BlockSize;
        var deviceId = envelope.Data.DeviceId;

        for (var attempt = 0; attempt < MaxBrnBlockConcurrencyRetries; attempt++)
        {
            var facility = await db.Facilities.FindAsync(facilityId);
            if (facility is null)
            {
                return ApiErrors.Result(ApiErrors.Single(
                    StatusCodes.Status404NotFound, "Facility not found.",
                    "facilityId", $"No facility exists with id '{facilityId}'."));
            }

            var start = facility.BrnBlockNextAvailable;
            if (start > facility.BrnBlockEnd)
            {
                return ApiErrors.Result(ApiErrors.Single(
                    StatusCodes.Status409Conflict, "BRN range exhausted.",
                    "facilityId",
                    $"Facility '{facilityId}' has exhausted its pre-approved BRN range (ceiling {facility.BrnBlockEnd}). "
                    + "A new range must be assigned by the central registry before more BRNs can be issued."));
            }

            // Clamp to the facility's ceiling rather than fail outright when
            // only a partial block remains.
            var end = Math.Min(start + blockSize - 1, facility.BrnBlockEnd);
            facility.BrnBlockNextAvailable = end + 1;

            db.AuditLogs.Add(new AuditLog
            {
                EntityType = nameof(Facility),
                DistrictId = await districts.ForFacilityAsync(facilityId, HttpContext.RequestAborted),
                EntityId = facilityId.ToString(),
                Action = "BrnBlockGranted",
                // The endpoint doesn't currently require the caller to
                // identify its device; record what's available rather than
                // inventing a value.
                DeviceId = deviceId ?? "unspecified",
                TransactionId = TransactionContext.Get(HttpContext)?.TransactionId
            });

            try
            {
                await db.SaveChangesAsync();
                return new BrnBlockResponse(facilityId, start, end);
            }
            catch (DbUpdateConcurrencyException)
            {
                // Another request already advanced BrnBlockNextAvailable for
                // this facility since we read it. Drop everything we staged
                // this attempt and retry against fresh data.
                db.ChangeTracker.Clear();
            }
        }

        return ApiErrors.Result(ApiErrors.Single(
            StatusCodes.Status409Conflict, "BRN block contention.",
            "facilityId", $"Facility '{facilityId}' is under high contention for BRN blocks; please retry."));
    }
}
