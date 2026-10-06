using Microsoft.Maui.Controls.Shapes;
using NCBRS.Client.Localization;
using MauiPath = Microsoft.Maui.Controls.Shapes.Path;

namespace NCBRS.Client.App.Pages;

/// <summary>
/// The colours, by what they are for (the design handoff's shadcn preset,
/// light). Every colour on every screen is read from here, so a dark palette
/// is one more instance rather than a hunt through the pages.
/// </summary>
public sealed record Palette(
    Color Background,
    Color Foreground,
    Color Primary,
    Color PrimaryForeground,
    Color PrimaryStrong,
    Color Secondary,
    Color SecondaryForeground,
    Color Muted,
    Color MutedForeground,
    Color Border,
    Color Ring,
    Color Destructive,
    Color Pending,
    Color PendingBackground,
    Color PendingForeground)
{
    public static readonly Palette Light = new(
        Background: Color.FromArgb("#FFFFFF"),
        Foreground: Color.FromArgb("#0A0A0A"),
        Primary: Color.FromArgb("#1447E6"),
        PrimaryForeground: Color.FromArgb("#EFF6FF"),
        PrimaryStrong: Color.FromArgb("#193CB8"),
        Secondary: Color.FromArgb("#F4F4F5"),
        SecondaryForeground: Color.FromArgb("#18181B"),
        Muted: Color.FromArgb("#F5F5F5"),
        MutedForeground: Color.FromArgb("#737373"),
        Border: Color.FromArgb("#E5E5E5"),
        Ring: Color.FromArgb("#A1A1A1"),
        Destructive: Color.FromArgb("#E7000B"),
        // "Waiting to sync" only: a birth on this tablet the registry has not got yet.
        Pending: Color.FromArgb("#B26A00"),
        PendingBackground: Color.FromArgb("#FCEFD6"),
        PendingForeground: Color.FromArgb("#8A5300"));
}

/// <summary>
/// The only spacing there is. A screen is a vertical stack with
/// <see cref="Xl"/> between sections; a label sits <see cref="Sm"/> above
/// what it names; grids and rows use <see cref="Md"/>; cards pad by
/// <see cref="Lg"/>. A number that is not here is a one-off, and one-offs
/// are how a screen stops lining up with the next.
/// </summary>
public static class Space
{
    public const double Xs = 4;
    public const double Sm = 8;
    public const double Md = 12;
    public const double Lg = 16;
    public const double Xl = 20;

    /// <summary>Text inside an input, from its edge.</summary>
    public const double InputInset = 14;
}

/// <summary>Corner radii: buttons, inputs and chips; cards; badges; the selected tab.</summary>
public static class Radius
{
    public const double Base = 10;
    public const double Card = 14;
    public const double Badge = 6;
    public const double Tab = 7;
}

/// <summary>What a badge says about a birth: registered, a neutral fact, waiting, or refused.</summary>
public enum Tone
{
    Primary,
    Neutral,
    Pending,
    Danger,
}

/// <summary>
/// The tablet's look, in one place, for the people who use it: midwives and
/// community health workers, often in bright sun, sometimes with a family
/// waiting.
/// - **Touch targets of at least 48 dp**; the main action is 56.
/// - **Body text of 16 sp, helper text never under 13.**
/// - **An icon never stands alone.** Every icon has its word beside it, or an
///   accessible label when there is no room for one.
/// - **Colour is never the only signal.** A state is also said in words.
/// </summary>
public static class Ui
{
    // --- colour ------------------------------------------------------------------------------------

    public static Palette Theme { get; set; } = Palette.Light;

    public static Color Background => Theme.Background;

    /// <summary>Cards and bars: the same white as the page, set apart by a border.</summary>
    public static Color Surface => Theme.Background;

    public static Color Text => Theme.Foreground;

    public static Color TextMuted => Theme.MutedForeground;

    public static Color Primary => Theme.Primary;

    /// <summary>Text on the primary colour.</summary>
    public static Color OnPrimary => Theme.PrimaryForeground;

    /// <summary>The light tint behind an icon, an avatar or the active tab.</summary>
    public static Color PrimaryTint => Theme.PrimaryForeground;

    public static Color PrimaryStrong => Theme.PrimaryStrong;

    public static Color Muted => Theme.Muted;

    public static Color Border => Theme.Border;

    public static Color Danger => Theme.Destructive;

    public static Color Pending => Theme.Pending;

    // --- size --------------------------------------------------------------------------------------

