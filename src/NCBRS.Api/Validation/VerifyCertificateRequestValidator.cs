using FluentValidation;
using NCBRS.Models;

namespace NCBRS.Validation;

/// <summary>
/// Only shape is checked here. Whether the payload actually verifies is the
/// endpoint's answer, not a validation error -- a forged certificate is a
/// successful check with a negative result, and returning 400 for one would
/// tell a forger their payload was rejected before the signature was even
/// examined.
/// </summary>
public class VerifyCertificateRequestValidator : AbstractValidator<VerifyCertificateRequest>
{
    public VerifyCertificateRequestValidator()
    {
        RuleFor(request => request.QrPayload)
            .NotEmpty().WithMessage("qrPayload is required -- scan the QR code printed on the certificate.")
            .MaximumLength(2048).WithMessage("qrPayload must be 2048 characters or fewer.");
    }
}
