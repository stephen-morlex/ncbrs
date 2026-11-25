using FluentValidation;
using NCBRS.Models;

namespace NCBRS.Validation;

public class CreateAdministrativeAreaRequestValidator : AbstractValidator<CreateAdministrativeAreaRequest>
{
    public CreateAdministrativeAreaRequestValidator()
    {
        RuleFor(request => request.ParentId)
            .NotEmpty().WithMessage("Say which area it is in: a county, or an area within one.");

        RuleFor(request => request.Level)
            .IsInEnum()
            .Must(AdministrativeLevels.IsCreatable)
            .WithMessage("Only a payam, block, boma, quarter or village can be added. States and counties are the official geography.");

        RuleFor(request => request.Name)
            .NotEmpty().WithMessage("An area needs a name.")
            .MaximumLength(200);
    }
}
