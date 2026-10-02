using FluentValidation;
using NCBRS.Models;

namespace NCBRS.Validation;

/// <summary>
/// One parent's details: every part optional, but what is given must hold
/// together. The tablet restates these rules (RegistrationRules), held to them
/// word for word by RegistrationRulesParityTests.
/// </summary>
// Internal so the assembly scan that registers validators passes it by: it
// takes which parent it checks, which dependency injection cannot supply.
internal sealed class ParentDetailsValidator : AbstractValidator<ParentDetails>
{
    public ParentDetailsValidator(string who, bool isMother)
    {
        // Details with no name describe nobody the register can name.
        RuleFor(parent => parent.GivenNames)
            .Must((parent, _) => parent.HasName)
            .WithMessage($"{who}.givenNames or {who}.surname is required when giving the {who}'s details.");

        RuleFor(parent => parent.GivenNames).MaximumLength(100).WithMessage($"{who}.givenNames must be 100 characters or fewer.");
        RuleFor(parent => parent.Surname).MaximumLength(100).WithMessage($"{who}.surname must be 100 characters or fewer.");

        if (isMother)
        {
            RuleFor(parent => parent.MaidenSurname).MaximumLength(100).WithMessage($"{who}.maidenSurname must be 100 characters or fewer.");
        }
        else
        {
            RuleFor(parent => parent.MaidenSurname).Empty().WithMessage($"{who}.maidenSurname applies to the mother only.");
        }

        RuleFor(parent => parent.PlaceOfBirth).MaximumLength(200).WithMessage($"{who}.placeOfBirth must be 200 characters or fewer.");
        RuleFor(parent => parent.Occupation).MaximumLength(200).WithMessage($"{who}.occupation must be 200 characters or fewer.");
        RuleFor(parent => parent.Address).MaximumLength(300).WithMessage($"{who}.address must be 300 characters or fewer.");

        RuleFor(parent => parent.DocumentType)
            .IsInEnum().WithMessage($"{who}.documentType must be one of: Passport, BirthCertificate, DrivingLicence, NationalId.")
            .When(parent => parent.DocumentType.HasValue);

        // A type without a number records nothing checkable; a number without
        // a type cannot be told apart from another document's.
        RuleFor(parent => parent.DocumentNumber)
            .NotEmpty().WithMessage($"{who}.documentNumber is required with the document type.")
            .When(parent => parent.DocumentType.HasValue);
        RuleFor(parent => parent.DocumentType)
            .NotNull().WithMessage($"{who}.documentType is required with the document number.")
            .When(parent => !string.IsNullOrWhiteSpace(parent.DocumentNumber));
        RuleFor(parent => parent.DocumentNumber).MaximumLength(50).WithMessage($"{who}.documentNumber must be 50 characters or fewer.");
    }
}
