using FluentValidation;
using NCBRS.Models;

namespace NCBRS.Validation;

public class BindRegistrarRequestValidator : AbstractValidator<BindRegistrarRequest>
{
    public BindRegistrarRequestValidator()
    {
        RuleFor(request => request.PendingAccountId).NotEmpty().WithMessage("Choose the account to bind.");
        RuleFor(request => request.FacilityId).NotEmpty().WithMessage("Choose the facility they work at.");
        RuleFor(request => request.Role).IsInEnum();
        RuleFor(request => request.DisplayName).MaximumLength(200);
    }
}

public class WithdrawRegistrarRequestValidator : AbstractValidator<WithdrawRegistrarRequest>
{
    public WithdrawRegistrarRequestValidator()
    {
        // Kept with the registrar for good: "left", "moved to Torit", "account
        // compromised" each mean different follow-up.
        RuleFor(request => request.Reason)
            .NotEmpty().WithMessage("Say why: it is kept with the registrar for good.")
            .MaximumLength(500);
    }
}
