using Microsoft.Maui.Controls.Shapes;
using NCBRS.Client.App.Services;
using NCBRS.Client.Auth;
using NCBRS.Client.Localization;
using NCBRS.Client.Storage;

namespace NCBRS.Client.App.Pages;

/// <summary>
/// Unlock: tap who you are, enter your PIN on the keypad. Checked on the
/// tablet with no signal; wrong guesses count for the tablet, not the name.
///
/// Two departures from the design's mockup, both to keep how unlocking works:
/// - **The PIN is 6 to 12 digits**, not 4, so the dots grow as it is typed
///   rather than standing at four.
/// - **Unlocking is a button, never automatic.** With a PIN of any length
///   from 6 to 12 the tablet cannot know when one is finished, and trying each
///   length as it is reached would spend the tablet's few wrong guesses on a
///   PIN still being typed. The button waits for six digits, for the same
///   reason: nothing shorter is a PIN, so it should cost no guess.
/// </summary>
public sealed class UnlockPage : FlowPage
{
    private readonly DeviceHost _host;
    private readonly List<(StaffCredential Person, Border Card, View Tick)> _cards = [];
    private readonly HorizontalStackLayout _dots = new() { Spacing = Space.Md, HorizontalOptions = LayoutOptions.Center };
    private readonly Button _unlock = Ui.PrimaryButton(Strings.Unlock_Button);
    private readonly VerticalStackLayout _someoneElse;

    // What to do about a forgotten PIN, shown when asked: a district officer
    // resets it at the registry, and the registrar sets a new one here.
    private readonly Border _forgot = Ui.Notice(Icons.Key, Strings.Unlock_ForgotBody, Tone.Primary);
    private StaffCredential? _who;
    private string _pin = "";

    public UnlockPage(DeviceHost host) : base(Strings.Unlock_Title)
    {
        _host = host;

        var staff = new List<View>();
        foreach (var person in host.State.Staff)
        {
            staff.Add(StaffCard(person));
        }

        _someoneElse = SomeoneElse(host);
        staff.Add(SomeoneElseCard());

        // One registrar at the facility: they are who is unlocking.
        if (host.State.Staff.Count == 1)
        {
            Choose(host.State.Staff[0]);
        }

        _unlock.Clicked += async (_, _) => await RunAsync(UnlockAsync);
        PaintDots();

        var pinForm = new PinForm(host, askCurrent: true, onSet: () => Flow.Advance(host), run: RunAsync, status: Status);

        // Checking a certificate needs no one unlocked: it shows only what the
        // paper itself says, verified against public keys, and nothing the
        // tablet holds. A teacher or a clinic clerk at the post can use it.
        var checkCertificate = Ui.SecondaryButton(Strings.Register_Check);
        checkCertificate.Clicked += (_, _) => Flow.Show(new CheckCertificatePage(host));

        var pinLabel = Ui.SectionLabel(Strings.Unlock_EnterPin);
        pinLabel.HorizontalTextAlignment = TextAlignment.Center;

        var views = new List<View>
        {
            Ui.PageHeader(Strings.Unlock_Welcome, host.EnrolledTo),
            Ui.Group(Strings.Unlock_WhoUsing, Ui.Columns(2, [.. staff]), _someoneElse),
            new VerticalStackLayout { Spacing = Space.Md, Children = { pinLabel, _dots, _forgot, Keypad(), Status } },
            _unlock,
            checkCertificate,
            Ui.Link(Strings.Unlock_ChangePin, () => pinForm.View.IsVisible = !pinForm.View.IsVisible),
            pinForm.View,
        };

#if DEBUG
        // Debug builds only: the printer screen without unlocking, so printing
        // can be tested after a reinstall without a registrar's PIN. Release
        // builds keep the printer setting behind the unlock.
        var printer = Ui.GhostButton("Printer (debug)");
        printer.Clicked += (_, _) => Flow.Show(new PrinterPage(host));
        views.Add(printer);
#endif

        Build(host, [.. views]);
    }

    // --- who -----------------------------------------------------------------------------------------

