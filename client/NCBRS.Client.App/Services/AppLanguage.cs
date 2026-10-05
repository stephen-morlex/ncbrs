using System.Globalization;
using NCBRS.Client.Localization;

namespace NCBRS.Client.App.Services;

/// <summary>
/// The tablet's language: English or Arabic, switched in the app rather than in
/// Android's settings, because a shared tablet is used by whoever is holding
/// it. The first run follows the tablet's own language; after that the choice
/// is remembered on the tablet. It is a preference, not a record, so it lives
/// in Preferences and never in the encrypted store.
/// </summary>
public static class AppLanguage
{
    private const string Key = "ncbrs-language";

    public static void Apply(Application app)
    {
        Language.Use(Preferences.Default.Get<string?>(Key, null) is { } saved
            ? Language.FromCode(saved)
            : Language.FromDevice(CultureInfo.CurrentUICulture));
        UseFonts(app);
    }

    /// <summary>Switch to the other language and remember it.</summary>
    public static void Toggle()
    {
        var next = Language.IsArabic ? Language.English : Language.Arabic;
        Preferences.Default.Set(Key, Language.CodeOf(next));
        Language.Use(next);
        if (Application.Current is { } app)
        {
            UseFonts(app);
        }
    }

    /// <summary>
    /// The faces the implicit styles read: Geist for English, Noto Sans Arabic
    /// for Arabic, which Geist does not draw. Set as resources so every control
    /// built from a style follows the language without being rebuilt by hand.
    /// </summary>
    private static void UseFonts(Application app)
    {
        app.Resources["FontRegular"] = Pages.Ui.Regular;
        app.Resources["FontSemiBold"] = Pages.Ui.SemiBold;
    }

    /// <summary>Right to left in Arabic: the whole page mirrors, not just the text.</summary>
    public static FlowDirection Direction => Language.IsArabic ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;

    /// <summary>
    /// The window itself, on Android: the title bar is drawn by the activity,
    /// outside any page, and does not mirror with the page's FlowDirection.
    /// </summary>
    public static void ApplyToWindow()
    {
#if ANDROID
        if (Platform.CurrentActivity?.Window?.DecorView is { } decor)
        {
            decor.LayoutDirection = Language.IsArabic ? Android.Views.LayoutDirection.Rtl : Android.Views.LayoutDirection.Ltr;
        }
#endif
    }
}
