using Microsoft.Maui.Controls.Shapes;
using MauiPath = Microsoft.Maui.Controls.Shapes.Path;

namespace NCBRS.Client.App.Pages;

/// <summary>
/// The tablet's look, in one place: a calm palette around the Ministry green,
/// large touch targets, and plain words beside every icon.
///
/// Designed for the people who use it -- midwives and community health
/// workers, often in bright sun, sometimes with a family waiting:
/// - **Touch targets of at least 52 dp**, above the 48 dp accessibility floor,
///   because the tablet is used one-handed and in a hurry.
/// - **Body text of 16 sp and up**, and contrast that passes WCAG AA in sun.
/// - **An icon never stands alone.** Every icon has its word beside it; a
///   symbol nobody was trained on is a guess.
/// - **Colour is never the only signal.** A warning also says so in words.
/// </summary>
public static class Ui
{
    // --- palette -----------------------------------------------------------------------------------

    public static readonly Color Primary = Color.FromArgb("#1B5E20");
    public static readonly Color PrimaryDark = Color.FromArgb("#103D14");
    public static readonly Color PrimarySoft = Color.FromArgb("#E8F3EA");
    public static readonly Color Background = Color.FromArgb("#F3F5F4");
    public static readonly Color Surface = Colors.White;
    public static readonly Color Text = Color.FromArgb("#17201A");
    public static readonly Color TextMuted = Color.FromArgb("#56625B");
    public static readonly Color Border = Color.FromArgb("#D9E0DB");
    public static readonly Color Danger = Color.FromArgb("#B42318");
    public static readonly Color DangerSoft = Color.FromArgb("#FDECEA");
    public static readonly Color Warning = Color.FromArgb("#93370D");
    public static readonly Color WarningSoft = Color.FromArgb("#FEF4E6");
    public static readonly Color Info = Color.FromArgb("#1849A9");
    public static readonly Color InfoSoft = Color.FromArgb("#EAF1FD");

    public const double Gutter = 16;
    public const double TouchTarget = 52;

    // --- text --------------------------------------------------------------------------------------

    public static Label Title(string text) => new()
    {
        Text = text, FontSize = 24, FontFamily = "OpenSansSemibold", TextColor = Text,
    };

    public static Label Heading(string text) => new()
    {
        Text = text, FontSize = 18, FontFamily = "OpenSansSemibold", TextColor = Text,
    };

    public static Label Body(string text, Color? color = null) => new()
    {
        Text = text, FontSize = 16, TextColor = color ?? Text, LineHeight = 1.25,
    };

    public static Label Caption(string text, Color? color = null) => new()
    {
        Text = text, FontSize = 14, TextColor = color ?? TextMuted,
    };

    /// <summary>A small capitalised label above a group, as a sign above a shelf.</summary>
    public static Label Overline(string text) => new()
    {
        Text = text.ToUpperInvariant(), FontSize = 13, FontFamily = "OpenSansSemibold",
        TextColor = TextMuted, CharacterSpacing = 1, Margin = new Thickness(4, 8, 4, 0),
    };

    // --- buttons -----------------------------------------------------------------------------------

    public static Button PrimaryButton(string text) => Styled(new Button { Text = text }, Primary, Colors.White);

    public static Button SecondaryButton(string text)
    {
        var button = Styled(new Button { Text = text }, Surface, Primary);
        button.BorderColor = Primary;
        button.BorderWidth = 1.5;
        return button;
    }

    public static Button DangerButton(string text) => Styled(new Button { Text = text }, Danger, Colors.White);

    /// <summary>A quiet action, like "Go back": it should not compete with the one that matters.</summary>
    public static Button GhostButton(string text)
    {
        var button = Styled(new Button { Text = text }, Colors.Transparent, TextMuted);
        button.BorderColor = Border;
        button.BorderWidth = 1;
        return button;
    }

    private static Button Styled(Button button, Color background, Color text)
    {
        button.BackgroundColor = background;
        button.TextColor = text;
        button.FontFamily = "OpenSansSemibold";
        button.FontSize = 16;
        button.CornerRadius = 12;
        button.MinimumHeightRequest = TouchTarget;
        button.Padding = new Thickness(16, 12);
        return button;
    }