    public const double TouchTarget = 48;
    public const double ButtonHeight = 56;
    public const double InputHeight = 52;
    public const double ChipHeight = 44;

    // --- type --------------------------------------------------------------------------------------

    /// <summary>Geist for Latin text; Noto Sans Arabic for Arabic, which Geist does not draw.</summary>
    public static string Regular => Language.IsArabic ? "NotoSansArabicRegular" : "GeistRegular";

    public static string SemiBold => Language.IsArabic ? "NotoSansArabicSemiBold" : "GeistSemiBold";

    /// <summary>Registration numbers: a mono face keeps 0 and O, 1 and I apart when read aloud or copied.</summary>
    public const string Mono = "GeistMonoSemiBold";

    /// <summary>
    /// A screen's title. Tightened slightly in Latin; never in Arabic, whose
    /// letters join, and spacing them apart breaks the word.
    /// </summary>
    public static Label Title(string text) => new()
    {
        Text = text, FontSize = 26, FontFamily = SemiBold, TextColor = Text,
        CharacterSpacing = Language.IsArabic ? 0 : -0.4,
    };

    /// <summary>A title with its subtitle, as one block.</summary>
    public static View PageHeader(string title, string? subtitle = null)
    {
        var stack = new VerticalStackLayout { Spacing = Space.Xs, Children = { Title(title) } };
        if (!string.IsNullOrEmpty(subtitle))
        {
            stack.Add(Subtitle(subtitle));
        }

        return stack;
    }

    public static Label SectionTitle(string text) => new()
    {
        Text = text, FontSize = 22, FontFamily = SemiBold, TextColor = Text,
    };

    /// <summary>A card's or a row's own title.</summary>
    public static Label Heading(string text) => new()
    {
        Text = text, FontSize = 16, FontFamily = SemiBold, TextColor = Text,
    };

    public static Label Body(string text, Color? color = null) => new()
    {
        Text = text, FontSize = 16, FontFamily = Regular, TextColor = color ?? Text, LineHeight = 1.2,
    };

    public static Label Subtitle(string text) => new()
    {
        Text = text, FontSize = 15, FontFamily = Regular, TextColor = TextMuted,
    };

    /// <summary>Helper text: what a field is for, a date beneath a name.</summary>
    public static Label Caption(string text, Color? color = null) => new()
    {
        Text = text, FontSize = 13, FontFamily = Regular, TextColor = color ?? TextMuted,
    };

    /// <summary>The name of a group on a screen ("Other tasks", "Today"), above its content.</summary>
    public static Label SectionLabel(string text) => new()
    {
        Text = text, FontSize = 14, FontFamily = SemiBold, TextColor = TextMuted,
    };

    public static Label FieldLabel(string text) => new()
    {
        Text = text, FontSize = 14, FontFamily = SemiBold, TextColor = Text,
    };

    /// <summary>
    /// A registration number, as it is written: left to right whatever the
    /// language, but on the side the page reads from, so in Arabic it lines
    /// up with the words around it rather than sitting at the far edge.
    /// </summary>
    public static Label Number(string text, double size = 15, Color? color = null) => new()
    {
        Text = text, FontSize = size, FontFamily = Mono, TextColor = color ?? Text,
        FlowDirection = FlowDirection.LeftToRight,
        HorizontalTextAlignment = Language.IsArabic ? TextAlignment.End : TextAlignment.Start,
    };

    // --- buttons -----------------------------------------------------------------------------------

    public static Button PrimaryButton(string text)
    {
        var button = Styled(new Button { Text = text });
        button.BackgroundColor = Primary;
        button.TextColor = OnPrimary;
        return button;
    }

    /// <summary>
    /// White, with a border. <paramref name="emphasis"/> makes it the screen's
    /// second action, in the primary colour; without it, a quiet one that does
    /// not compete with what matters.
    /// </summary>
    public static Button OutlineButton(string text, bool emphasis = false)
    {
        var button = Styled(new Button { Text = text });
        button.BackgroundColor = Surface;
        button.TextColor = emphasis ? Primary : Text;
        button.BorderColor = emphasis ? Primary : Border;
        button.BorderWidth = 1;
        button.Shadow = Faint();
        return button;
    }

    public static Button SecondaryButton(string text) => OutlineButton(text, emphasis: true);

    public static Button GhostButton(string text) => OutlineButton(text);

