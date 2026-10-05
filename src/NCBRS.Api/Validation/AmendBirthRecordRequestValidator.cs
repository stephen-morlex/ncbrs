using FluentValidation;
using NCBRS.Models;

namespace NCBRS.Validation;

/// <summary>
/// The per-field rules mirror <see cref="RegisterBirthRequestValidator"/> --
/// a correction must not be able to put a value into the register that
/// registration itself would have refused.
///
/// The difference is that every field is optional here, so each rule is
/// conditioned on the field being supplied. The two rules with real weight
/// are on Reason, which is what distinguishes a correction from a silent
/// rewrite, and on requiring at least one field to change.
/// </summary>
public class AmendBirthRecordRequestValidator : AbstractValidator<AmendBirthRecordRequest>
{
    private static readonly TimeSpan ClockSkewTolerance = TimeSpan.FromHours(12);

    public AmendBirthRecordRequestValidator()
    {
        RuleFor(request => request.Reason)
            .NotEmpty().WithMessage("reason is required -- an unexplained change to a legal record cannot be audited.")
            .MinimumLength(10).WithMessage("reason must be at least 10 characters, describing why the correction is needed.")
            .MaximumLength(500).WithMessage("reason must be 500 characters or fewer.");

        RuleFor(request => request.DeviceId)
            .NotEmpty().WithMessage("deviceId is required so the amendment can be traced to a device.")
            .MaximumLength(64).WithMessage("deviceId must be 64 characters or fewer.");

        // Written against the whole object, so it reports no property path.
        // FluentValidationFilter maps that to "data", which is what it is
        // about -- naming it explicitly would yield "data.data".
        RuleFor(request => request)
            .Must(HaveSomethingToChange)
            .WithMessage("Supply at least one field to amend.");

        RuleFor(request => request.ChildFullName)
            .NotEmpty().WithMessage("childFullName cannot be corrected to an empty value.")
            .MaximumLength(200).WithMessage("childFullName must be 200 characters or fewer.")
            .When(request => request.ChildFullName is not null);

        RuleFor(request => request.MotherFullName)
            .NotEmpty().WithMessage("motherFullName cannot be corrected to an empty value.")
            .MaximumLength(200).WithMessage("motherFullName must be 200 characters or fewer.")
            .When(request => request.MotherFullName is not null);

        RuleFor(request => request.FatherFullName)
            .NotEmpty().WithMessage("fatherFullName cannot be corrected to an empty value.")
            .MaximumLength(200).WithMessage("fatherFullName must be 200 characters or fewer.")
            .When(request => request.FatherFullName is not null);

        RuleFor(request => request.DateOfBirth)
            .Must(BeInThePast).WithMessage("dateOfBirth cannot be in the future.")
            .When(request => request.DateOfBirth.HasValue);

        RuleFor(request => request.Sex)
            .IsInEnum().WithMessage("sex must be one of: Male, Female, Undetermined.")
            .When(request => request.Sex.HasValue);

        RuleFor(request => request.BirthWeightGrams)
            .InclusiveBetween(200, 9999).WithMessage("birthWeightGrams must be between 200 and 9999 when supplied.")
            .When(request => request.BirthWeightGrams.HasValue);

        RuleFor(request => request.GestationalAgeWeeks)
            .InclusiveBetween(16m, 45m).WithMessage("gestationalAgeWeeks must be between 16 and 45 when supplied.")
            .When(request => request.GestationalAgeWeeks.HasValue);

        RuleFor(request => request.BirthOrder)
            .InclusiveBetween(1, 10).WithMessage("birthOrder must be between 1 and 10 when supplied.")
            .When(request => request.BirthOrder.HasValue);

        // The fuller registration's fields. The registration rules' lengths
        // and codes, without the rules that need the whole registration: a
        // correction may touch one part of a parent's details, and whether it
        // leaves the record whole is the service's call (AmendmentResult.Incomplete).
        RuleFor(request => request.ChildGivenNames)
            .NotEmpty().WithMessage("childGivenNames cannot be corrected to an empty value.")
            .MaximumLength(100).WithMessage("childGivenNames must be 100 characters or fewer.")
            .When(request => request.ChildGivenNames is not null);

        RuleFor(request => request.ChildSurname)
            .NotEmpty().WithMessage("childSurname cannot be corrected to an empty value.")
            .MaximumLength(100).WithMessage("childSurname must be 100 characters or fewer.")
            .When(request => request.ChildSurname is not null);

        RuleFor(request => request.ChildFullName)
            .Null().WithMessage("childFullName must be left out when the given names or surname are corrected.")
            .When(request => request.ChildGivenNames is not null || request.ChildSurname is not null);

        RuleFor(request => request.PlaceOfBirthKind)
            .IsInEnum().WithMessage("placeOfBirthKind must be one of: ThisFacility, OtherHealthFacility, Home, Elsewhere.")
            .When(request => request.PlaceOfBirthKind.HasValue);

        RuleFor(request => request.PlaceOfBirth)
            .MaximumLength(200).WithMessage("placeOfBirth must be 200 characters or fewer.");

        Parent("mother", request => request.Mother, request => request.MotherFullName, isMother: true);
        Parent("father", request => request.Father, request => request.FatherFullName, isMother: false);

        RuleFor(request => request.Marriage!.Date)
            .Must(date => date!.Value <= DateOnly.FromDateTime(DateTime.UtcNow.Add(ClockSkewTolerance)))
            .WithMessage("marriage.date cannot be in the future.")
            .When(request => request.Marriage?.Date is not null);

        RuleFor(request => request.Marriage!.CertificateNumber)
            .MaximumLength(50).WithMessage("marriage.certificateNumber must be 50 characters or fewer.")
            .When(request => request.Marriage is not null);

        RuleFor(request => request.ProofOfAddress!.Kind)
            .MaximumLength(100).WithMessage("proofOfAddress.kind must be 100 characters or fewer.")
            .When(request => request.ProofOfAddress is not null);

        RuleFor(request => request.ProofOfAddress!.Reference)
            .MaximumLength(100).WithMessage("proofOfAddress.reference must be 100 characters or fewer.")
            .When(request => request.ProofOfAddress is not null);
    }