    // --- surfaces ----------------------------------------------------------------------------------

    /// <summary>A white card on the grey page: the unit everything is grouped in.</summary>
    public static Border Card(params View[] views)
    {
        var stack = new VerticalStackLayout { Spacing = 12 };
        foreach (var view in views)
        {
            stack.Add(view);
        }

        return Panel(stack, Surface, Border);
    }

    /// <summary>A coloured notice: a warning, a refusal, good news. Its words carry the meaning; the colour repeats it.</summary>
    public static Border Notice(string icon, string text, Color color, Color background, Action? onTap = null)
    {
        var row = new HorizontalStackLayout { Spacing = 12 };
        row.Add(Icon(icon, color, 24));
        var label = Body(text, color);
        label.FontFamily = "OpenSansSemibold";
        label.VerticalOptions = LayoutOptions.Center;
        label.MaximumWidthRequest = 640;
        row.Add(label);

        var panel = Panel(row, background, background);
        if (onTap is not null)
        {
            Tappable(panel, onTap);
        }

        return panel;
    }

    private static Border Panel(View content, Color background, Color stroke) => new()
    {
        Content = content,
        BackgroundColor = background,
        Stroke = stroke,
        StrokeThickness = 1,
        StrokeShape = new RoundRectangle { CornerRadius = 16 },
        Padding = new Thickness(16),
        Shadow = new Shadow { Brush = Colors.Black, Opacity = 0.06f, Radius = 8, Offset = new Point(0, 2) },
    };

    /// <summary>
    /// A task on the home screen: an icon in a soft circle, a name and a line
    /// saying what it does. The whole tile is the button.
    /// </summary>
    public static Border Tile(string icon, string title, string subtitle, Action onTap, Color? accent = null, string? badge = null)
    {
        var colour = accent ?? Primary;
        var circle = new Border
        {
            BackgroundColor = colour.WithAlpha(0.12f),
            StrokeThickness = 0,
            StrokeShape = new Ellipse(),
            WidthRequest = 48, HeightRequest = 48,
            Content = Icon(icon, colour, 26),
            HorizontalOptions = LayoutOptions.Start,
        };

        var top = new Grid { ColumnDefinitions = [new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto)] };
        top.Add(circle, 0);
        if (badge is not null)
        {
            top.Add(Pill(badge, colour), 1);
        }

        var name = Heading(title);
        name.FontSize = 17;
        var stack = new VerticalStackLayout { Spacing = 8, Children = { top, name, Caption(subtitle) } };

