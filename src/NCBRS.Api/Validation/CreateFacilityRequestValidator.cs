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
    }
}