    public static Button DangerButton(string text)
    {
        var button = Styled(new Button { Text = text });
        button.BackgroundColor = Danger;
        button.TextColor = Colors.White;
        return button;
    }

    private static Button Styled(Button button)
    {
        button.FontFamily = SemiBold;
        button.FontSize = 16;
        button.CornerRadius = (int)Radius.Base;
        button.HeightRequest = ButtonHeight;
        button.Padding = new Thickness(Space.Lg, 0);
        return button;
    }

    /// <summary>A text link, for an action that is not the point of the screen. Still a full touch target.</summary>
    public static View Link(string text, Action onTap)
    {
        var label = new Label
        {
            Text = text, FontSize = 15, FontFamily = SemiBold, TextColor = Primary,
            TextDecorations = TextDecorations.Underline,
            HorizontalTextAlignment = TextAlignment.Center, VerticalTextAlignment = TextAlignment.Center,
            MinimumHeightRequest = TouchTarget,
        };
        SemanticProperties.SetDescription(label, text);
        Tappable(label, onTap);
        return label;
    }

    /// <summary>
    /// A small outlined action with an icon and a word, such as the language
    /// switch. Drawn rather than a Button, which cannot hold a vector icon.
    /// </summary>
    public static Border ActionChip(string icon, string text, Action onTap, bool emphasis = true)
    {
        var colour = emphasis ? Primary : Text;
        var chip = new Border
        {
            BackgroundColor = Surface,
            Stroke = emphasis ? Primary : Border,
            StrokeThickness = 1,
            StrokeShape = new RoundRectangle { CornerRadius = Radius.Base },
            Padding = new Thickness(Space.Md, 0),
            HeightRequest = TouchTarget,
            Shadow = Faint(),
            Content = new HorizontalStackLayout
            {
                Spacing = Space.Sm,
                VerticalOptions = LayoutOptions.Center,
                Children =
                {
                    Icon(icon, colour, 18),
                    new Label { Text = text, FontSize = 15, FontFamily = SemiBold, TextColor = colour, VerticalOptions = LayoutOptions.Center },
                },
            },
        };
        SemanticProperties.SetDescription(chip, text);
        Tappable(chip, onTap);
        return chip;
    }

    // --- surfaces ----------------------------------------------------------------------------------

    /// <summary>The faint shadow every card, input and outline button carries.</summary>
    public static Shadow Faint() => new() { Brush = Colors.Black, Opacity = 0.05f, Radius = 2, Offset = new Point(0, 1) };

    /// <summary>A card: white, a hairline border, 14 radius, 16 padding. Every card is this card.</summary>
    public static Border Card(params View[] views)
    {
        var stack = new VerticalStackLayout { Spacing = Space.Md };
        foreach (var view in views)
        {
            stack.Add(view);
        }

        return CardOf(stack);
    }

    public static Border CardOf(View content, double padding = Space.Lg) => new()
    {
        Content = content,
        BackgroundColor = Surface,
        Stroke = Border,
        StrokeThickness = 1,
        StrokeShape = new RoundRectangle { CornerRadius = Radius.Card },
        Padding = padding,
        Shadow = Faint(),
    };

    /// <summary>A card holding a list: no padding of its own, a hairline between rows.</summary>
    public static Border ListCard(params View[] rows)
    {
        var stack = new VerticalStackLayout();
        for (var i = 0; i < rows.Length; i++)
        {
            if (i > 0)
            {
                stack.Add(Divider());
            }

            stack.Add(rows[i]);
        }

        return CardOf(stack, 0);
    }

    public static BoxView Divider() => new() { HeightRequest = 1, Color = Border };

    /// <summary>
    /// A row in a list card: something to recognise it by, its name and a line
    /// beneath, and at the end a badge or a chevron. Tappable when it goes somewhere.
    /// </summary>
    public static Grid Row(View? leading, string title, string? subtitle, View? trailing = null, Action? onTap = null, View? below = null)
    {
        var grid = new Grid
        {
            ColumnDefinitions = [new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto)],
            ColumnSpacing = Space.Md,
            Padding = new Thickness(Space.Lg, Space.Md),
            MinimumHeightRequest = 56,
            BackgroundColor = Surface,
        };

        if (leading is not null)
        {
            leading.VerticalOptions = LayoutOptions.Center;
            grid.Add(leading, 0);
        }

        var words = new VerticalStackLayout { VerticalOptions = LayoutOptions.Center, Spacing = Space.Xs };
        words.Add(Heading(title));
        if (!string.IsNullOrEmpty(subtitle))
        {
            words.Add(Caption(subtitle));
        }

