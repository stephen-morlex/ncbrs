using FluentValidation;
using NCBRS.Models;

namespace NCBRS.Validation;

public class HandOverCertificateRequestValidator : AbstractValidator<HandOverCertificateRequest>
{
    public HandOverCertificateRequestValidator()
    {
        RuleFor(request => request.ReceivedBy)
            .NotNull().WithMessage("Say who received the certificate: the mother, the father, a guardian, another relative, or the person themselves.")
            .IsInEnum();
    }
}
