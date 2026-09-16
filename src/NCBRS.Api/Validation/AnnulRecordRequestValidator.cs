using FluentValidation;
using NCBRS.Models;

namespace NCBRS.Validation;

/// <summary>
/// The justification is held to a higher bar than an amendment's reason.
/// An amendment explains why a detail was wrong; this explains why someone's
/// legal identity is being withdrawn, and it is the document an appeal or an
/// investigation would later read.
/// </summary>
public class AnnulRecordRequestValidator : AbstractValidator<AnnulRecordRequest>
{
    public AnnulRecordRequestValidator()
    {
        RuleFor(request => request.Reason)
            .IsInEnum().WithMessage(
                "reason must be one of: RegisteredInError, FraudulentRegistration, CourtOrdered.");

        RuleFor(request => request.Justification)
            .NotEmpty().WithMessage("justification is required.")
            .MinimumLength(30).WithMessage(
                "justification must be at least 30 characters. Annulment withdraws a legal identity; "
                + "the record has to explain why to someone reading it years later.")
            .MaximumLength(2000).WithMessage("justification must be 2000 characters or fewer.");

        RuleFor(request => request.AuthorityReference)
            .MaximumLength(200).WithMessage("authorityReference must be 200 characters or fewer.");

        // The service enforces this too, since it is a domain rule rather
        // than a shape rule -- but catching it here gives the caller the
        // field-level error rather than a generic failure.
        RuleFor(request => request.AuthorityReference)
            .NotEmpty().WithMessage("authorityReference is required for a court-ordered annulment: cite the order.")
            .When(request => request.Reason == AnnulmentReason.CourtOrdered);
    }
}