        var tile = Panel(stack, Surface, Border);
        tile.MinimumHeightRequest = 150;
        SemanticProperties.SetDescription(tile, $"{title}. {subtitle}");
        Tappable(tile, onTap);
        return tile;
    }

    /// <summary>A short fact in a rounded pill: a count, a state.</summary>
    public static Border Pill(string text, Color color) => new()
    {
        BackgroundColor = color.WithAlpha(0.12f),
        StrokeThickness = 0,
        StrokeShape = new RoundRectangle { CornerRadius = 12 },
        Padding = new Thickness(10, 4),
        VerticalOptions = LayoutOptions.Start,
        Content = new Label { Text = text, FontSize = 13, FontFamily = "OpenSansSemibold", TextColor = color },
    };

    /// <summary>A figure with its name beneath it, for the home screen's summary.</summary>
    public static Border Stat(string value, string label, Color color, Action? onTap = null)
    {
        var number = new Label { Text = value, FontSize = 28, FontFamily = "OpenSansSemibold", TextColor = color };
        var panel = Panel(new VerticalStackLayout { Spacing = 2, Children = { number, Caption(label) } }, Surface, Border);
        SemanticProperties.SetDescription(panel, $"{label}: {value}");
        if (onTap is not null)
        {
            Tappable(panel, onTap);
        }

        return panel;
    }

    /// <summary>A row in a menu: icon, words, and a chevron saying it goes somewhere.</summary>
    public static View MenuRow(string icon, string title, string? subtitle, Action onTap, Color? accent = null)
    {
        var grid = new Grid
        {
            ColumnDefinitions = [new ColumnDefinition(40), new ColumnDefinition(GridLength.Star), new ColumnDefinition(24)],
            ColumnSpacing = 12,
            MinimumHeightRequest = 64,
            Padding = new Thickness(16, 10),
            BackgroundColor = Surface,
        };
        grid.Add(Icon(icon, accent ?? Primary, 26), 0);

        var words = new VerticalStackLayout { VerticalOptions = LayoutOptions.Center, Spacing = 2 };
        var name = Body(title);
        name.FontFamily = "OpenSansSemibold";
        words.Add(name);
        if (subtitle is not null)
        {
            words.Add(Caption(subtitle));
        }

        grid.Add(words, 1);
        grid.Add(Icon(Icons.ChevronForward, TextMuted, 20), 2);
        SemanticProperties.SetDescription(grid, subtitle is null ? title : $"{title}. {subtitle}");
        Tappable(grid, onTap);
        return grid;
    }

    /// <summary>A vector icon from 24-unit path data: drawn, not a font, so nothing is downloaded.</summary>
    public static View Icon(string pathData, Color color, double size = 24)
    {
        var path = new MauiPath
        {
            Data = (Geometry)new PathGeometryConverter().ConvertFromInvariantString(pathData)!,
            Fill = color,
            Aspect = Stretch.Uniform,
            WidthRequest = size,
            HeightRequest = size,
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center,
        };
        // Decorative: the word beside it is what a screen reader should say.
        SemanticProperties.SetDescription(path, string.Empty);
        AutomationProperties.SetIsInAccessibleTree(path, false);
        return path;
    }

    /// <summary>
    /// An input with its name above it, shown and hidden with it. A placeholder
    /// vanishes once something is typed, and three name fields for a mother with
    /// no labels are three guesses. The name is not repeated inside the box.
    /// </summary>
    public static View Labeled(View input)
    {
        var label = input switch
        {
            Entry entry => entry.Placeholder,
            Picker picker => picker.Title,
            _ => "",
        };

        if (input is Entry box)
        {
            box.Placeholder = string.Empty;
        }

        return Labeled(label, input);
    }

    public static View Labeled(string label, View input)
    {
        var caption = Caption(label);
        caption.FontFamily = "OpenSansSemibold";
        return ShownWith(new VerticalStackLayout { Spacing = 4, Children = { caption, input } }, input);
    }

    /// <summary>A wrapper that shows and hides with what it wraps.</summary>
    public static View ShownWith(View wrapper, View inner)
    {
        wrapper.SetBinding(VisualElement.IsVisibleProperty, new Binding(nameof(VisualElement.IsVisible), source: inner));
        return wrapper;
    }

    /// <summary>A line that takes no room until it has something to say.</summary>
    public static Label HideWhenEmpty(Label label)
    {
        label.IsVisible = !string.IsNullOrEmpty(label.Text);
        label.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(Label.Text))
            {
                label.IsVisible = !string.IsNullOrEmpty(label.Text);
            }
        };
        return label;
    }

    public static void Tappable(View view, Action onTap)
    {
        var tap = new TapGestureRecognizer();
        tap.Tapped += (_, _) => onTap();
        view.GestureRecognizers.Add(tap);
        AutomationProperties.SetIsInAccessibleTree(view, true);
    }
}