        if (below is not null)
        {
            words.Add(below);
        }

        grid.Add(words, 1);

        trailing ??= onTap is null ? null : Icon(Icons.ChevronForward, TextMuted, 20, directional: true);
        if (trailing is not null)
        {
            trailing.VerticalOptions = LayoutOptions.Center;
            grid.Add(trailing, 2);
        }

        SemanticProperties.SetDescription(grid, string.IsNullOrEmpty(subtitle) ? title : $"{title}. {subtitle}");
        if (onTap is not null)
        {
            Tappable(grid, onTap);
        }

        return grid;
    }

    /// <summary>A menu row: an icon in a tinted square, words, and a chevron saying it goes somewhere.</summary>
    public static Grid MenuRow(string icon, string title, string? subtitle, Action onTap, Color? accent = null)
        => Row(IconBox(icon, accent), title, subtitle, onTap: onTap);

    /// <summary>An icon in a small tinted square, as on the task tiles.</summary>
    public static Border IconBox(string icon, Color? color = null, double size = 36)
    {
        var colour = color ?? Primary;
        return new Border
        {
            BackgroundColor = colour == Primary ? PrimaryTint : colour.WithAlpha(0.1f),
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = Radius.Base },
            WidthRequest = size,
            HeightRequest = size,
            HorizontalOptions = LayoutOptions.Start,
            Content = Icon(icon, colour, size * 0.55),
        };
    }

    /// <summary>A person's initials in a tinted circle: who registered, who is unlocking.</summary>
    public static Border Avatar(string name, double size = 40, bool strong = false) => new()
    {
        BackgroundColor = strong ? Primary : PrimaryTint,
        StrokeThickness = 0,
        StrokeShape = new Ellipse(),
        WidthRequest = size,
        HeightRequest = size,
        Content = new Label
        {
            Text = Initials(name), FontSize = size * 0.36, FontFamily = SemiBold,
            TextColor = strong ? OnPrimary : PrimaryStrong,
            HorizontalOptions = LayoutOptions.Center, VerticalOptions = LayoutOptions.Center,
        },
    };

    /// <summary>The first letters of the first and last words: "Alice Lado" is AL.</summary>
    public static string Initials(string? name)
    {
        var words = (name ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length switch
        {
            0 => "?",
            1 => words[0][..1].ToUpperInvariant(),
            _ => (words[0][..1] + words[^1][..1]).ToUpperInvariant(),
        };
    }

    /// <summary>A task on the home screen: an icon, its name, and an optional line. The whole tile is the button.</summary>
    public static Border Tile(string icon, string title, string? subtitle, Action onTap, Color? accent = null, string? badge = null)
    {
        var top = new Grid { ColumnDefinitions = [new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto)] };
        top.Add(IconBox(icon, accent, 32), 0);
        if (badge is not null)
        {
            top.Add(Badge(badge, Tone.Pending), 1);
        }

        var stack = new VerticalStackLayout { Spacing = Space.Sm, Children = { top, Heading(title) } };
        if (!string.IsNullOrEmpty(subtitle))
        {
            stack.Add(Caption(subtitle));
        }

        var tile = CardOf(stack);
        SemanticProperties.SetDescription(tile, string.IsNullOrEmpty(subtitle) ? title : $"{title}. {subtitle}");
        Tappable(tile, onTap);
        return tile;
    }

    /// <summary>
    /// A short state in words: Registered, Waiting to sync, Refused. The colour
    /// repeats what the words say and never says it alone.
    /// </summary>
    public static Border Badge(string text, Tone tone)
    {
        var (ink, paper) = tone switch
        {
            Tone.Primary => (PrimaryStrong, PrimaryTint),
            Tone.Pending => (Theme.PendingForeground, Theme.PendingBackground),
            Tone.Danger => (Danger, Danger.WithAlpha(0.08f)),
            _ => (Theme.SecondaryForeground, Theme.Secondary),
        };

        return new Border
        {
            BackgroundColor = paper,
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = Radius.Badge },
            Padding = new Thickness(Space.Sm, Space.Xs),
            HorizontalOptions = LayoutOptions.Start,
            VerticalOptions = LayoutOptions.Center,
            Content = new Label { Text = text, FontSize = 12, FontFamily = SemiBold, TextColor = ink },
        };
    }

    /// <summary>
    /// Something that needs attention, said in words: a refused birth, numbers
    /// running out. Tappable when there is somewhere to go about it.
    /// </summary>
    public static Border Notice(string icon, string text, Tone tone, Action? onTap = null)
    {
        var (ink, paper, edge) = tone switch
        {
            Tone.Danger => (Danger, Danger.WithAlpha(0.06f), Danger.WithAlpha(0.3f)),
            Tone.Pending => (Theme.PendingForeground, Theme.PendingBackground, Theme.PendingBackground),
            Tone.Primary => (PrimaryStrong, PrimaryTint, PrimaryTint),
            _ => (Text, Muted, Border),
        };

        var row = new Grid
        {
            ColumnDefinitions = [new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto)],
            ColumnSpacing = Space.Md,
        };
        row.Add(Icon(icon, ink, 22), 0);
        var label = new Label { Text = text, FontSize = 15, FontFamily = SemiBold, TextColor = ink, VerticalOptions = LayoutOptions.Center };
        row.Add(label, 1);
        if (onTap is not null)
        {
            row.Add(Icon(Icons.ChevronForward, ink, 20, directional: true), 2);
        }

        var notice = new Border
        {
            Content = row,
            BackgroundColor = paper,
            Stroke = edge,
            StrokeThickness = 1,
            StrokeShape = new RoundRectangle { CornerRadius = Radius.Base },
            Padding = new Thickness(Space.Lg, Space.Md),
        };
        SemanticProperties.SetDescription(notice, text);
        if (onTap is not null)
        {
            Tappable(notice, onTap);
        }

        return notice;
    }

    /// <summary>The label inside a notice, for a page that changes what it says.</summary>
    public static Label NoticeText(Border notice) => ((Grid)notice.Content!).Children.OfType<Label>().First();

    /// <summary>A figure with its name beneath it, in a grey box.</summary>
    public static Border Stat(string value, string label, Color? color = null, Action? onTap = null)
    {
        var stat = new Border
        {
            BackgroundColor = Muted,
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = Radius.Base },
            Padding = Space.Md,
            Content = new VerticalStackLayout
            {
                Spacing = Space.Xs,
                Children =
                {
                    new Label { Text = value, FontSize = 22, FontFamily = SemiBold, TextColor = color ?? Text },
                    Caption(label),
                },
            },
        };
        SemanticProperties.SetDescription(stat, $"{label}: {value}");
        if (onTap is not null)
        {
            Tappable(stat, onTap);
        }

        return stat;
    }

    /// <summary>Items in rows of <paramref name="columns"/>, 12 apart.</summary>
    public static Grid Columns(int columns, params View[] items)
    {
        var grid = new Grid { ColumnSpacing = Space.Md, RowSpacing = Space.Md };
        for (var c = 0; c < columns; c++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        }

        for (var i = 0; i < items.Length; i++)
        {
            if (i % columns == 0)
            {
                grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            }

            grid.Add(items[i], i % columns, i / columns);
        }

        return grid;
    }

    /// <summary>A label and its content, 8 apart: the unit every group on a screen is built from.</summary>
    public static VerticalStackLayout Group(string label, params View[] content)
    {
        var stack = new VerticalStackLayout { Spacing = Space.Sm, Children = { SectionLabel(label) } };
        foreach (var view in content)
        {
            stack.Add(view);
        }

        return stack;
    }

    // --- icons -------------------------------------------------------------------------------------

    /// <summary>
    /// An outline icon from 24-unit Lucide path data, stroked at 2. Drawn, not
    /// a font, so nothing is fetched. <paramref name="directional"/> icons
    /// (chevrons, a back arrow) point the other way in Arabic: forward is left.
    /// </summary>
    public static View Icon(string pathData, Color color, double size = 24, bool directional = false)
    {
        // A chevron is turned round by drawing its twin, not by flipping it:
        // a path flipped with ScaleX was not drawn at all on the blue
        // register card in Arabic, while the same flip drew in a list row.
        if (directional && Language.IsArabic)
        {
            if (pathData == Icons.ChevronForward)
            {
                pathData = Icons.ChevronBack;
                directional = false;
            }
            else if (pathData == Icons.ChevronBack)
            {
                pathData = Icons.ChevronForward;
                directional = false;
            }
        }

        var scale = size / 24;
        var path = new MauiPath
        {
            Data = (Geometry)new PathGeometryConverter().ConvertFromInvariantString(pathData)!,
            Stroke = color,
            StrokeThickness = 2,
            StrokeLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            Aspect = Stretch.None,
            RenderTransform = new ScaleTransform(scale, scale),
            WidthRequest = size,
            HeightRequest = size,
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center,
            // Drawn in its own coordinates whatever the page's direction: a
            // mirrored page would otherwise mirror every glyph, clocks included.
            FlowDirection = FlowDirection.LeftToRight,
        };

        if (directional && Language.IsArabic)
        {
            path.ScaleX = -1;
        }

        // In a box of its own size: a path set directly as a Border's content
        // was not drawn at all on Android, while the same path in a layout was.
        var box = new Grid
        {
            WidthRequest = size,
            HeightRequest = size,
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center,
            InputTransparent = true,
            Children = { path },
        };

        // Decorative: the word beside it is what a screen reader should say.
        AutomationProperties.SetIsInAccessibleTree(box, false);
        return box;
    }

    // --- inputs ------------------------------------------------------------------------------------

    /// <summary>
    /// An input in its box: 52 tall, a hairline border, the label above it
    /// rather than underlined. A unit ("g", "weeks") sits inside on the right;
    /// focused, the border turns primary with a soft ring, so it is plain
    /// which box the keyboard is typing into.
    /// </summary>
    public static View Input(View input, string? suffix = null, string? trailingIcon = null)
    {
        input.BackgroundColor = Colors.Transparent;
        input.VerticalOptions = LayoutOptions.Center;
        if (input is InputView or Picker or DatePicker)
        {
            input.MinimumHeightRequest = TouchTarget;
        }

        var row = new Grid
        {
            ColumnDefinitions = [new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto)],
            ColumnSpacing = Space.Sm,
        };
        row.Add(input, 0);
        if (suffix is not null)
        {
            row.Add(new Label { Text = suffix, FontSize = 15, FontFamily = Regular, TextColor = TextMuted, VerticalOptions = LayoutOptions.Center }, 1);
        }
        else if (trailingIcon is not null)
        {
            var icon = Icon(trailingIcon, TextMuted, 18);
            icon.InputTransparent = true;
            row.Add(icon, 1);
        }

        var box = new Border
        {
            Content = row,
            BackgroundColor = Surface,
            Stroke = Border,
            StrokeThickness = 1,
            StrokeShape = new RoundRectangle { CornerRadius = Radius.Base },
            Padding = new Thickness(Space.InputInset, 0),
            HeightRequest = InputHeight,
            Shadow = Faint(),
        };

        // The ring: a 3-unit band of primary at 20% around the box, drawn by
        // a wrapper that extends past the box rather than nudging it, so a
        // focused input stays aligned with its label.
        var ring = new Border
        {
            Content = box,
            BackgroundColor = Colors.Transparent,
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = Radius.Base + 3 },
            Padding = 3,
            Margin = -3,
        };

        void Focus(bool focused)
        {
            box.Stroke = focused ? Primary : Border;
            ring.BackgroundColor = focused ? Primary.WithAlpha(0.2f) : Colors.Transparent;
        }

        input.Focused += (_, _) => Focus(true);
        input.Unfocused += (_, _) => Focus(false);
        return ShownWith(ring, input);
    }

    /// <summary>
    /// An input with its name above it, shown and hidden with it. A placeholder
    /// vanishes once something is typed, and three name fields for a mother
    /// with no labels are three guesses; the name is not repeated inside the box.
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
        else if (input is Picker choice)
        {
            // Unanswered, the box says so, not the question a second time.
            choice.Title = Strings.Common_Choose;
        }

        return Labeled(label, input);
    }

    public static View Labeled(string label, View input, string? helper = null, string? suffix = null)
    {
        // Named above the box, so not again inside it: the weight and
        // pregnancy-length boxes showed their names twice, the second time
        // squeezed in beside the unit.
        if (input is Entry entry && entry.Placeholder == label)
        {
            entry.Placeholder = string.Empty;
        }

        var boxed = input switch
        {
            Picker => Input(input, trailingIcon: Icons.ChevronDown),
            DatePicker => Input(input, trailingIcon: Icons.Calendar),
            Entry => Input(input, suffix),
            _ => input,
        };

        var stack = new VerticalStackLayout { Spacing = Space.Sm, Children = { FieldLabel(label), boxed } };
        if (helper is not null)
        {
            stack.Add(Caption(helper));
        }

        return ShownWith(stack, input);
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
