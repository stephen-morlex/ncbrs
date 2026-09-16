using System.Globalization;
using Microsoft.EntityFrameworkCore;
using NCBRS.Data;
using NCBRS.Events;
using NCBRS.Kafka;
using NCBRS.Models;

namespace NCBRS.Services;

public enum AmendmentResult
{
    Amended,
    BirthRecordNotFound,
    NotPermitted,

    /// <summary>Nothing supplied actually differed from what is on file.</summary>
    NothingToChange,

    /// <summary>A superseded record is not corrected; the surviving one is.</summary>
    RecordSuperseded,

    /// <summary>The registration was voided; there is nothing left to correct.</summary>
    RecordAnnulled
}

public record AmendmentOutcome(
    AmendmentResult Result,
    AmendBirthRecordResponse? Response = null,
    string? Detail = null)
{
    public bool Succeeded => Result is AmendmentResult.Amended;

    /// <summary>
    /// True when nothing took effect and the whole submission is waiting on a
    /// reviewer. The caller answers 202 rather than 200 for this, so a device
    /// never tells a family a correction is done when it is not.
    /// </summary>
    public bool EverythingPending
        => Succeeded && Response!.Applied.Count == 0 && Response.PendingApproval.Count > 0;
}

public enum AmendmentReviewResult
{
    Reviewed,
    NotFound,
    AlreadyReviewed,
    NotPermitted,

    /// <summary>The record moved under the proposal; the approver saw stale values.</summary>
    Conflict
}

public record AmendmentReviewOutcome(
    AmendmentReviewResult Result,
    ReviewAmendmentResponse? Response = null,
    string? Detail = null)
{
    public bool Succeeded => Result is AmendmentReviewResult.Reviewed;
}