    private void Parent(
        string who,
        Func<AmendBirthRecordRequest, ParentDetails?> details,
        Func<AmendBirthRecordRequest, string?> fullName,
        bool isMother)
    {
        bool Given(AmendBirthRecordRequest request) => details(request) is not null;

        RuleFor(request => details(request)!.GivenNames)
            .MaximumLength(100).WithMessage($"{who}.givenNames must be 100 characters or fewer.")
            .When(Given).OverridePropertyName($"{who}.givenNames");

        RuleFor(request => details(request)!.Surname)
            .MaximumLength(100).WithMessage($"{who}.surname must be 100 characters or fewer.")
            .When(Given).OverridePropertyName($"{who}.surname");

        RuleFor(request => details(request)!.MaidenSurname)
            .MaximumLength(100).WithMessage($"{who}.maidenSurname must be 100 characters or fewer.")
            .When(Given).OverridePropertyName($"{who}.maidenSurname");

        if (!isMother)
        {
            RuleFor(request => details(request)!.MaidenSurname)
                .Null().WithMessage($"{who}.maidenSurname applies to the mother only.")
                .When(Given).OverridePropertyName($"{who}.maidenSurname");
        }

        RuleFor(request => details(request)!.DateOfBirth)
            .Must(date => date!.Value <= DateOnly.FromDateTime(DateTime.UtcNow))
            .WithMessage($"{who}.dateOfBirth cannot be in the future.")
            .When(request => details(request)?.DateOfBirth is not null).OverridePropertyName($"{who}.dateOfBirth");

        RuleFor(request => details(request)!.PlaceOfBirth)
            .MaximumLength(200).WithMessage($"{who}.placeOfBirth must be 200 characters or fewer.")
            .When(Given).OverridePropertyName($"{who}.placeOfBirth");

        RuleFor(request => details(request)!.Occupation)
            .MaximumLength(200).WithMessage($"{who}.occupation must be 200 characters or fewer.")
            .When(Given).OverridePropertyName($"{who}.occupation");

        RuleFor(request => details(request)!.Address)
            .MaximumLength(300).WithMessage($"{who}.address must be 300 characters or fewer.")
            .When(Given).OverridePropertyName($"{who}.address");

        RuleFor(request => details(request)!.DocumentType)
            .IsInEnum().WithMessage($"{who}.documentType must be one of: Passport, BirthCertificate, DrivingLicence, NationalId.")
            .When(request => details(request)?.DocumentType is not null).OverridePropertyName($"{who}.documentType");

        RuleFor(request => details(request)!.DocumentNumber)
            .MaximumLength(50).WithMessage($"{who}.documentNumber must be 50 characters or fewer.")
            .When(Given).OverridePropertyName($"{who}.documentNumber");

        // A name corrected two ways at once has no single meaning.
        RuleFor(request => fullName(request))
            .Null().WithMessage($"{who}FullName must be left out when the {who}'s given names or surname are corrected.")
            .When(request => details(request)?.GivenNames is not null || details(request)?.Surname is not null)
            .OverridePropertyName($"{who}FullName");
    }

    /// <summary>
    /// An amendment carrying only a reason would write an audit row claiming
    /// a correction that never happened.
    /// </summary>
    private static bool HaveSomethingToChange(AmendBirthRecordRequest request)
        => request.ChildFullName is not null
           || request.MotherFullName is not null
           || request.FatherFullName is not null
           || request.DateOfBirth.HasValue
           || request.Sex.HasValue
           || request.BirthWeightGrams.HasValue
           || request.GestationalAgeWeeks.HasValue
           || request.BirthOrder.HasValue
           || request.ChildGivenNames is not null
           || request.ChildSurname is not null
           || request.PlaceOfBirthKind.HasValue
           || request.PlaceOfBirth is not null
           || request.Mother is not null
           || request.Father is not null
           || request.Marriage is not null
           || request.ProofOfAddress is not null;

    private static bool BeInThePast(DateTime? dateOfBirth)
        => dateOfBirth!.Value.ToUniversalTime() <= DateTime.UtcNow.Add(ClockSkewTolerance);
}
