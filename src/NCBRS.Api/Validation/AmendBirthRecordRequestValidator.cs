using FluentValidation;
using NCBRS.Models;

namespace NCBRS.Validation;

/// <summary>
/// The per-field rules mirror <see cref="RegisterBirthRequestValidator"/> --
/// a correction must not be able to put a value into the register that
/// registration itself would have refused.
///
/// The difference is that every field is optional here, so each rule is
/// conditioned on the field being supplied. The two rules with real weight
/// are on Reason, which is what distinguishes a correction from a silent
/// rewrite, and on requiring at least one field to change.
/// </summary>
public class AmendBirthRecordRequestValidator : AbstractValidator<AmendBirthRecordRequest>
{
    private static readonly TimeSpan ClockSkewTolerance = TimeSpan.FromHours(12);

    public AmendBirthRecordRequestValidator()
    {
        RuleFor(request => request.Reason)
            .NotEmpty().WithMessage("reason is required -- an unexplained change to a legal record cannot be audited.")
            .MinimumLength(10).WithMessage("reason must be at least 10 characters, describing why the correction is needed.")
            .MaximumLength(500).WithMessage("reason must be 500 characters or fewer.");

        RuleFor(request => request.DeviceId)
            .NotEmpty().WithMessage("deviceId is required so the amendment can be traced to a device.")
            .MaximumLength(64).WithMessage("deviceId must be 64 characters or fewer.");

        // Written against the whole object, so it reports no property path.
        // FluentValidationFilter maps that to "data", which is what it is
        // about -- naming it explicitly would yield "data.data".
        RuleFor(request => request)
            .Must(HaveSomethingToChange)
            .WithMessage("Supply at least one field to amend.");

        RuleFor(request => request.ChildFullName)
            .NotEmpty().WithMessage("childFullName cannot be corrected to an empty value.")
            .MaximumLength(200).WithMessage("childFullName must be 200 characters or fewer.")
            .When(request => request.ChildFullName is not null);

        RuleFor(request => request.MotherFullName)
            .NotEmpty().WithMessage("motherFullName cannot be corrected to an empty value.")
            .MaximumLength(200).WithMessage("motherFullName must be 200 characters or fewer.")
            .When(request => request.MotherFullName is not null);

        RuleFor(request => request.FatherFullName)
            .NotEmpty().WithMessage("fatherFullName cannot be corrected to an empty value.")
            .MaximumLength(200).WithMessage("fatherFullName must be 200 characters or fewer.")
            .When(request => request.FatherFullName is not null);

        RuleFor(request => request.DateOfBirth)
            .Must(BeInThePast).WithMessage("dateOfBirth cannot be in the future.")
            .When(request => request.DateOfBirth.HasValue);

        RuleFor(request => request.Sex)
            .IsInEnum().WithMessage("sex must be one of: Male, Female, Undetermined.")
            .When(request => request.Sex.HasValue);

        RuleFor(request => request.BirthWeightGrams)
            .InclusiveBetween(200, 9999).WithMessage("birthWeightGrams must be between 200 and 9999 when supplied.")
            .When(request => request.BirthWeightGrams.HasValue);

        RuleFor(request => request.GestationalAgeWeeks)
            .InclusiveBetween(16m, 45m).WithMessage("gestationalAgeWeeks must be between 16 and 45 when supplied.")
            .When(request => request.GestationalAgeWeeks.HasValue);

        RuleFor(request => request.BirthOrder)
            .InclusiveBetween(1, 10).WithMessage("birthOrder must be between 1 and 10 when supplied.")
            .When(request => request.BirthOrder.HasValue);
    }

    /// <summary>
    /// An amendment carrying only a reason would write an audit row claiming
    /// a correction that never happened.
    /// </summary>
    private static bool HaveSomethingToChange(AmendBirthRecordRequest request)
        => request.ChildFullName is not null
           || request.MotherFullName is not null
           || request.FatherFullName is not null
           || request.DateOfBirth.HasValue
           || request.Sex.HasValue
           || request.BirthWeightGrams.HasValue
           || request.GestationalAgeWeeks.HasValue
           || request.BirthOrder.HasValue;

    private static bool BeInThePast(DateTime? dateOfBirth)
        => dateOfBirth!.Value.ToUniversalTime() <= DateTime.UtcNow.Add(ClockSkewTolerance);
}
