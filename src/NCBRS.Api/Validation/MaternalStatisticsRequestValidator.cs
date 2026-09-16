using FluentValidation;
using NCBRS.Models;

namespace NCBRS.Validation;

/// <summary>
/// Ranges here reject the physically impossible, not the merely unusual.
/// A statistical questionnaire that refuses a real answer produces a gap in
/// the national figures, which is worse than an outlier in them.
///
/// Cross-field consistency (a last live birth that postdates this one, care
/// beginning before the pregnancy) lives in MaternalStatisticsService,
/// because those checks need the birth record to compare against.
/// </summary>
public class MaternalStatisticsRequestValidator : AbstractValidator<MaternalStatisticsRequest>
{
    public MaternalStatisticsRequestValidator()
    {
        RuleFor(request => request.MotherEducationLevel)
            .IsInEnum().WithMessage("motherEducationLevel is not a recognised ISCED level.")
            .When(request => request.MotherEducationLevel.HasValue);

        RuleFor(request => request.FatherEducationLevel)
            .IsInEnum().WithMessage("fatherEducationLevel is not a recognised ISCED level.")
            .When(request => request.FatherEducationLevel.HasValue);

        RuleFor(request => request.MotherOccupation)
            .IsInEnum().WithMessage("motherOccupation is not a recognised ISCO-08 major group.")
            .When(request => request.MotherOccupation.HasValue);

        RuleFor(request => request.FatherOccupation)
            .IsInEnum().WithMessage("fatherOccupation is not a recognised ISCO-08 major group.")
            .When(request => request.FatherOccupation.HasValue);

        // The documented maximum for live births to one woman is in the
        // twenties; 30 leaves room above any real case while still catching a
        // transposed or mistyped figure.
        RuleFor(request => request.PriorLiveBirths)
            .InclusiveBetween(0, 30).WithMessage("priorLiveBirths must be between 0 and 30.");

        RuleFor(request => request.PriorFetalDeaths)
            .InclusiveBetween(0, 30).WithMessage("priorFetalDeaths must be between 0 and 30.");

        // WHO recommends a minimum of eight contacts; a high-risk pregnancy
        // under close follow-up can far exceed that, so the ceiling only
        // catches nonsense.
        RuleFor(request => request.PrenatalVisitCount)
            .InclusiveBetween(0, 60).WithMessage("prenatalVisitCount must be between 0 and 60 when supplied.")
            .When(request => request.PrenatalVisitCount.HasValue);

        RuleFor(request => request.DateOfLastLiveBirth)
            .Must(BeInThePast).WithMessage("dateOfLastLiveBirth cannot be in the future.")
            .When(request => request.DateOfLastLiveBirth.HasValue);

        RuleFor(request => request.MedicalCareBeganDate)
            .Must(BeInThePast).WithMessage("medicalCareBeganDate cannot be in the future.")
            .When(request => request.MedicalCareBeganDate.HasValue);
    }

    private static bool BeInThePast(DateOnly? date)
        => !date.HasValue || date.Value <= DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(1));
}

/// <summary>
/// The standalone endpoint additionally needs the capturing device, so the
/// questionnaire can be traced like every other write.
/// </summary>
public class CaptureMaternalStatisticsRequestValidator
    : AbstractValidator<CaptureMaternalStatisticsRequest>
{
    public CaptureMaternalStatisticsRequestValidator()
    {
        Include(new MaternalStatisticsRequestValidator());

        RuleFor(request => request.DeviceId)
            .NotEmpty().WithMessage("deviceId is required so the questionnaire can be traced to a device.")
            .MaximumLength(64).WithMessage("deviceId must be 64 characters or fewer.");
    }
}
