using FluentValidation.Results;
using NCBRS.Models;
using NCBRS.Validation;
using Xunit;

namespace NCBRS.Tests;

/// <summary>
/// Exercises the FluentValidation rules directly. These run in the MVC
/// pipeline before an action is reached, so a controller unit test can't
/// see them -- validating through the validator is validating the real rule.
/// </summary>
public class ValidationTests
{
    private static readonly RegisterBirthRequestValidator BirthValidator = new();

    private static ValidationResult Validate(RegisterBirthRequest request)
        => BirthValidator.Validate(request);

    private static bool HasErrorFor(ValidationResult result, string propertyName)
        => result.Errors.Any(failure => failure.PropertyName == propertyName);

    private static RegisterBirthRequest ValidBirth(
        string? brn = "100000",
        string? childFullName = "Chipo Mwale",
        DateTime? dateOfBirth = null,
        int? birthWeightGrams = 3200,
        decimal? gestationalAgeWeeks = 39.5m,
        int? birthOrder = 1,
        string? deviceId = "TABLET-07",
        Guid? facilityId = null,
        BirthPlurality plurality = BirthPlurality.Singleton)
        => new()
        {
            Brn = brn!,
            FacilityId = facilityId ?? Guid.CreateVersion7(),
            ChildFullName = childFullName!,
            DateOfBirth = dateOfBirth ?? new DateTime(2026, 9, 10, 4, 30, 0, DateTimeKind.Utc),
            Sex = Sex.Female,
            BirthWeightGrams = birthWeightGrams,
            GestationalAgeWeeks = gestationalAgeWeeks,
            Plurality = plurality,
            BirthOrder = birthOrder,
            MotherFullName = "Grace Mwale",
            FatherFullName = null,
            DeviceId = deviceId!
        };

    [Fact]
    public void AValidRegistration_PassesCleanly()
        => Assert.True(Validate(ValidBirth()).IsValid);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Brn_IsRequired(string? brn)
        => Assert.True(HasErrorFor(Validate(ValidBirth(brn: brn)), nameof(RegisterBirthRequest.Brn)));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ChildFullName_IsRequired(string? name)
        => Assert.True(HasErrorFor(Validate(ValidBirth(childFullName: name)), nameof(RegisterBirthRequest.ChildFullName)));

    [Fact]
    public void FacilityId_CannotBeAnEmptyGuid()
    {
        // An omitted UUID binds to all-zeroes rather than null, so a plain
        // "required" check would let it through to become a foreign-key error.
        var result = Validate(ValidBirth(facilityId: Guid.Empty));
        Assert.True(HasErrorFor(result, nameof(RegisterBirthRequest.FacilityId)));
    }

    [Fact]
    public void DateOfBirth_CannotBeInTheFuture()
        => Assert.True(HasErrorFor(
            Validate(ValidBirth(dateOfBirth: DateTime.UtcNow.AddDays(3))),
            nameof(RegisterBirthRequest.DateOfBirth)));

    [Fact]
    public void DateOfBirth_ToleratesMinorClockSkew()
    {
        // An offline device's clock drifts; a birth stamped slightly ahead is
        // a wrong clock, not a wrong record.
        var result = Validate(ValidBirth(dateOfBirth: DateTime.UtcNow.AddHours(2)));
        Assert.False(HasErrorFor(result, nameof(RegisterBirthRequest.DateOfBirth)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(199)]
    [InlineData(10_000)]
    public void BirthWeight_RejectsPhysicallyImpossibleValues(int grams)
        => Assert.True(HasErrorFor(
            Validate(ValidBirth(birthWeightGrams: grams)),
            nameof(RegisterBirthRequest.BirthWeightGrams)));

    [Theory]
    [InlineData(400)]   // extremely low but real
    [InlineData(6000)]  // extremely high but real
    public void BirthWeight_AcceptsClinicalOutliers(int grams)
        => Assert.False(HasErrorFor(
            Validate(ValidBirth(birthWeightGrams: grams)),
            nameof(RegisterBirthRequest.BirthWeightGrams)));

    [Fact]
    public void BirthWeight_IsOptional()
        => Assert.False(HasErrorFor(
            Validate(ValidBirth(birthWeightGrams: null)),
            nameof(RegisterBirthRequest.BirthWeightGrams)));

    [Theory]
    [InlineData(15)]
    [InlineData(46)]
    public void GestationalAge_RejectsImpossibleValues(int weeks)
        => Assert.True(HasErrorFor(
            Validate(ValidBirth(gestationalAgeWeeks: weeks)),
            nameof(RegisterBirthRequest.GestationalAgeWeeks)));

    [Theory]
    [InlineData(0)]
    [InlineData(11)]
    public void BirthOrder_RejectsOutOfRangeValues(int order)
        => Assert.True(HasErrorFor(
            Validate(ValidBirth(birthOrder: order)),
            nameof(RegisterBirthRequest.BirthOrder)));

    /// <summary>
    /// A cross-field rule FluentValidation makes easy that attributes did
    /// not: each sibling of a multiple birth is its own record, so one
    /// without a birth order can't be placed among them.
    /// </summary>
    [Fact]
    public void MultipleBirth_RequiresABirthOrder()
        => Assert.True(HasErrorFor(
            Validate(ValidBirth(plurality: BirthPlurality.Twin, birthOrder: null)),
            nameof(RegisterBirthRequest.BirthOrder)));

    [Fact]
    public void Singleton_DoesNotRequireABirthOrder()
        => Assert.False(HasErrorFor(
            Validate(ValidBirth(plurality: BirthPlurality.Singleton, birthOrder: null)),
            nameof(RegisterBirthRequest.BirthOrder)));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void DeviceId_IsRequired(string? deviceId)
        => Assert.True(HasErrorFor(Validate(ValidBirth(deviceId: deviceId)), nameof(RegisterBirthRequest.DeviceId)));

    [Fact]
    public void ErrorMessages_NameTheFieldAndTheRule()
    {
        var result = Validate(ValidBirth(birthWeightGrams: 50));
        var message = result.Errors
            .Single(failure => failure.PropertyName == nameof(RegisterBirthRequest.BirthWeightGrams))
            .ErrorMessage;

        Assert.Equal("birthWeightGrams must be between 200 and 9999 when supplied.", message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(10_001)]
    public void BlockSize_RejectsOutOfRangeValues(int blockSize)
    {
        var result = new BrnBlockRequestValidator().Validate(new BrnBlockRequest { BlockSize = blockSize });
        Assert.Contains(result.Errors, failure => failure.PropertyName == nameof(BrnBlockRequest.BlockSize));
    }

    [Fact]
    public void BlockSize_AcceptsTheSupportedRange()
        => Assert.True(new BrnBlockRequestValidator()
            .Validate(new BrnBlockRequest { BlockSize = 10_000 }).IsValid);

    [Fact]
    public void RequestMeta_RejectsAnEmptyTransactionId()
    {
        var result = new RequestMetaValidator()
            .Validate(new RequestMeta { TransactionId = Guid.Empty, ClientId = "MobileApp" });

        Assert.Contains(result.Errors, failure => failure.PropertyName == nameof(RequestMeta.TransactionId));
    }

    [Fact]
    public void RequestMeta_AllowsAnAbsentTransactionId()
        => Assert.True(new RequestMetaValidator()
            .Validate(new RequestMeta { ClientId = "MobileApp" }).IsValid);
}
