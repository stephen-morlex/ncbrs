using FluentValidation;
using NCBRS.Models;

namespace NCBRS.Validation;

/// <summary>
/// Validates the batch envelope only -- deliberately NOT the records inside
/// it.
///
/// Per Section 6.3 of the draft, sync is transactional per record: one
/// malformed entry must not cost a device the other forty-nine births it
/// spent a week collecting. The controller validates each record
/// individually and reports the failures alongside the successes, so the
/// device knows precisely which entries to fix.
/// </summary>
public class SyncBatchRequestValidator : AbstractValidator<SyncBatchRequest>
{
    /// <summary>
    /// Bounds one upload so a device returning from a long outage splits its
    /// outbox into several calls rather than one request the server has to
    /// hold entirely in memory.
    /// </summary>
    public const int MaxRecordsPerBatch = 500;

    public SyncBatchRequestValidator()
    {
        RuleFor(batch => batch.DeviceId)
            .NotEmpty().WithMessage("deviceId is required so the batch can be traced to a device.")
            .MaximumLength(64).WithMessage("deviceId must be 64 characters or fewer.");

        RuleFor(batch => batch.FacilityId)
            .NotEqual(Guid.Empty).WithMessage("facilityId must be a non-empty UUID.");

        RuleFor(batch => batch.Records)
            .NotEmpty().WithMessage("records must contain at least one birth registration.")
            .Must(records => records.Count <= MaxRecordsPerBatch)
            .WithMessage($"records must contain {MaxRecordsPerBatch} entries or fewer per batch.");
    }
}