/// <summary>
/// Icons as 24×24 path data, from Google's Material icons (Apache 2.0). Drawn
/// by the app, so no icon font has to be shipped or fetched.
/// </summary>
public static class Icons
{
    public const string Home = "M10 20v-6h4v6h5v-8h3L12 3 2 12h3v8z";
    public const string Add = "M19 13h-6v6h-2v-6H5v-2h6V5h2v6h6v2z";
    public const string Sync = "M12 4V1L8 5l4 4V6c3.31 0 6 2.69 6 6 0 1.01-.25 1.97-.7 2.8l1.46 1.46C19.54 15.03 20 13.57 20 12c0-4.42-3.58-8-8-8zm0 14c-3.31 0-6-2.69-6-6 0-1.01.25-1.97.7-2.8L5.24 7.74C4.46 8.97 4 10.43 4 12c0 4.42 3.58 8 8 8v3l4-4-4-4v3z";
    public const string Verified = "M12 2C6.48 2 2 6.48 2 12s4.48 10 10 10 10-4.48 10-10S17.52 2 12 2zm-2 15l-5-5 1.41-1.41L10 14.17l7.59-7.59L19 8l-9 9z";
    public const string More = "M3 18h18v-2H3v2zm0-5h18v-2H3v2zm0-7v2h18V6H3z";
    public const string Print = "M19 8H5c-1.66 0-3 1.34-3 3v6h4v4h12v-4h4v-6c0-1.66-1.34-3-3-3zm-3 11H8v-5h8v5zm3-7c-.55 0-1-.45-1-1s.45-1 1-1 1 .45 1 1-.45 1-1 1zm-1-9H6v4h12V3z";
    public const string Lock = "M18 8h-1V6c0-2.76-2.24-5-5-5S7 3.24 7 6v2H6c-1.1 0-2 .9-2 2v10c0 1.1.9 2 2 2h12c1.1 0 2-.9 2-2V10c0-1.1-.9-2-2-2zm-6 9c-1.1 0-2-.9-2-2s.9-2 2-2 2 .9 2 2-.9 2-2 2zm3.1-9H8.9V6c0-1.71 1.39-3.1 3.1-3.1 1.71 0 3.1 1.39 3.1 3.1v2z";
    public const string Usb = "M15 7v4h1v2h-3V5h2l-3-4-3 4h2v8H8v-2.07c.7-.37 1.2-1.08 1.2-1.93 0-1.21-.99-2.2-2.2-2.2S4.8 7.79 4.8 9c0 .85.5 1.56 1.2 1.93V13c0 1.11.89 2 2 2h3v3.05c-.71.37-1.2 1.1-1.2 1.95 0 1.22.99 2.2 2.2 2.2s2.2-.98 2.2-2.2c0-.85-.49-1.58-1.2-1.95V15h3c1.11 0 2-.89 2-2v-2h1V7h-4z";
    public const string Warning = "M1 21h22L12 2 1 21zm12-3h-2v-2h2v2zm0-4h-2v-4h2v4z";
    public const string Error = "M12 2C6.48 2 2 6.48 2 12s4.48 10 10 10 10-4.48 10-10S17.52 2 12 2zm1 15h-2v-2h2v2zm0-4h-2V7h2v6z";
    public const string Person = "M12 12c2.21 0 4-1.79 4-4s-1.79-4-4-4-4 1.79-4 4 1.79 4 4 4zm0 2c-2.67 0-8 1.34-8 4v2h16v-2c0-2.66-5.33-4-8-4z";
    public const string Language = "M12.87 15.07l-2.54-2.51.03-.03c1.74-1.94 2.98-4.17 3.71-6.53H17V4h-7V2H8v2H1v1.99h11.17C11.5 7.92 10.44 9.75 9 11.35 8.07 10.32 7.3 9.19 6.69 8h-2c.73 1.63 1.73 3.17 2.98 4.56l-5.09 5.02L4 19l5-5 3.11 3.11.76-2.04zM18.5 10h-2L12 22h2l1.12-3h4.75L21 22h2l-4.5-12zm-2.62 7l1.62-4.33L19.12 17h-3.24z";
    public const string ChevronForward = "M10 6L8.59 7.41 13.17 12l-4.58 4.59L10 18l6-6z";
    public const string Hospital = "M19 3H5c-1.1 0-1.99.9-1.99 2L3 19c0 1.1.9 2 2 2h14c1.1 0 2-.9 2-2V5c0-1.1-.9-2-2-2zm-1 11h-4v4h-4v-4H6v-4h4V6h4v4h4v4z";
    public const string Upload = "M9 16h6v-6h4l-7-7-7 7h4zm-4 2h14v2H5z";
    public const string Numbers = "M20 10V8h-4V4h-2v4h-4V4H8v4H4v2h4v4H4v2h4v4h2v-4h4v4h2v-4h4v-2h-4v-4h4zm-6 4h-4v-4h4v4z";
}
