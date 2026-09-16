using FluentValidation;
using NCBRS.Models;

namespace NCBRS.Validation;

public class RecordNeonatalOutcomeRequestValidator : AbstractValidator<RecordNeonatalOutcomeRequest>
{
    public RecordNeonatalOutcomeRequestValidator()
    {
        RuleFor(request => request.DeathDateUtc)
            .NotEmpty().WithMessage("deathDateUtc is required.")
            .Must(date => date.ToUniversalTime() <= DateTime.UtcNow.AddHours(12))
            .WithMessage("deathDateUtc cannot be in the future.");

        RuleFor(request => request.IcdPmTiming)
            .IsInEnum().WithMessage("icdPmTiming must be one of: Antepartum, Intrapartum, Neonatal.");

        RuleFor(request => request.IcdPmCauseCode)
            .NotEmpty().WithMessage("icdPmCauseCode is required -- cause of death is coded, never free text.")
            .MaximumLength(16).WithMessage("icdPmCauseCode must be 16 characters or fewer.");

        RuleFor(request => request.ContributingMaternalConditionCode)
            .MaximumLength(16).WithMessage("contributingMaternalConditionCode must be 16 characters or fewer.");

        RuleFor(request => request.DeviceId)
            .NotEmpty().WithMessage("deviceId is required so the record can be traced to a device.")
            .MaximumLength(64).WithMessage("deviceId must be 64 characters or fewer.");
    }
}

public class RecordMaternalOutcomeRequestValidator : AbstractValidator<RecordMaternalOutcomeRequest>
{
    public RecordMaternalOutcomeRequestValidator()
    {
        RuleFor(request => request.DeathDateUtc)
            .NotEmpty().WithMessage("deathDateUtc is required.")
            .Must(date => date.ToUniversalTime() <= DateTime.UtcNow.AddHours(12))
            .WithMessage("deathDateUtc cannot be in the future.");

        RuleFor(request => request.IcdMmCauseCode)
            .NotEmpty().WithMessage("icdMmCauseCode is required -- cause of death is coded, never free text.")
            .MaximumLength(16).WithMessage("icdMmCauseCode must be 16 characters or fewer.");

        RuleFor(request => request.DeviceId)
            .NotEmpty().WithMessage("deviceId is required so the record can be traced to a device.")
            .MaximumLength(64).WithMessage("deviceId must be 64 characters or fewer.");
    }
}
