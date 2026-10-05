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
    DeviceChannelGate channelGate) : ControllerBase
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
        // exact bytes: the same rule a sync batch is held to (WS-B9).
        var refused = await channelGate.RefuseUnlessPermittedAsync(
            HttpContext, registrar, envelope.Data.DeviceId, envelope.Data.FacilityId,
            nameof(BirthRecord), envelope.Data.Brn);
        if (refused is not null)
        {
            return refused;
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
        // A composed number is read as typed at a counter -- lower case, a
        // space where the paper broke the line -- and looked up as the
        // registry writes it. Anything else is looked up as given: a
        // provisional identifier carries the device's id, case and all.
        var reading = BrnFormat.Read(brn, out _);
        if (reading is BrnReading.Composed or BrnReading.Mistyped)
        {
            brn = BrnFormat.Normalise(brn);
        }

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

        // Said as mistyped, not as "not found": the check character exists so
        // that a misread number is caught as a misreading, and "no such
        // birth" would send a family away believing their registration lost.
        if (record is null && reading is BrnReading.Mistyped)
        {
            return ApiErrors.Result(ApiErrors.Single(
                StatusCodes.Status404NotFound, "This number is mistyped.",
                "brn", $"'{brn}' does not check: a character was probably misread or mistyped, or two "
                       + "were swapped. Read it again from the slip or certificate."));
        }

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
            BirthOrder: record.BirthOrder,
            Details: await DetailsOfAsync(record));
    }

    /// <summary>
    /// The fuller registration, or null on a record registered before it was
    /// asked. This lookup is open to any signed-in caller -- a family carries
    /// the number between facilities -- so addresses, document numbers and
    /// certificate references go only to a caller who may act for the record's
    /// facility; anyone else is told they were withheld.
    /// </summary>
    private async Task<RegistrationDetails?> DetailsOfAsync(BirthRecord record)
    {
        var child = record.ChildPerson!;
        var recorded = record.PlaceOfBirthKind is not null || child.GivenNames is not null
                       || record.MotherPerson?.GivenNames is not null || record.MotherPerson?.Surname is not null
                       || record.FatherPerson?.GivenNames is not null || record.FatherPerson?.Surname is not null
                       || record.ParentsMarriageDate is not null || record.ProofOfAddressKind is not null;
        if (!recorded)
        {
            return null;
        }

        var caller = await currentRegistrar.GetAsync(HttpContext.RequestAborted);
        var full = caller is not null
                   && await currentRegistrar.CanActForFacilityAsync(caller, record.FacilityId, HttpContext.RequestAborted);

        ParentDetails? Parent(Person? person) => person is null ? null : new ParentDetails
        {
            GivenNames = person.GivenNames,
            Surname = person.Surname,
            MaidenSurname = person.MaidenSurname,
            DateOfBirth = person.DateOfBirth,
            PlaceOfBirth = person.PlaceOfBirth,
            Occupation = person.Occupation,
            Address = full ? person.Address : null,
            DocumentType = person.IdentityDocumentType,
            DocumentNumber = full ? person.IdentityDocumentNumber : null,
        };

        return new RegistrationDetails(
            child.GivenNames,
            child.Surname,
            record.PlaceOfBirthKind,
            record.PlaceOfBirth,
            Parent(record.MotherPerson),
            Parent(record.FatherPerson),
            record.ParentsMarriageDate is null && record.MarriageCertificateNumber is null
                ? null
                : new MarriageDetails { Date = record.ParentsMarriageDate, CertificateNumber = full ? record.MarriageCertificateNumber : null },
            record.ProofOfAddressKind is null && record.ProofOfAddressReference is null
                ? null
                : new ProofOfAddressDetails { Kind = record.ProofOfAddressKind, Reference = full ? record.ProofOfAddressReference : null },
            Restricted: !full);
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

        // A correction to a legal record is attributed to a device like any
        // other write, so the device is held to the same proof.
        var refused = await channelGate.RefuseUnlessPermittedForRecordAsync(
            HttpContext, registrar, envelope.Data.DeviceId, brn);
        if (refused is not null)
        {
            return refused;
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

            AmendmentResult.Incomplete => ApiErrors.Result(ApiErrors.Single(
                StatusCodes.Status400BadRequest, "This correction is incomplete.", "data", result.Detail!)),

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

        // The history is open to any signed-in caller, like the lookup by
        // number; a corrected address or document number is not, any more
        // than the record's own is (RegistrationDetails.Restricted).
        var caller = await currentRegistrar.GetAsync(HttpContext.RequestAborted);
        var full = caller is not null
                   && await currentRegistrar.CanActForFacilityAsync(caller, record.FacilityId, HttpContext.RequestAborted);

        var history = await db.BirthRecordAmendments
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
                amendment.TransactionId,
                false))
            .ToListAsync(HttpContext.RequestAborted);

        return full
            ? history
            : history.Select(entry => AmendmentFields.Withheld.Contains(entry.Field)
                ? entry with { PreviousValue = null, NewValue = null, Withheld = true }
                : entry).ToList();
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
        if (!await currentRegistrar.CanActForFacilityAsync(registrar, facilityId, HttpContext.RequestAborted))
        {
            return ApiErrors.Result(ApiErrors.Single(
                StatusCodes.Status403Forbidden, "Not permitted for this facility.",
                "facilityId", $"You are not permitted to request BRN blocks for facility '{facilityId}'."));
        }

        // Drawing numbers is the act a stolen device token is most worth
        // using for: a block taken offline under a revoked tablet's name is a
        // fortnight of registrations nobody can attribute. The device proves
        // itself here, as on every other write.
        var refused = await channelGate.RefuseUnlessPermittedAsync(
            HttpContext, registrar, envelope.Data.DeviceId, facilityId, nameof(Facility), facilityId.ToString());
        if (refused is not null)
        {
            return refused;
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

            // Drawn through the issuer, which skips numbers already on a record
            // and composes them under the facility's office code when it has one.
            var grant = await new BrnIssuer(db).StageAsync(facility, blockSize, HttpContext.RequestAborted);

            if (grant is null)
            {
                return ApiErrors.Result(ApiErrors.Single(
                    StatusCodes.Status409Conflict, "BRN range exhausted.",
                    "facilityId",
                    facility.OfficeCode is null
                        ? $"Facility '{facilityId}' has exhausted its pre-approved BRN range (ceiling {facility.BrnBlockEnd}) "
                          + "once numbers already on a record are excluded. A new range must be assigned by the central "
                          + "registry before more BRNs can be issued."
                        : $"Facility '{facilityId}' has issued every running number for this year "
                          + $"({BrnFormat.MaxRunning:N0}) once numbers already on a record are excluded."));
            }

            var skipped = grant.Skipped;

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
                // Only the management site may leave this empty (the channel
                // gate makes a device name itself); record what was sent
                // rather than inventing a value.
                DeviceId = deviceId ?? "unspecified",
                TransactionId = TransactionContext.Get(HttpContext)?.TransactionId
            });

            try
            {
                await db.SaveChangesAsync();
                return new BrnBlockResponse(facilityId, grant.Start, grant.End)
                {
                    OfficeCode = grant.OfficeCode,
                    Year = grant.Year,
                    FirstBrn = grant.FirstBrn,
                    LastBrn = grant.LastBrn,
                };
            }
            catch (DbUpdateException)
            {
                // Another grant advanced the counter since we read it -- or,
                // for the first grant of a year, created that year's counter at
                // the same moment. Drop everything staged this attempt and
                // retry against fresh data.
                db.ChangeTracker.Clear();
            }
        }

        return ApiErrors.Result(ApiErrors.Single(
            StatusCodes.Status409Conflict, "BRN block contention.",
            "facilityId", $"Facility '{facilityId}' is under high contention for BRN blocks; please retry."));
    }
}
