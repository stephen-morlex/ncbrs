using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
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
[Authorize]
[Produces("application/json")]
public class BirthRecordsController(
    NcbrsDbContext db,
    BirthRegistrationService registrations,
    AmendmentService amendments,
    CurrentRegistrarService currentRegistrar,
    CountyLookup districts,
    IOptions<StatutoryRegistrationOptions> statutory,
    DeviceEnrolmentService devices,
    RefusalAudit refusals) : ControllerBase
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
    /// The rules a client needs before it can collect a registration, rather
    /// than after it has tried one.
    ///
    /// **The statutory window is set in law and is therefore configuration**
    /// (draft 4.1) — which meant, until now, that nothing outside the server
    /// knew it. A client had two bad options: hardcode the number, and drift
    /// silently the day the Act is amended; or submit, be refused with
    /// "evidence required", and ask the registrar to fill the form a second
    /// time. The second is worse where it lands hardest — late registration is
    /// the *ordinary* route for a large share of rural births, not an
    /// exception.
    ///
    /// The facility device app needs this for the same reason and more
    /// urgently: it must decide whether to collect evidence while it is
    /// offline and cannot ask anything.
    ///
    /// Literal route segment, so it is matched ahead of <c>{brn}</c>.
    /// </summary>
    [HttpGet("registration-rules", Name = "GetRegistrationRules")]
    [ProducesResponseType(typeof(RegistrationRulesResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    public ActionResult<RegistrationRulesResponse> GetRegistrationRules()
        => new RegistrationRulesResponse(statutory.Value.WindowDays);

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

        // Which device -- or the management site -- this came from is decided
        // by the token, and for a device proved by its signature over these
        // exact bytes: the same rule a sync batch is held to (WS-B9). Before
        // this the online path took the device id on trust, so a stolen token
        // could register as any device, even a revoked one.
        var channel = await devices.CheckRegistrationChannelAsync(
            User,
            envelope.Data.DeviceId,
            envelope.Data.FacilityId,
            await Request.ReadRawAsync(HttpContext.RequestAborted),
            Request.Headers[DeviceSignature.HeaderName],
            HttpContext.RequestAborted);

        if (!channel.Accepted)
        {
            // Audited though nothing is registered: registering as a device from
            // the wrong channel, or without its key, is what a stolen token
            // looks like. Recorded as a refusal so it survives the rollback of
            // the request it refused.
            refusals.Record(new AuditLog
            {
                EntityType = nameof(BirthRecord),
                EntityId = envelope.Data.Brn,
                CountyCode = await districts.ForFacilityAsync(envelope.Data.FacilityId, HttpContext.RequestAborted),
                Action = $"DeviceRefused:{channel.Outcome}",
                UserId = registrar.RegistrarId,
                DeviceId = envelope.Data.DeviceId,
                TransactionId = TransactionContext.Get(HttpContext)?.TransactionId
            });

            await db.SaveChangesAsync(HttpContext.RequestAborted);

            return ApiErrors.Result(ApiErrors.Single(
                StatusCodes.Status403Forbidden, "Device not permitted to register.",
                "data.deviceId", channel.Detail));
        }

        // A device that has just proved itself has been seen, exactly as on
        // sync -- which also resolves a silence alert raised against it.
        await devices.MarkSeenAsync(channel.Device, HttpContext.RequestAborted);

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
                ReceivedAtUtc: record.CreatedAtUtc,
                RegisteredAtUtc: record.RegisteredAtUtc,
                StatutoryWindowDays: record.StatutoryWindowDays,
                ProvisionalIdentifier: record.ProvisionalIdentifier));
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
            // Whether a certificate can be issued at all, and whether one
            // already was. Both are things a registrar is asked about while
            // the family is standing there.
            .Include(b => b.LateRegistration)
            .Include(b => b.Certificates)
            // Loaded so a correction can be made against what the record
            // actually says. People are separate rows, so without these the
            // names come back null and read as "not recorded".
            .Include(b => b.MotherPerson)
            .Include(b => b.FatherPerson)
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
            // Was never populated here, only on the registration response --
            // so a record looked up afterwards showed no sign it had been
            // registered late, and no sign its certificate was being withheld
            // pending verification. That is precisely the thing a family must
            // not be sent away without being told.
            LateRegistration: record.LateRegistration is null
                ? null
                : new LateRegistrationSummary(
                    record.LateRegistration.DaysLate,
                    record.LateRegistration.WindowDaysAtFiling,
                    record.LateRegistration.Status,
                    record.LateRegistration.EvidenceType),
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
            ReceivedAtUtc: record.CreatedAtUtc,
            RegisteredAtUtc: record.RegisteredAtUtc,
            StatutoryWindowDays: record.StatutoryWindowDays,
            ProvisionalIdentifier: record.ProvisionalIdentifier,
            Certificate: CertificateStateOf(record),
            // The rest of what a correction can change, so a correction form
            // can show what the register currently says rather than asking a
            // registrar to retype it from memory.
            MotherFullName: record.MotherPerson?.FullName,
            FatherFullName: record.FatherPerson?.FullName,
            BirthWeightGrams: record.BirthWeightGrams,
            GestationalAgeWeeks: record.GestationalAgeWeeks,
            BirthOrder: record.BirthOrder);
    }

    /// <summary>
    /// The current certificate, if any.
    ///
    /// A record accumulates certificates over time — an amendment withdraws
    /// the one it contradicts and a replacement is issued — so the latest by
    /// issue date is the one a family is being asked about. The withdrawn
    /// predecessors are history, and belong in the amendment trail rather than
    /// on the record header.
    /// </summary>
    private static CertificateState? CertificateStateOf(BirthRecord record)
    {
        var certificate = record.Certificates
            .OrderByDescending(issued => issued.IssueDateUtc)
            .FirstOrDefault();

        return certificate is null
            ? null
            : new CertificateState(
                certificate.IssueDateUtc,
                certificate.WithdrawnAtUtc,
                certificate.WithdrawnReason,
                certificate.ReprintCount);
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
    /// How many windows of candidate numbers a grant will look past before it
    /// gives up. Each pass skips at least one used number, so a facility whose
    /// counter is behind by less than this catches up in one request.
    /// </summary>
    private const int MaxUsedNumberProbes = 8;

    /// <summary>
    /// Moves <c>BrnBlockNextAvailable</c> past any number already carried by a
    /// record, and answers how many it skipped.
    ///
    /// **Checked at grant time rather than maintained on write, deliberately.**
    /// The alternative — advancing the counter whenever a record arrives with a
    /// BRN above it — would make a device's own number move a facility's
    /// counter, and one device with a bad clock or a bad build could burn a
    /// whole range by sending a single high value. Decision #2 and the BRN
    /// confirmation rules both turn on the centre never trusting a
    /// device-supplied number; this keeps that intact by asking the register
    /// what it actually holds.
    ///
    /// Bounded work: it looks only at the window it is about to hand out, and
    /// only at numeric BRNs — a provisional identifier was never drawn from a
    /// block and cannot collide with one.
    /// </summary>
    private async Task<int> AdvancePastUsedAsync(
        Facility facility, int blockSize, CancellationToken cancellationToken)
    {
        var skipped = 0;

        for (var probe = 0; probe < MaxUsedNumberProbes; probe++)
        {
            var start = facility.BrnBlockNextAvailable;

            if (start > facility.BrnBlockEnd)
            {
                return skipped;
            }

            var end = Math.Min(start + blockSize - 1, facility.BrnBlockEnd);

            var candidates = new List<string>();
            for (var number = start; number <= end; number++)
            {
                candidates.Add(number.ToString(CultureInfo.InvariantCulture));
            }

            var taken = await db.BirthRecords
                .Where(record => candidates.Contains(record.Brn))
                .Select(record => record.Brn)
                .ToListAsync(cancellationToken);

            if (taken.Count == 0)
            {
                return skipped;
            }

            skipped += taken.Count;

            // Past the highest one found, not merely past the first. The gap
            // between is free, but re-probing it costs a round trip to
            // rediscover numbers this pass already knows about.
            var highest = taken
                .Select(value => long.TryParse(value, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0)
                .Max();

            facility.BrnBlockNextAvailable = highest + 1;
        }

        return skipped;
    }

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
        if (!await currentRegistrar.CanActForFacilityAsync(registrar, facilityId, HttpContext.RequestAborted))
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

            // Numbers already on a record are not available to grant, whatever
            // the counter says.
            //
            // BrnBlockNextAvailable tracks what has been *handed out*, not what
            // has been *used*, and those diverge: a record can enter carrying a
            // BRN from this facility's range without a grant ever happening --
            // a sync from a device provisioned elsewhere, a restored dump, a
            // seeded environment. The counter never learns, and the next grant
            // hands out numbers that are already registered.
            //
            // For the online form that surfaces as a refusal the registrar can
            // retry past. For a device it is far worse: it takes the block
            // offline, registers a fortnight of births against numbers that
            // every one of them will be refused on, and nobody finds out until
            // it syncs. That is the collision decision #2 exists to prevent.
            var skipped = await AdvancePastUsedAsync(facility, blockSize, HttpContext.RequestAborted);

            var start = facility.BrnBlockNextAvailable;

            if (start > facility.BrnBlockEnd)
            {
                return ApiErrors.Result(ApiErrors.Single(
                    StatusCodes.Status409Conflict, "BRN range exhausted.",
                    "facilityId",
                    $"Facility '{facilityId}' has exhausted its pre-approved BRN range (ceiling {facility.BrnBlockEnd}) "
                    + "once numbers already on a record are excluded. A new range must be assigned by the central "
                    + "registry before more BRNs can be issued."));
            }

            // Clamp to the facility's ceiling rather than fail outright when
            // only a partial block remains.
            var end = Math.Min(start + blockSize - 1, facility.BrnBlockEnd);
            facility.BrnBlockNextAvailable = end + 1;

            db.AuditLogs.Add(new AuditLog
            {
                EntityType = nameof(Facility),
                CountyCode = await districts.ForFacilityAsync(facilityId, HttpContext.RequestAborted),
                EntityId = facilityId.ToString(),
                // Skipping is recorded rather than done quietly. A counter
                // behind the register means records reached this facility's
                // range outside the grant path, and somebody should be able to
                // find out that happened and how often.
                Action = skipped == 0
                    ? "BrnBlockGranted"
                    : $"BrnBlockGranted:skipped={skipped}",
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
