using System.Globalization;

namespace NCBRS.Client.Localization;

/// <summary>
/// The tablet's two languages. English is the neutral resource set; Arabic is
/// Modern Standard Arabic — a draft for the Ministry to review, because the
/// legal and clinical terms are what the law names and a translation of them is
/// a decision, not a wording.
///
/// The culture is South Sudan's (<c>ar-SS</c>, <c>en-SS</c>) so dates and
/// numbers read as they would there. BRNs are identifiers and stay as they are
/// written in either language.
/// </summary>
public static class Language
{
    public static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-SS");
    public static readonly CultureInfo Arabic = CultureInfo.GetCultureInfo("ar-SS");

    public static CultureInfo Current => Strings.Culture ?? CultureInfo.CurrentUICulture;

    public static bool IsArabic => Current.TwoLetterISOLanguageName == "ar";

    /// <summary>Arabic if the tablet's own language is Arabic, else English: the first-run choice.</summary>
    public static CultureInfo FromDevice(CultureInfo device) => device.TwoLetterISOLanguageName == "ar" ? Arabic : English;

    public static CultureInfo FromCode(string? code) => code == "ar" ? Arabic : English;

    public static string CodeOf(CultureInfo culture) => culture.TwoLetterISOLanguageName == "ar" ? "ar" : "en";

    /// <summary>Use <paramref name="culture"/> for every string and every date from here on.</summary>
    public static void Use(CultureInfo culture)
    {
        Strings.Culture = culture;
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
    }

    /// <summary>A resource with placeholders, formatted in the current culture.</summary>
    public static string Format(string text, params object?[] args) => string.Format(Current, text, args);

    /// <summary>
    /// A coded answer (sex, plurality, evidence type, education level) as a
    /// registrar reads it: the resource <c>{Enum}_{Member}</c>. Every member has
    /// one (<c>LanguageTests</c>), so the fallback to the code is a safety net.
    /// </summary>
    public static string Name<T>(T value) where T : struct, Enum
        => Strings.ResourceManager.GetString($"{typeof(T).Name}_{value}", Current) ?? value.ToString();

    /// <summary>A registry field (<c>childFullName</c>, <c>lateRegistration.declarantName</c>) as a registrar would name it.</summary>
    public static string Field(string field)
    {
        var top = field.Split('.')[0];
        return Strings.ResourceManager.GetString($"Field_{top}", Current) ?? field;
    }

    /// <summary>
    /// Every decimal digit as its ASCII digit. A numeric keypad in Arabic types
    /// Arabic-Indic digits (٣١٠٠), which an invariant parse refuses — a birth
    /// weight typed in Arabic would otherwise be "not a number".
    /// </summary>
    public static string WesternDigits(string? text)
        => new((text ?? "").Select(c => char.IsDigit(c) ? (char)('0' + (int)char.GetNumericValue(c)) : c).ToArray());
}
