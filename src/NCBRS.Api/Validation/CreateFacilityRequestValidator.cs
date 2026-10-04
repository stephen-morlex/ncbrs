using FluentValidation;
using NCBRS.Models;

namespace NCBRS.Validation;

public class CreateFacilityRequestValidator : AbstractValidator<CreateFacilityRequest>
{
    public CreateFacilityRequestValidator()
    {
        RuleFor(request => request.Name)
            .NotEmpty().WithMessage("A facility needs a name.")
            .MaximumLength(200);

        RuleFor(request => request.Tier).IsInEnum();
        RuleFor(request => request.ConnectivityProfile).IsInEnum();

        RuleFor(request => request.AdministrativeAreaId)
            .NotEmpty().WithMessage("Say where the facility is: its county, payam, block, boma, quarter or village.");

        RuleFor(request => request.OfficeCode)
            .Must(OfficeCodes.IsValid).WithMessage(OfficeCodes.Message)
            .When(request => !string.IsNullOrWhiteSpace(request.OfficeCode));
    }
}

public class SetOfficeCodeRequestValidator : AbstractValidator<SetOfficeCodeRequest>
{
    public SetOfficeCodeRequestValidator()
    {
        RuleFor(request => request.OfficeCode)
            .NotEmpty().WithMessage("Give the office code.");

        // Separate: a trailing When applies to every rule in its chain, which
        // would excuse an empty code from being required at all.
        RuleFor(request => request.OfficeCode)
            .Must(OfficeCodes.IsValid).WithMessage(OfficeCodes.Message)
            .When(request => !string.IsNullOrWhiteSpace(request.OfficeCode));
    }
}

/// <summary>An office code as typed: trimmed and in capitals, then checked against <see cref="BrnFormat"/>.</summary>
public static class OfficeCodes
{
    public const string Message = "An office code is two to six letters or digits, for example JTH01.";

    public static string Normalise(string code) => code.Trim().ToUpperInvariant();

    public static bool IsValid(string? code) => code is not null && BrnFormat.IsOfficeCode(Normalise(code));
}