/// <summary>
/// Corrects a registered birth (draft Section 5.3 / 6.5, RecordStatus.Amended).
///
/// Corrections are the ordinary business of a civil register -- a misspelled
/// name is far more common than a disputed one -- but they are still changes
/// to a legal record, so each one keeps its previous value, its author and
/// its reason.
///
/// Corrections run on two tracks. Fields the certificate signature does not
/// cover take effect immediately; fields it does cover wait for a reviewer
/// who is not the author, because those are the fields that decide which
/// person the register is describing.
/// </summary>
public class AmendmentService(
    NcbrsDbContext db,
    IEventPublisher eventPublisher,
    CertificateRevocationRecorder revocations,
    CurrentRegistrarService currentRegistrar)
{
    /// <summary>
    /// Fields the certificate's signature covers. Changing any of them means
    /// a certificate already issued no longer describes the record.
    /// </summary>
    private static readonly string[] CertificateFields =
        [nameof(AmendBirthRecordRequest.ChildFullName),
         nameof(AmendBirthRecordRequest.DateOfBirth),
         nameof(AmendBirthRecordRequest.Sex)];

    /// <summary>
    /// Fields that cannot be corrected without a second person agreeing.
    ///
    /// A superset of the certificate's signed fields, because the two answer
    /// different questions: one is "does this invalidate a printed document",
    /// the other is "is this a change of legal identity". Parents' names sit
    /// only in the second. Nothing about a certificate changes when a
    /// father's name is corrected, but filiation does -- it is the field a
    /// disputed paternity would be rewritten through, and the one an
    /// inheritance claim later turns on. It is not clerical.
    ///
    /// What is left on the immediate track is therefore exactly the clinical
    /// measurements: birth weight, gestational age and birth order. Those
    /// describe the event. Everything describing *who* the record is about
    /// now waits for a second pair of eyes.
    /// </summary>
    private static readonly string[] ApprovalRequiredFields =
        [.. CertificateFields,
         nameof(AmendBirthRecordRequest.MotherFullName),
         nameof(AmendBirthRecordRequest.FatherFullName)];

    public async Task<AmendmentOutcome> AmendAsync(
        string brn,
        AmendBirthRecordRequest request,
        Registrar registrar,
        Guid? transactionId,
        CancellationToken cancellationToken = default)
    {
        var record = await LoadAsync(brn, cancellationToken);

        if (record is null || record.ChildPerson is null)
        {
            return new AmendmentOutcome(AmendmentResult.BirthRecordNotFound,
                Detail: $"No birth record exists with BRN '{brn}'.");
        }

        if (!currentRegistrar.CanActForFacility(registrar, record.FacilityId))
        {
            return new AmendmentOutcome(AmendmentResult.NotPermitted,
                Detail: "You are not permitted to amend records for this facility.");
        }

        // A record that should never have existed cannot be corrected into
        // one that should. Annulment is terminal.
        if (record.AnnulledAtUtc is not null)
        {
            return new AmendmentOutcome(AmendmentResult.RecordAnnulled,
                Detail: $"BRN '{brn}' was annulled on {record.AnnulledAtUtc:yyyy-MM-dd} and cannot be amended.");
        }

        // Correcting a record already ruled a duplicate would put the fix on
        // the copy nobody uses.
        if (record.SupersededByBirthRecordId is not null)
        {
            return new AmendmentOutcome(AmendmentResult.RecordSuperseded,
                Detail: $"BRN '{brn}' was superseded as a duplicate. Amend the surviving record instead.");
        }

        var changes = Collect(record, request);

        if (changes.Count == 0)
        {
            return new AmendmentOutcome(AmendmentResult.NothingToChange,
                Detail: "Nothing supplied differs from the current record.");
        }

        var requestId = Guid.CreateVersion7();
        var submittedAt = DateTime.UtcNow;

        // A device that spent weeks offline may have composed this against a
        // value the register no longer holds. Detected per field rather than
        // per record: a device correcting a birth weight while the centre
        // corrected a name is not in conflict with anything, and flagging it
        // would train registrars to dismiss the queue.
        var stale = DetectConflicts(record, request, changes);

        // The split. Each field goes to the track its own sensitivity earns,
        // so a birth-weight typo is not held hostage by a name change sharing
        // the same submission.
        var immediate = changes.Where(change => !RequiresApproval(change.Field)).ToList();
        var pending = changes.Where(change => RequiresApproval(change.Field)).ToList();

        foreach (var change in changes)
        {
            var isPending = RequiresApproval(change.Field);

            db.BirthRecordAmendments.Add(new BirthRecordAmendment
            {
                BirthRecordId = record.BirthRecordId,
                AmendmentRequestId = requestId,
                Field = change.Field,
                PreviousValue = change.PreviousValue,
                NewValue = change.NewValue,
                Reason = request.Reason,
                AmendedByRegistrarId = registrar.RegistrarId,
                AmendedAtUtc = submittedAt,
                TransactionId = transactionId,
                Status = isPending ? AmendmentStatus.PendingApproval : AmendmentStatus.Applied,
                AppliedAtUtc = isPending ? null : submittedAt
            });
        }

        var certificateInvalidated = false;

        if (immediate.Count > 0)
        {
            Apply(record, immediate);
            record.Status = RecordStatus.Amended;

            certificateInvalidated = await WithdrawCertificateIfNeededAsync(
                record, immediate, request.Reason, submittedAt,
                registrar.RegistrarId, transactionId, cancellationToken);

            Publish(record, brn, immediate, request.Reason,
                registrar.RegistrarId, certificateInvalidated, submittedAt, transactionId);
        }

        foreach (var conflict in stale)
        {
            // Applied fields carry the value that won; pending ones carry
            // null, because nothing has been applied yet and claiming a
            // winner before approval would misdescribe the record.
            var resolved = immediate.Any(change => change.Field == conflict.Field)
                ? changes.First(change => change.Field == conflict.Field).NewValue
                : null;

            db.AmendmentConflicts.Add(new AmendmentConflict
            {
                BirthRecordId = record.BirthRecordId,
                AmendmentRequestId = requestId,
                Field = conflict.Field,
                ExpectedPreviousValue = conflict.Expected,
                ActualPreviousValue = conflict.Actual,
                ResolvedValue = resolved,
                DetectedFromRegistrarId = registrar.RegistrarId,
                DetectedAtUtc = submittedAt,
                TransactionId = transactionId
            });
        }

        db.AuditLogs.Add(new AuditLog
        {
            EntityType = nameof(BirthRecord),
            EntityId = brn,
            Action = immediate.Count == 0 ? "AmendSubmitted" : "Amend",
            UserId = registrar.RegistrarId,
            DeviceId = request.DeviceId,
            TransactionId = transactionId
        });

        if (stale.Count > 0)
        {
            db.AuditLogs.Add(new AuditLog
            {
                EntityType = nameof(AmendmentConflict),
                EntityId = brn,
                Action = $"AmendmentConflict:{string.Join(",", stale.Select(conflict => conflict.Field))}",
                UserId = registrar.RegistrarId,
                DeviceId = request.DeviceId,
                TransactionId = transactionId
            });
        }

        await db.SaveChangesAsync(cancellationToken);

        return new AmendmentOutcome(
            AmendmentResult.Amended,
            new AmendBirthRecordResponse(
                brn,
                record.Status,
                requestId,
                Describe(immediate),
                Describe(pending),
                certificateInvalidated,
                submittedAt));
    }

    /// <summary>
    /// Records a reviewer's decision on the pending half of a submission.
    ///
    /// Approval is what makes those changes real: until now the record has
    /// read exactly as it did before, and any certificate issued against it
    /// has stayed valid.
    /// </summary>
    public async Task<AmendmentReviewOutcome> ReviewAsync(
        Guid amendmentRequestId,
        bool approve,
        Registrar reviewer,
        string? note,
        Guid? transactionId,
        CancellationToken cancellationToken = default)
    {
        var rows = await db.BirthRecordAmendments
            .Where(amendment => amendment.AmendmentRequestId == amendmentRequestId)
            .ToListAsync(cancellationToken);

        if (rows.Count == 0)
        {
            return new AmendmentReviewOutcome(AmendmentReviewResult.NotFound,
                Detail: $"No amendment request exists with id '{amendmentRequestId}'.");
        }

        var pending = rows.Where(row => row.Status == AmendmentStatus.PendingApproval).ToList();

        if (pending.Count == 0)
        {
            return new AmendmentReviewOutcome(AmendmentReviewResult.AlreadyReviewed,
                Detail: "This amendment request has already been reviewed.");
        }

        var submitter = pending[0].AmendedByRegistrarId;

        // Separation of duties. The whole purpose of the approval track is a
        // second pair of eyes, and an author approving their own correction
        // is the same pair.
        if (submitter == reviewer.RegistrarId)
        {
            return new AmendmentReviewOutcome(AmendmentReviewResult.NotPermitted,
                Detail: "An amendment cannot be approved by the registrar who submitted it.");
        }

        var record = await db.BirthRecords
            .Include(r => r.ChildPerson)
            .Include(r => r.MotherPerson)
            .Include(r => r.FatherPerson)
            .Include(r => r.Facility)
            .FirstOrDefaultAsync(r => r.BirthRecordId == pending[0].BirthRecordId, cancellationToken);

        if (record is null || record.ChildPerson is null)
        {
            return new AmendmentReviewOutcome(AmendmentReviewResult.NotFound,
                Detail: "The birth record this amendment belongs to no longer exists.");
        }

        if (!currentRegistrar.CanActForFacility(reviewer, record.FacilityId))
        {
            return new AmendmentReviewOutcome(AmendmentReviewResult.NotPermitted,
                Detail: "You are not permitted to review amendments for this facility.");
        }

        var reviewedAt = DateTime.UtcNow;

        if (!approve)
        {
            foreach (var row in pending)
            {
                row.Status = AmendmentStatus.Rejected;
                row.ReviewedByRegistrarId = reviewer.RegistrarId;
                row.ReviewedAtUtc = reviewedAt;
                row.ReviewNote = note;
            }

            Audit(record.Brn, "RejectAmendment", reviewer, transactionId);
            await db.SaveChangesAsync(cancellationToken);

            // No event: nothing about the register changed, and publishing a
            // refusal would invite a consumer to act on a correction that
            // never happened.
            return new AmendmentReviewOutcome(
                AmendmentReviewResult.Reviewed,
                new ReviewAmendmentResponse(amendmentRequestId, record.Brn, AmendmentStatus.Rejected,
                    Describe([.. pending.Select(ToChange)]), false, reviewedAt));
        }

        // The record may have moved since the proposal was written. Approving
        // a change away from a value that is no longer there would record a
        // previous value the register never held.
        var drifted = pending
            .Where(row => Current(record, row.Field) != row.PreviousValue
                          && Current(record, row.Field) != row.NewValue)
            .Select(row => row.Field)
            .ToList();

        if (drifted.Count > 0)
        {
            return new AmendmentReviewOutcome(AmendmentReviewResult.Conflict,
                Detail: $"The record changed after this correction was submitted ({string.Join(", ", drifted)}). "
                        + "Review the current values and ask for it to be resubmitted.");
        }

        // Anything already matching the proposal was fixed by someone else in
        // the meantime; recorded as applied rather than failed, since the
        // register now says what the reviewer approved.
        var effective = pending.Where(row => Current(record, row.Field) != row.NewValue).ToList();

        Apply(record, [.. effective.Select(ToChange)]);

        foreach (var row in pending)
        {
            row.Status = AmendmentStatus.Applied;
            row.AppliedAtUtc = reviewedAt;
            row.ReviewedByRegistrarId = reviewer.RegistrarId;
            row.ReviewedAtUtc = reviewedAt;
            row.ReviewNote = note;
        }

        record.Status = RecordStatus.Amended;

        var approvedChanges = pending.Select(ToChange).ToList();

        var certificateInvalidated = await WithdrawCertificateIfNeededAsync(
            record, approvedChanges, pending[0].Reason, reviewedAt,
            reviewer.RegistrarId, transactionId, cancellationToken);

        // Attributed to the registrar who authored the correction, not the
        // one who approved it -- downstream systems care whose change it was.
        Publish(record, record.Brn, approvedChanges, pending[0].Reason,
            submitter, certificateInvalidated, reviewedAt, transactionId);

        Audit(record.Brn, "ApproveAmendment", reviewer, transactionId);
        await db.SaveChangesAsync(cancellationToken);

        return new AmendmentReviewOutcome(
            AmendmentReviewResult.Reviewed,
            new ReviewAmendmentResponse(amendmentRequestId, record.Brn, AmendmentStatus.Applied,
                Describe(approvedChanges), certificateInvalidated, reviewedAt));
    }

    /// <summary>The queue a reviewer works from.</summary>
    public async Task<IReadOnlyList<PendingAmendmentResponse>> PendingAsync(
        Guid? facilityId,
        CancellationToken cancellationToken = default)
    {
        var query = db.BirthRecordAmendments
            .Include(amendment => amendment.BirthRecord!).ThenInclude(record => record.ChildPerson)
            .Include(amendment => amendment.BirthRecord!).ThenInclude(record => record.Facility)
            .Include(amendment => amendment.AmendedByRegistrar)
            .Where(amendment => amendment.Status == AmendmentStatus.PendingApproval);

        if (facilityId is not null)
        {
            query = query.Where(amendment => amendment.BirthRecord!.FacilityId == facilityId);
        }

        var rows = await query.ToListAsync(cancellationToken);

        return [.. rows
            .GroupBy(amendment => amendment.AmendmentRequestId)
            .Select(group =>
            {
                var first = group.First();
                return new PendingAmendmentResponse(
                    group.Key,
                    first.BirthRecord?.Brn ?? string.Empty,
                    first.BirthRecord?.ChildPerson?.FullName ?? string.Empty,
                    first.BirthRecord?.FacilityId ?? Guid.Empty,
                    first.BirthRecord?.Facility?.Name ?? string.Empty,
                    first.Reason,
                    first.AmendedByRegistrarId,
                    first.AmendedByRegistrar?.DisplayName ?? string.Empty,
                    first.AmendedAtUtc,
                    Describe([.. group.Select(ToChange)]));
            })
            .OrderBy(pending => pending.SubmittedAtUtc)];
    }

    /// <summary>
    /// Conflicts awaiting a registrar's judgement, oldest first.
    ///
    /// Distinct from the approval queue, which asks "should this change be
    /// made". This asks "the wrong value may already have won" -- and unlike
    /// an approval, the record has usually already moved, so age here is a
    /// measure of how long a possibly-wrong value has been standing.
    /// </summary>
    public async Task<IReadOnlyList<AmendmentConflictResponse>> ConflictsAsync(
        Guid? facilityId,
        CancellationToken cancellationToken = default)
    {
        var query = db.AmendmentConflicts
            .AsNoTracking()
            .Include(conflict => conflict.BirthRecord!).ThenInclude(record => record.ChildPerson)
            .Where(conflict => conflict.Status == AmendmentConflictStatus.PendingReview);

        if (facilityId is not null)
        {
            query = query.Where(conflict => conflict.BirthRecord!.FacilityId == facilityId);
        }

        var rows = await query.OrderBy(conflict => conflict.DetectedAtUtc).ToListAsync(cancellationToken);

        var submitters = await db.Registrars
            .AsNoTracking()
            .ToDictionaryAsync(person => person.RegistrarId, person => person.DisplayName, cancellationToken);

        return [.. rows.Select(conflict => new AmendmentConflictResponse(
            conflict.AmendmentConflictId,
            conflict.AmendmentRequestId,
            conflict.BirthRecord?.Brn ?? string.Empty,
            conflict.BirthRecord?.ChildPerson?.FullName ?? string.Empty,
            conflict.BirthRecord?.FacilityId ?? Guid.Empty,
            conflict.Field,
            conflict.ExpectedPreviousValue,
            conflict.ActualPreviousValue,
            conflict.ResolvedValue,
            conflict.DetectedFromRegistrarId,
            submitters.GetValueOrDefault(conflict.DetectedFromRegistrarId, string.Empty),
            conflict.DetectedAtUtc))];
    }

    public async Task<AmendmentReviewOutcome> ReviewConflictAsync(
        Guid amendmentConflictId,
        bool uphold,
        string note,
        Registrar reviewer,
        Guid? transactionId,
        CancellationToken cancellationToken = default)
    {
        var conflict = await db.AmendmentConflicts
            .Include(entry => entry.BirthRecord)
            .FirstOrDefaultAsync(entry => entry.AmendmentConflictId == amendmentConflictId, cancellationToken);

        if (conflict is null || conflict.BirthRecord is null)
        {
            return new AmendmentReviewOutcome(AmendmentReviewResult.NotFound,
                Detail: $"No amendment conflict exists with id '{amendmentConflictId}'.");
        }

        if (conflict.Status != AmendmentConflictStatus.PendingReview)
        {
            return new AmendmentReviewOutcome(AmendmentReviewResult.AlreadyReviewed,
                Detail: "This conflict has already been reviewed.");
        }

        if (!currentRegistrar.CanActForFacility(reviewer, conflict.BirthRecord.FacilityId))
        {
            return new AmendmentReviewOutcome(AmendmentReviewResult.NotPermitted,
                Detail: "You are not permitted to review conflicts for this facility.");
        }

        conflict.Status = uphold ? AmendmentConflictStatus.Upheld : AmendmentConflictStatus.Corrected;
        conflict.ReviewedByRegistrarId = reviewer.RegistrarId;
        conflict.ReviewedAtUtc = DateTime.UtcNow;
        conflict.ReviewNote = note;

        Audit(conflict.BirthRecord.Brn,
            uphold ? "UpholdAmendmentConflict" : "CorrectAmendmentConflict", reviewer, transactionId);

        await db.SaveChangesAsync(cancellationToken);

        return new AmendmentReviewOutcome(
            AmendmentReviewResult.Reviewed,
            new ReviewAmendmentResponse(
                conflict.AmendmentRequestId, conflict.BirthRecord.Brn, AmendmentStatus.Applied,
                [], false, conflict.ReviewedAtUtc.Value));
    }

    // --- internals --------------------------------------------------------

    private static bool RequiresApproval(string field)
        => ApprovalRequiredFields.Contains(field);

    private record StaleField(string Field, string? Expected, string? Actual);

    /// <summary>
    /// Finds the fields this correction was composed against a value the
    /// register no longer holds.
    ///
    /// Only fields the caller is actually changing are checked. A device that
    /// echoes back its whole view would otherwise raise a conflict on every
    /// field the centre had touched, including ones it is not proposing to
    /// change, which is noise rather than a clash.
    ///
    /// Silence is not a conflict: an online caller sends no observed values
    /// because it read the record moments ago, and treating that as a
    /// mismatch would flag every ordinary correction.
    /// </summary>
    private static List<StaleField> DetectConflicts(
        BirthRecord record,
        AmendBirthRecordRequest request,
        List<FieldChange> changes)
    {
        if (request.ObservedValues is not { Count: > 0 } observed)
        {
            return [];
        }

        var stale = new List<StaleField>();

        foreach (var change in changes)
        {
            var seen = observed.FirstOrDefault(value =>
                string.Equals(value.Field, change.Field, StringComparison.Ordinal));

            if (seen is null)
            {
                continue;
            }

            var actual = Current(record, change.Field);

            if (!string.Equals(seen.Value, actual, StringComparison.Ordinal))
            {
                stale.Add(new StaleField(change.Field, seen.Value, actual));
            }
        }

        return stale;
    }

    private Task<BirthRecord?> LoadAsync(string brn, CancellationToken cancellationToken)
        => db.BirthRecords
            .Include(r => r.ChildPerson)
            .Include(r => r.MotherPerson)
            .Include(r => r.FatherPerson)
            .Include(r => r.Facility)
            .FirstOrDefaultAsync(r => r.Brn == brn, cancellationToken);

    private void Audit(string brn, string action, Registrar actor, Guid? transactionId)
        => db.AuditLogs.Add(new AuditLog
        {
            EntityType = nameof(BirthRecord),
            EntityId = brn,
            Action = action,
            UserId = actor.RegistrarId,
            DeviceId = "review",
            TransactionId = transactionId
        });

    private void Publish(
        BirthRecord record,
        string brn,
        List<FieldChange> changes,
        string reason,
        Guid authorId,
        bool certificateInvalidated,
        DateTime at,
        Guid? transactionId)
        => eventPublisher.EnqueueBirthRecordAmended(
            new BirthRecordAmendedEvent(
                brn,
                record.BirthRecordId,
                record.FacilityId,
                [.. changes.Select(change => new AmendedField(change.Field, change.PreviousValue, change.NewValue))],
                reason,
                authorId,
                certificateInvalidated,
                at,
                transactionId),
            record.Facility?.DistrictId ?? string.Empty);

    /// <summary>
    /// A certificate signs the child's name, date of birth and sex. Once any
    /// of those changes the signed payload no longer describes the register,
    /// so the certificate is withdrawn and must be re-issued.
    ///
    /// A certificate already printed still verifies against its own stale
    /// contents, because the signature over them is genuine -- nothing here
    /// can reach a document in a family's hands. What closes that gap is the
    /// published revocation list, which is why the withdrawal goes through
    /// CertificateRevocationRecorder rather than being set directly.
    /// </summary>
    private async Task<bool> WithdrawCertificateIfNeededAsync(
        BirthRecord record,
        List<FieldChange> changes,
        string reason,
        DateTime at,
        Guid actorId,
        Guid? transactionId,
        CancellationToken cancellationToken)
    {
        var signedChanges = changes
            .Where(change => CertificateFields.Contains(change.Field))
            .Select(change => change.Field)
            .ToList();

        if (signedChanges.Count == 0)
        {
            return false;
        }

        var certificate = await db.Certificates
            .FirstOrDefaultAsync(
                c => c.BirthRecordId == record.BirthRecordId && c.WithdrawnAtUtc == null,
                cancellationToken);

        if (certificate is null)
        {
            return false;
        }

        revocations.Revoke(
            certificate,
            RevocationReason.Amended,
            at,
            actorId,
            transactionId,
            detail: $"Amended ({string.Join(", ", signedChanges)}): {reason}");

        return true;
    }

    private record FieldChange(string Field, string? PreviousValue, string? NewValue);

    private static FieldChange ToChange(BirthRecordAmendment row)
        => new(row.Field, row.PreviousValue, row.NewValue);

    private static IReadOnlyList<AmendedFieldResponse> Describe(IReadOnlyList<FieldChange> changes)
        => [.. changes.Select(change =>
            new AmendedFieldResponse(change.Field, change.PreviousValue, change.NewValue))];

    private static List<FieldChange> Collect(BirthRecord record, AmendBirthRecordRequest request)
    {
        var changes = new List<FieldChange>();

        Compare(changes, nameof(request.ChildFullName), record.ChildPerson!.FullName, request.ChildFullName);
        Compare(changes, nameof(request.MotherFullName), record.MotherPerson?.FullName, request.MotherFullName);
        Compare(changes, nameof(request.FatherFullName), record.FatherPerson?.FullName, request.FatherFullName);

        if (request.DateOfBirth is { } dateOfBirth && AsUtc(dateOfBirth) != AsUtc(record.DateOfBirth))
        {
            changes.Add(new FieldChange(nameof(request.DateOfBirth),
                Stamp(record.DateOfBirth), Stamp(dateOfBirth)));
        }

        if (request.Sex is { } sex && sex != record.Sex)
        {
            changes.Add(new FieldChange(nameof(request.Sex), record.Sex.ToString(), sex.ToString()));
        }

        if (request.BirthWeightGrams is { } weight && weight != record.BirthWeightGrams)
        {
            changes.Add(new FieldChange(nameof(request.BirthWeightGrams),
                Number(record.BirthWeightGrams), Number(weight)));
        }

        if (request.GestationalAgeWeeks is { } gestation && gestation != record.GestationalAgeWeeks)
        {
            changes.Add(new FieldChange(nameof(request.GestationalAgeWeeks),
                Number(record.GestationalAgeWeeks), Number(gestation)));
        }

        if (request.BirthOrder is { } order && order != record.BirthOrder)
        {
            changes.Add(new FieldChange(nameof(request.BirthOrder),
                Number(record.BirthOrder), Number(order)));
        }

        return changes;
    }

    /// <summary>
    /// The record's current value for a field, in the same string form the
    /// amendment rows store. This is what makes an approval comparable
    /// against a proposal written days earlier.
    /// </summary>
    private static string? Current(BirthRecord record, string field) => field switch
    {
        nameof(AmendBirthRecordRequest.ChildFullName) => record.ChildPerson?.FullName,
        nameof(AmendBirthRecordRequest.MotherFullName) => record.MotherPerson?.FullName,
        nameof(AmendBirthRecordRequest.FatherFullName) => record.FatherPerson?.FullName,
        nameof(AmendBirthRecordRequest.DateOfBirth) => Stamp(record.DateOfBirth),
        nameof(AmendBirthRecordRequest.Sex) => record.Sex.ToString(),
        nameof(AmendBirthRecordRequest.BirthWeightGrams) => Number(record.BirthWeightGrams),
        nameof(AmendBirthRecordRequest.GestationalAgeWeeks) => Number(record.GestationalAgeWeeks),
        nameof(AmendBirthRecordRequest.BirthOrder) => Number(record.BirthOrder),
        _ => null
    };

    /// <summary>
    /// Writes the changes onto the record.
    ///
    /// Reads values back from their stored string form rather than from the
    /// request, because an approval applies a proposal written days earlier
    /// and the request object is long gone by then. One code path serves both
    /// tracks, so the two cannot drift apart.
    /// </summary>
    private void Apply(BirthRecord record, IReadOnlyList<FieldChange> changes)
    {
        foreach (var change in changes)
        {
            switch (change.Field)
            {
                case nameof(AmendBirthRecordRequest.ChildFullName):
                    record.ChildPerson!.FullName = change.NewValue!;
                    break;

                case nameof(AmendBirthRecordRequest.MotherFullName):
                    record.MotherPerson = UpsertPerson(record.MotherPerson, change.NewValue!);
                    break;

                case nameof(AmendBirthRecordRequest.FatherFullName):
                    record.FatherPerson = UpsertPerson(record.FatherPerson, change.NewValue!);
                    break;

                case nameof(AmendBirthRecordRequest.DateOfBirth):
                    var dateOfBirth = AsUtc(DateTime.Parse(change.NewValue!,
                        CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
                    record.DateOfBirth = dateOfBirth;
                    record.ChildPerson!.DateOfBirth = DateOnly.FromDateTime(dateOfBirth);
                    break;

                case nameof(AmendBirthRecordRequest.Sex):
                    record.Sex = Enum.Parse<Sex>(change.NewValue!);
                    break;

                case nameof(AmendBirthRecordRequest.BirthWeightGrams):
                    record.BirthWeightGrams = int.Parse(change.NewValue!, CultureInfo.InvariantCulture);
                    break;

                case nameof(AmendBirthRecordRequest.GestationalAgeWeeks):
                    record.GestationalAgeWeeks = decimal.Parse(change.NewValue!, CultureInfo.InvariantCulture);
                    break;

                case nameof(AmendBirthRecordRequest.BirthOrder):
                    record.BirthOrder = int.Parse(change.NewValue!, CultureInfo.InvariantCulture);
                    break;
            }
        }
    }

    private static void Compare(List<FieldChange> changes, string field, string? current, string? proposed)
    {
        if (proposed is null || string.Equals(current, proposed, StringComparison.Ordinal))
        {
            return;
        }

        changes.Add(new FieldChange(field, current, proposed));
    }

    /// <summary>
    /// Stored and compared in the invariant culture. Without this a server
    /// under a comma-decimal locale would write "39,5" for gestational age
    /// and then fail to parse it back on approval.
    /// </summary>
    private static string? Number(IFormattable? value)
        => value?.ToString(null, CultureInfo.InvariantCulture);

    private static string Stamp(DateTime value)
        => AsUtc(value).ToString("O", CultureInfo.InvariantCulture);

    /// <summary>
    /// Normalises a date to UTC for comparison.
    ///
    /// SQLite hands back DateTimeKind.Unspecified, and ToUniversalTime()
    /// would treat that as the server's local time and shift it -- which
    /// would report every date of birth as changed on any host not running
    /// in UTC. Everything is written as UTC, so an unspecified kind is
    /// already UTC and is labelled rather than converted.
    /// </summary>
    private static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };

    /// <summary>
    /// A parent may not have been recorded at first registration, so naming
    /// one later creates the Person rather than failing.
    /// </summary>
    private Person UpsertPerson(Person? existing, string fullName)
    {
        if (existing is not null)
        {
            existing.FullName = fullName;
            return existing;
        }

        var person = new Person { FullName = fullName };
        db.People.Add(person);
        return person;
    }
}
