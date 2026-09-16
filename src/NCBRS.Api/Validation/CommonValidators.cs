using FluentValidation;
using NCBRS.Models;

namespace NCBRS.Validation;

public class BrnBlockRequestValidator : AbstractValidator<BrnBlockRequest>
{
    public const int MaxBlockSize = 10_000;

    public BrnBlockRequestValidator()
    {
        RuleFor(request => request.BlockSize)
            .InclusiveBetween(1, MaxBlockSize)
            .WithMessage($"blockSize must be between 1 and {MaxBlockSize}.");

        RuleFor(request => request.DeviceId)
            .MaximumLength(64).WithMessage("deviceId must be 64 characters or fewer.");
    }
}

public class RequestMetaValidator : AbstractValidator<RequestMeta>
{
    public RequestMetaValidator()
    {
        // Absent is fine -- the server mints one. Present-but-empty is a
        // client bug worth naming.
        RuleFor(meta => meta.TransactionId)
            .NotEqual(Guid.Empty).WithMessage("transactionId must be a non-empty UUID when supplied.")
            .When(meta => meta.TransactionId.HasValue);

        RuleFor(meta => meta.ClientId)
            .MaximumLength(64).WithMessage("clientId must be 64 characters or fewer.");
    }
}

// Note: there is deliberately no validator for ApiRequest<T> itself.
// Registering one per closed generic would mean a new line of DI wiring
// every time an endpoint is added, and forgetting it would silently skip
// validation. FluentValidationFilter unwraps the envelope and validates
// Meta and Data by their own runtime types instead.
