using System.Collections;
using System.Globalization;
using System.Resources;
using System.Text.RegularExpressions;
using NCBRS.Client.Localization;
using NCBRS.Models;
using Xunit;

namespace NCBRS.Client.Tests;

/// <summary>
/// The tablet's two languages must say the same things. A string missing in
/// Arabic falls back to English mid-sentence; a placeholder missing in one
/// language drops a BRN or a count; a placeholder added throws at runtime. All
/// three are caught here rather than on a registrar's screen.
///
/// Run alone: one test switches the process to Arabic for a moment, and any
/// test running beside it that reads a string twice would see both languages.
/// </summary>
[Collection(ProcessLanguage.Name)]
public class LanguageTests
{
    private static Dictionary<string, string> Read(CultureInfo culture)
    {
        // Not disposed: the resource manager caches the set and hands it out again.
        var set = Strings.ResourceManager.GetResourceSet(culture, createIfNotExists: true, tryParents: false)!;
        return set.Cast<DictionaryEntry>().ToDictionary(entry => (string)entry.Key, entry => (string)entry.Value!);
    }

    private static readonly Dictionary<string, string> English = Read(CultureInfo.InvariantCulture);
    private static readonly Dictionary<string, string> Arabic = Read(CultureInfo.GetCultureInfo("ar"));

    [Fact]
    public void EveryStringHasAnArabicVersion()
    {
        Assert.Empty(English.Keys.Except(Arabic.Keys));
        Assert.Empty(Arabic.Keys.Except(English.Keys));
        Assert.DoesNotContain(Arabic, entry => string.IsNullOrWhiteSpace(entry.Value));
    }

    [Fact]
    public void EveryArabicStringIsArabic()
    {
        // The one exception is the switch, which names English in English.
        var untranslated = Arabic
            .Where(entry => entry.Key != nameof(Strings.Language_Switch) && entry.Value.Length > 1)
            // A pure template ("{0:d MMMM yyyy}") has no words to translate.
            .Where(entry => Regex.IsMatch(Regex.Replace(English[entry.Key], @"\{[^}]*\}", ""), @"\p{L}"))
            .Where(entry => !Regex.IsMatch(entry.Value, @"\p{IsArabic}"))
            .Select(entry => entry.Key);

        Assert.Empty(untranslated);
    }

    [Fact]
    public void BothLanguagesUseTheSamePlaceholders()
    {
        static string[] Holes(string text) => [.. Regex.Matches(text, @"\{(\d+)(?:[:,][^}]*)?\}").Select(match => match.Groups[1].Value).Distinct().Order()];

        var mismatched = English.Keys
            .Where(key => !Holes(English[key]).SequenceEqual(Holes(Arabic[key])))
            .ToList();

        Assert.Empty(mismatched);
    }

    /// <summary>Every coded answer a registrar picks from has a name in both languages.</summary>
    [Theory]
    [InlineData(typeof(Sex))]
    [InlineData(typeof(BirthPlurality))]
    [InlineData(typeof(LateRegistrationEvidenceType))]
    [InlineData(typeof(EducationLevel))]
    [InlineData(typeof(NCBRS.Client.Network.CentralOutcome))]
    [InlineData(typeof(RevocationReason))]
    [InlineData(typeof(PlaceOfBirthKind))]
    [InlineData(typeof(IdentityDocumentType))]
    public void EveryCodedAnswerHasAName(Type codes)
    {
        var missing = Enum.GetNames(codes).Where(name => !English.ContainsKey($"{codes.Name}_{name}"));

        Assert.Empty(missing);
    }

    /// <summary>
    /// Every field the tablet's copy of the registry's rules can name has a name
    /// a registrar reads, so a refusal never shows a property path. Collected by
    /// running the rules over a request that breaks each one.
    /// </summary>
    [Fact]
    public void EveryFieldTheRulesNameHasAName()
    {
        var broken = new RegisterBirthRequest
        {
            ChildGivenNames = new string('x', 101),
            ChildSurname = new string('x', 101),
            ChildFullName = "Also given whole",
            DateOfBirth = DateTime.UtcNow.Date.AddDays(1),
            PlaceOfBirthKind = PlaceOfBirthKind.Home,
            Mother = new ParentDetails { Occupation = "Teacher", DocumentNumber = "SS1" },
            Father = new ParentDetails { GivenNames = "Deng", MaidenSurname = "Garang" },
            MotherFullName = "Achol Deng",
            Marriage = new MarriageDetails { CertificateNumber = new string('x', 51) },
            ProofOfAddress = new ProofOfAddressDetails { Kind = new string('x', 101) },
            Sex = (Sex)99,
            Plurality = (BirthPlurality)99,
        };

        var unnamed = RegistrationRules.ShapeProblems(broken, DateTime.UtcNow)
            .Select(problem => problem.Field.Split('.')[0])
            .Distinct()
            .Where(top => !English.ContainsKey($"Field_{top}"))
            .ToList();

        Assert.Empty(unnamed);
    }

    [Fact]
    public void TheSwitchNamesTheOtherLanguage()
    {
        Assert.Equal("العربية", English[nameof(Strings.Language_Switch)]);
        Assert.Equal("English", Arabic[nameof(Strings.Language_Switch)]);
    }

    [Theory]
    [InlineData("٣١٠٠", "3100")]
    [InlineData("۳۱۰۰", "3100")]
    [InlineData("39.5", "39.5")]
    [InlineData("٣٩٫٥", "39٫5")]
    public void ArabicDigitsAreReadAsDigits(string typed, string read)
        => Assert.Equal(read, Language.WesternDigits(typed));

    [Fact]
    public void TheFirstRunLanguageFollowsTheTablet()
    {
        Assert.Equal("ar", Language.CodeOf(Language.FromDevice(CultureInfo.GetCultureInfo("ar-EG"))));
        Assert.Equal("en", Language.CodeOf(Language.FromDevice(CultureInfo.GetCultureInfo("fr-FR"))));
    }

    [Fact]
    public void ACodedAnswerReadsInTheChosenLanguage()
    {
        var before = Strings.Culture;
        try
        {
            Strings.Culture = Language.Arabic;
            Assert.Equal("أنثى", Language.Name(Sex.Female));
            Assert.Equal("اسم الطفل", Language.Field("childFullName"));
            Assert.Equal("التسجيل المتأخر", Language.Field("lateRegistration.declarantName"));

            Strings.Culture = Language.English;
            Assert.Equal("Female", Language.Name(Sex.Female));
        }
        finally
        {
            Strings.Culture = before;
        }
    }
}

/// <summary>Tests that change the process-wide language run with nothing beside them.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessLanguage
{
    public const string Name = "Process language";
}