    /// <summary>A person on the facility's staff, by initials and name. Chosen, it is outlined and ticked.</summary>
    private Border StaffCard(StaffCredential person)
    {
        var tick = Ui.Icon(Icons.Tick, Ui.Primary, 20);
        tick.IsVisible = false;

        var row = new Grid
        {
            ColumnDefinitions = [new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto)],
            ColumnSpacing = Space.Md,
        };
        row.Add(Ui.Avatar(person.DisplayName, 40), 0);
        var name = Ui.Heading(person.DisplayName);
        name.VerticalOptions = LayoutOptions.Center;
        name.MaxLines = 2;
        row.Add(name, 1);
        row.Add(tick, 2);

        var card = Choice(row);
        Ui.Tappable(card, () => Choose(person));
        _cards.Add((person, card, tick));
        Paint();
        return card;
    }

    /// <summary>
    /// Someone not on the list: a dashed card. Tapped, it says why they are
    /// missing and offers the way on, rather than a dead end.
    /// </summary>
    private Border SomeoneElseCard()
    {
        var icon = new Border
        {
            BackgroundColor = Ui.Muted,
            StrokeThickness = 0,
            StrokeShape = new Ellipse(),
            WidthRequest = 40,
            HeightRequest = 40,
            Content = Ui.Icon(Icons.PersonAdd, Ui.TextMuted, 20),
        };

        var row = new Grid
        {
            ColumnDefinitions = [new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star)],
            ColumnSpacing = Space.Md,
        };
        row.Add(icon, 0);
        var label = Ui.Heading(Strings.Unlock_SomeoneElse);
        label.TextColor = Ui.TextMuted;
        label.VerticalOptions = LayoutOptions.Center;
        row.Add(label, 1);

        var card = Choice(row);
        card.Stroke = Ui.Theme.Ring;
        card.StrokeDashArray = [4, 3];
        card.Shadow = null!;
        SemanticProperties.SetDescription(card, Strings.Unlock_SomeoneElse);
        Ui.Tappable(card, () => _someoneElse.IsVisible = !_someoneElse.IsVisible);
        return card;
    }

    /// <summary>
    /// Why someone is not on the list, and the one way on: a PIN reaches this
    /// tablet at its next sync, and a person with no PIN yet sets one by
    /// signing in with their own account, which needs signal.
    /// </summary>
    private VerticalStackLayout SomeoneElse(DeviceHost host)
    {
        var signIn = Ui.OutlineButton(Strings.Recovery_SomeoneElse);
        signIn.Clicked += async (_, _) => await RunAsync(async () =>
        {
            await host.SignOutRegistrarAsync();
            Flow.Advance(host);
        });

        return new VerticalStackLayout
        {
            Spacing = Space.Md,
            IsVisible = false,
            Children = { Ui.Card(Ui.Body(Strings.Unlock_SomeoneElseBody, Ui.TextMuted), signIn) },
        };
    }

    private static Border Choice(View content) => new()
    {
        Content = content,
        BackgroundColor = Ui.Surface,
        StrokeThickness = 1,
        StrokeShape = new RoundRectangle { CornerRadius = Radius.Base },
        Padding = Space.Md,
        MinimumHeightRequest = 64,
        Shadow = Ui.Faint(),
    };

    private void Choose(StaffCredential person)
    {
        _who = person;
        Status.Text = "";
        Paint();
    }

    private void Paint()
    {
        foreach (var (person, card, tick) in _cards)
        {
            var chosen = person == _who;
            card.Stroke = chosen ? Ui.Primary : Ui.Border;
            card.StrokeThickness = chosen ? 2 : 1;
            tick.IsVisible = chosen;
            // A screen reader hears who is chosen, not only a blue outline.
            SemanticProperties.SetDescription(card, chosen
                ? Language.Format(Strings.Unlock_Chosen, person.DisplayName)
                : person.DisplayName);
        }
    }

    // --- the PIN -------------------------------------------------------------------------------------

    /// <summary>
    /// The digits on keys 64 tall, in a phone's layout. Left to right in
    /// Arabic too, as every phone keypad is: a mirrored keypad would put 3
    /// where a thumb expects 1.
    /// </summary>
    private Grid Keypad()
    {
        var grid = new Grid
        {
            ColumnSpacing = Space.Md,
            RowSpacing = Space.Md,
            FlowDirection = FlowDirection.LeftToRight,
            ColumnDefinitions = [new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Star)],
        };

        _forgot.IsVisible = false;
        string[] keys = ["1", "2", "3", "4", "5", "6", "7", "8", "9", "?", "0", "⌫"];
        for (var i = 0; i < keys.Length; i++)
        {
            if (i % 3 == 0)
            {
                grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            }

            var key = keys[i];
            if (key == "?")
            {
                grid.Add(ForgotKey(), i % 3, i / 3);
                continue;
            }

            grid.Add(key == "⌫" ? Key(Ui.Icon(Icons.Backspace, Ui.Text, 24), Strings.Unlock_Backspace, Backspace, outlined: false)
                                : Key(new Label { Text = key, FontSize = 24, FontFamily = Ui.SemiBold, TextColor = Ui.Text, HorizontalOptions = LayoutOptions.Center, VerticalOptions = LayoutOptions.Center }, key, () => Type(key)),
                i % 3, i / 3);
        }

        return grid;
    }

    /// <summary>
    /// "Forgot PIN?" where the keypad has no key, as on a phone. It says what
    /// to do rather than doing anything: a PIN is reset at the registry by a
    /// district officer, never on the tablet, so the tablet cannot be talked
    /// into clearing one.
    /// </summary>
    private View ForgotKey()
    {
        var label = new Label
        {
            Text = Strings.Unlock_Forgot,
            FontSize = 14,
            FontFamily = Ui.SemiBold,
            TextColor = Ui.Primary,
            HorizontalTextAlignment = TextAlignment.Center,
            VerticalTextAlignment = TextAlignment.Center,
            // Read in the page's language, inside a keypad held left to right.
            FlowDirection = AppLanguage.Direction,
        };
        return Key(label, Strings.Unlock_Forgot, () => _forgot.IsVisible = !_forgot.IsVisible, outlined: false);
    }

    private static Border Key(View face, string name, Action onTap, bool outlined = true)
    {
        var key = new Border
        {
            Content = face,
            BackgroundColor = Ui.Surface,
            Stroke = outlined ? Ui.Border : Colors.Transparent,
            StrokeThickness = 1,
            StrokeShape = new RoundRectangle { CornerRadius = Radius.Base },
            HeightRequest = 64,
            Shadow = outlined ? Ui.Faint() : null!,
        };
        SemanticProperties.SetDescription(key, name);
        Ui.Tappable(key, onTap);
        return key;
    }

    private void Type(string digit)
    {
        if (_pin.Length >= PinPolicy.MaximumLength)
        {
            return;
        }

        _pin += digit;
        Status.Text = "";
        PaintDots();
    }

    private void Backspace()
    {
        if (_pin.Length > 0)
        {
            _pin = _pin[..^1];
            PaintDots();
        }
    }

    /// <summary>
    /// Six dots to start with, the shortest PIN there is, and one more for each
    /// digit past six. Filled as digits are typed; never the digits themselves.
    /// </summary>
    private void PaintDots()
    {
        var count = Math.Max(PinPolicy.MinimumLength, _pin.Length);
        _dots.Clear();
        for (var i = 0; i < count; i++)
        {
            var filled = i < _pin.Length;
            _dots.Add(new Ellipse
            {
                WidthRequest = 14,
                HeightRequest = 14,
                Fill = filled ? Ui.Primary : Colors.Transparent,
                Stroke = filled ? Ui.Primary : Ui.Theme.Ring,
                StrokeThickness = 2,
            });
        }

        SemanticProperties.SetDescription(_dots, Language.Format(Strings.Unlock_Digits, _pin.Length));
        _unlock.IsEnabled = _pin.Length >= PinPolicy.MinimumLength;
        _unlock.Opacity = _unlock.IsEnabled ? 1 : 0.5;
    }

    private async Task UnlockAsync()
    {
        if (_who is not { } who)
        {
            Status.Text = Strings.Unlock_ChooseName;
            return;
        }

        var result = await _host.UnlockAsync(who, _pin);
        _pin = "";
        PaintDots();
        if (result.Unlocked)
        {
            Flow.Advance(_host);
            return;
        }

        Status.Text = result.Outcome == UnlockOutcome.LockedOut
            ? Language.Format(Strings.Unlock_LockedOut, result.LockedUntilUtc?.ToLocalTime())
            : Language.Format(Strings.Unlock_Wrong, result.AttemptsRemaining);
    }
}
