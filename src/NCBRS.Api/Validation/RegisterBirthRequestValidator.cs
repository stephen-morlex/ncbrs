using FluentValidation;
using NCBRS.Models;

namespace NCBRS.Validation;

/// <summary>
/// Ranges here are deliberately wide enough for genuine clinical outliers
/// (extreme prematurity, very low birth weight) and only reject values that
/// are physically impossible -- a registry that refuses a real birth is
/// worse than one that accepts an unusual one.
/// </summary>
public class RegisterBirthRequestValidator : AbstractValidator<RegisterBirthRequest>
{
    /// <summary>
    /// A village post's device is offline for weeks and its clock drifts, so
    /// a birth stamped slightly ahead is a wrong clock, not a wrong record.
    /// Beyond this it's a data-entry error worth catching at the door.
    /// </summary>
    private static readonly TimeSpan ClockSkewTolerance = TimeSpan.FromHours(12);

    public RegisterBirthRequestValidator()
    {
        RuleFor(request => request.Brn)
            .NotEmpty().WithMessage("brn is required -- allocate one from the facility's BRN block.")
            .MaximumLength(64).WithMessage("brn must be 64 characters or fewer.");

        // A device that exhausted its block offline sends a provisional
        // identifier instead (draft 6.3). The shape is checked strictly: a
        // malformed one would be neither a BRN the centre can reconcile nor
        // a fallback it can recognise, and would sit in the register as
        // neither.
        RuleFor(request => request.Brn)
            .Must(ProvisionalIdentifier.IsWellFormed)
            .WithMessage("A provisional identifier must look like PROV-{deviceId}-{sequence}, "
                         + "e.g. PROV-TABLET-07-3.")
            .When(request => ProvisionalIdentifier.Looks(request.Brn));

        RuleFor(request => request.FacilityId)
            .NotEqual(Guid.Empty).WithMessage("facilityId must be a non-empty UUID.");

        // No rule for the acting registrar: it is taken from the access
        // token's subject, never from the payload.

        RuleFor(request => request.ChildFullName)
            .NotEmpty().WithMessage("childFullName is required.")
            .MaximumLength(200).WithMessage("childFullName must be 200 characters or fewer.");

        RuleFor(request => request.DateOfBirth)
            .NotEmpty().WithMessage("dateOfBirth is required.")
            .Must(BeInThePast).WithMessage("dateOfBirth cannot be in the future.");

        RuleFor(request => request.Sex)
            .IsInEnum().WithMessage("sex must be one of: Male, Female, Undetermined.");

        RuleFor(request => request.Plurality)
            .IsInEnum().WithMessage("plurality must be one of: Singleton, Twin, Triplet, HigherOrderMultiple.");

        RuleFor(request => request.BirthWeightGrams)
            .InclusiveBetween(200, 9999).WithMessage("birthWeightGrams must be between 200 and 9999 when supplied.")
            .When(request => request.BirthWeightGrams.HasValue);

        RuleFor(request => request.GestationalAgeWeeks)
            .InclusiveBetween(16m, 45m).WithMessage("gestationalAgeWeeks must be between 16 and 45 when supplied.")
            .When(request => request.GestationalAgeWeeks.HasValue);

        RuleFor(request => request.BirthOrder)
            .InclusiveBetween(1, 10).WithMessage("birthOrder must be between 1 and 10 when supplied.")
            .When(request => request.BirthOrder.HasValue);

        RuleFor(request => request.MotherFullName)
            .MaximumLength(200).WithMessage("motherFullName must be 200 characters or fewer.");

        RuleFor(request => request.FatherFullName)
            .MaximumLength(200).WithMessage("fatherFullName must be 200 characters or fewer.");

        RuleFor(request => request.DeviceId)
            .NotEmpty().WithMessage("deviceId is required so the registration can be traced to a device.")
            .MaximumLength(64).WithMessage("deviceId must be 64 characters or fewer.");

        RuleFor(request => request.RegisteredAtUtc)
            .Must(BeInThePast).WithMessage("registeredAtUtc cannot be in the future.")
            .When(request => request.RegisteredAtUtc.HasValue);

        // Whether late-registration details are *required* depends on the
        // statutory window, which is configuration the registration service
        // holds -- so that check lives there. This validates only the shape
        // of what was supplied.
        When(request => request.LateRegistration is not null, () =>
        {
            RuleFor(request => request.LateRegistration!.EvidenceType)
                .IsInEnum().WithMessage("lateRegistration.evidenceType is not a recognised evidence type.");

            RuleFor(request => request.LateRegistration!.DeclarantName)
                .NotEmpty().WithMessage("lateRegistration.declarantName is required -- someone must stand behind the claim.")
                .MaximumLength(200).WithMessage("lateRegistration.declarantName must be 200 characters or fewer.");

            RuleFor(request => request.LateRegistration!.DeclarantRelationship)
                .NotEmpty().WithMessage("lateRegistration.declarantRelationship is required, e.g. mother, father, guardian.")
                .MaximumLength(100).WithMessage("lateRegistration.declarantRelationship must be 100 characters or fewer.");

            RuleFor(request => request.LateRegistration!.EvidenceReference)
                .MaximumLength(200).WithMessage("lateRegistration.evidenceReference must be 200 characters or fewer.");
        });

        // A multiple birth without a birth order can't be placed among its
        // siblings, and each sibling is its own record (WHO/UN: never one
        // bundled row). Singletons don't need it.
        RuleFor(request => request.BirthOrder)
            .NotNull().WithMessage("birthOrder is required for a multiple birth.")
            .When(request => request.Plurality != BirthPlurality.Singleton);
    }

    private static bool BeInThePast(DateTime dateOfBirth)
        => dateOfBirth.ToUniversalTime() <= DateTime.UtcNow.Add(ClockSkewTolerance);

    private static bool BeInThePast(DateTime? moment)
        => !moment.HasValue || BeInThePast(moment.Value);
}
