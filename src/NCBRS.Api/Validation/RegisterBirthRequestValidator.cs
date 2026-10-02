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

        // The child's name: in parts (the fuller form, 2026-10-02) or in one
        // piece (tablets not yet upgraded). Naming the child in parts is what
        // makes a request the fuller form, which also asks where the birth was.
        When(request => request.UsesStructuredNames, () =>
        {
            RuleFor(request => request.ChildGivenNames)
                .NotEmpty().WithMessage("childGivenNames is required.")
                .MaximumLength(100).WithMessage("childGivenNames must be 100 characters or fewer.");

            RuleFor(request => request.ChildSurname)
                .NotEmpty().WithMessage("childSurname is required.")
                .MaximumLength(100).WithMessage("childSurname must be 100 characters or fewer.");

            RuleFor(request => request.ChildFullName)
                .Empty().WithMessage("childFullName must be left out when the given names and surname are given.");

            RuleFor(request => request.PlaceOfBirthKind)
                .NotNull().WithMessage("placeOfBirthKind is required.");
        }).Otherwise(() =>
        {
            RuleFor(request => request.ChildFullName)
                .NotEmpty().WithMessage("childFullName is required.")
                .MaximumLength(200).WithMessage("childFullName must be 200 characters or fewer.");
        });

        RuleFor(request => request.PlaceOfBirthKind)
            .IsInEnum().WithMessage("placeOfBirthKind must be one of: ThisFacility, OtherHealthFacility, Home, Elsewhere.")
            .When(request => request.PlaceOfBirthKind.HasValue);

        RuleFor(request => request.PlaceOfBirth)
            .NotEmpty().WithMessage("placeOfBirth is required when the birth was not at this facility.")
            .When(request => request.PlaceOfBirthKind is { } kind && kind != PlaceOfBirthKind.ThisFacility);

        RuleFor(request => request.PlaceOfBirth)
            .MaximumLength(200).WithMessage("placeOfBirth must be 200 characters or fewer.");

        RuleFor(request => request.Mother!).SetValidator(new ParentDetailsValidator("mother", isMother: true))
            .When(request => request.Mother is not null);
        RuleFor(request => request.Father!).SetValidator(new ParentDetailsValidator("father", isMother: false))
            .When(request => request.Father is not null);

        // A parent born after their child is a typing slip in one date or the other.
        RuleFor(request => request.Mother!.DateOfBirth)
            .Must((request, born) => born < DateOnly.FromDateTime(request.DateOfBirth))
            .WithMessage("mother.dateOfBirth must be before the child's date of birth.")
            .When(request => request.Mother?.DateOfBirth is not null);
        RuleFor(request => request.Father!.DateOfBirth)
            .Must((request, born) => born < DateOnly.FromDateTime(request.DateOfBirth))
            .WithMessage("father.dateOfBirth must be before the child's date of birth.")
            .When(request => request.Father?.DateOfBirth is not null);

        // One name per parent: the parts, or the original one-piece field, never both.
        RuleFor(request => request.MotherFullName)
            .Empty().WithMessage("motherFullName must be left out when the mother's given names or surname are given.")
            .When(request => request.Mother?.HasName == true);
        RuleFor(request => request.FatherFullName)
            .Empty().WithMessage("fatherFullName must be left out when the father's given names or surname are given.")
            .When(request => request.Father?.HasName == true);

        When(request => request.Marriage is not null, () =>
        {
            RuleFor(request => request.Marriage!.Date)
                .Must(date => date!.Value.ToDateTime(TimeOnly.MinValue) <= DateTime.UtcNow.Add(ClockSkewTolerance))
                .WithMessage("marriage.date cannot be in the future.")
                .When(request => request.Marriage!.Date is not null);
            RuleFor(request => request.Marriage!.CertificateNumber)
                .MaximumLength(50).WithMessage("marriage.certificateNumber must be 50 characters or fewer.");
        });

        When(request => request.ProofOfAddress is not null, () =>
        {
            RuleFor(request => request.ProofOfAddress!.Kind)
                .MaximumLength(100).WithMessage("proofOfAddress.kind must be 100 characters or fewer.");
            RuleFor(request => request.ProofOfAddress!.Reference)
                .MaximumLength(100).WithMessage("proofOfAddress.reference must be 100 characters or fewer.");
        });

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
