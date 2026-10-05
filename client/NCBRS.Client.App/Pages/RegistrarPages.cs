using NCBRS.Client.App.Services;
using NCBRS.Client.Auth;
using NCBRS.Client.Localization;
using NCBRS.Client.Storage;

namespace NCBRS.Client.App.Pages;

/// <summary>A registrar signs the tablet in once; it then keeps an offline sign-in for weeks without signal.</summary>
public sealed class RegistrarSignInPage : FlowPage
{
    public RegistrarSignInPage(DeviceHost host) : base(Strings.SignIn_Title)
    {
        var signIn = Ui.PrimaryButton(Strings.SignIn_Button);
        signIn.Clicked += async (_, _) => await RunAsync(async () =>
        {
            if (await host.RegistrarSignInAsync() is { } problem)
            {
                Status.Text = problem;
                return;
            }

            Flow.Advance(host);
        });

        Build(
            Heading(Language.Format(Strings.SignIn_Heading, host.State.Identity?.DeviceId)),
            Note(Language.Format(Strings.SignIn_EnrolledTo, host.EnrolledTo)),
            Note(Strings.SignIn_Intro),
            signIn,
            new Recovery(host, RunAsync, Status).View);
    }
}

/// <summary>
/// The way out of a handover that went wrong: another account signs in, or the
/// officer revokes the tablet and hands it over again. Without it a tablet
/// enrolled to the wrong facility is stuck for good.
/// </summary>
public sealed class Recovery
{
    public Recovery(DeviceHost host, Func<Func<Task>, Task> run, Label status)
    {
        var someoneElse = Ui.GhostButton(Strings.Recovery_SomeoneElse);
        someoneElse.IsVisible = host.State.OfflineToken is not null;
        var again = Ui.GhostButton(Strings.Recovery_HandOverAgain);

        someoneElse.Clicked += async (_, _) => await run(async () =>
        {
            await host.SignOutRegistrarAsync();
            Flow.Advance(host);
        });

        again.Clicked += async (_, _) => await run(async () =>
        {
            if (await host.HandOverAgainAsync() is { } problem)
            {
                status.Text = problem;
                return;
            }

            Flow.Advance(host);
        });

        View = new VerticalStackLayout
        {
            Spacing = 10,
            Margin = new Thickness(0, 24, 0, 0),
            Children =
            {
                new Label { Text = Strings.Recovery_Title, FontAttributes = FontAttributes.Bold },
                new Label
                {
                    FontSize = 13,
                    Text = Language.Format(Strings.Recovery_Body, host.EnrolledTo),
                },
                someoneElse,
                again,
            },
        };
    }

    public VerticalStackLayout View { get; }
}

/// <summary>
/// Setting up after sign-in: the first block of numbers, the verification
/// bundle, and the staff who can unlock — and the registrar's PIN, set at the
/// centre, when nobody here has one yet.
/// </summary>
public sealed class ProvisionPage : FlowPage
{
    private readonly DeviceHost _host;
    private readonly PinForm _pin;

    public ProvisionPage(DeviceHost host) : base(Strings.Provision_Title)
    {
        _host = host;
        _pin = new PinForm(host, askCurrent: false, onSet: () => Flow.Advance(host), run: RunAsync, status: Status);
        var retry = Ui.PrimaryButton(Strings.Provision_Retry);
        retry.Clicked += async (_, _) => await RunAsync(ProvisionAsync);
        Build(
            Heading(Strings.Provision_Heading),
            Note(Language.Format(Strings.Provision_Intro, host.EnrolledTo)),
            retry,
            _pin.View,
            new Recovery(host, RunAsync, Status).View);
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await RunAsync(ProvisionAsync);
    }

    private async Task ProvisionAsync()
    {
        var problems = await _host.ProvisionAsync();
        if (_host.Stage != Stage.Provision)
        {
            Flow.Advance(_host);
            return;
        }

        _pin.View.IsVisible = _host.State.Brn is not null && _host.State.Staff.Count == 0;
        Status.Text = _pin.View.IsVisible && problems.Count == 0
            ? Strings.Provision_NoPins
            : string.Join("\n", problems);
    }
}

/// <summary>Set (or change) the signed-in registrar's PIN at the centre. Needs signal.</summary>
public sealed class PinForm
{
    public PinForm(DeviceHost host, bool askCurrent, Action onSet, Func<Func<Task>, Task> run, Label status)
    {
        var current = new Entry { Placeholder = Strings.Pin_Current, IsPassword = true, Keyboard = Keyboard.Numeric, IsVisible = askCurrent };
        var pin = new Entry { Placeholder = Strings.Pin_New, IsPassword = true, Keyboard = Keyboard.Numeric };
        var again = new Entry { Placeholder = Strings.Pin_Again, IsPassword = true, Keyboard = Keyboard.Numeric };
        var set = Ui.PrimaryButton(Strings.Pin_Set);
        set.Clicked += async (_, _) => await run(async () =>
        {
            // Checked here first, against the registry's own rules
            // (PinPolicyParityTests), so the reason shows while typing.
            if (PinPolicy.Problem(PinPolicy.Normalise(pin.Text)) is { } problem)
            {
                status.Text = problem;
                return;
            }

            if (PinPolicy.Normalise(pin.Text) != PinPolicy.Normalise(again.Text))
            {
                status.Text = Strings.Pin_Different;
                return;
            }

            var problems = await host.SetPinAsync(pin.Text ?? "", current.Text);
            pin.Text = again.Text = current.Text = "";
            if (problems.Count > 0)
            {
                status.Text = string.Join("\n", problems);
                return;
            }

            onSet();
        });

        View = new VerticalStackLayout
        {
            Spacing = 10,
            IsVisible = false,
            Children =
            {
                new Label { Text = Strings.Pin_Title, FontAttributes = FontAttributes.Bold },
                new Label { Text = Strings.Pin_Intro, FontSize = 13 },
                current, pin, again, set,
            },
        };
    }

    public VerticalStackLayout View { get; }
}

/// <summary>
/// Unlock: pick your name, enter your PIN. Checked on the tablet with no signal;
/// wrong guesses count for the tablet, not the name.
/// </summary>
public sealed class UnlockPage : FlowPage
{
    public UnlockPage(DeviceHost host) : base(Strings.Unlock_Title)
    {
        var person = new Picker
        {
            Title = Strings.Unlock_Who,
            ItemsSource = host.State.Staff.ToList(),
            ItemDisplayBinding = new Binding(nameof(StaffCredential.DisplayName)),
        };
        var pin = new Entry { Placeholder = Strings.Unlock_Pin, IsPassword = true, Keyboard = Keyboard.Numeric };
        var unlock = Ui.PrimaryButton(Strings.Unlock_Button);
        var pinForm = new PinForm(host, askCurrent: true, onSet: () => Flow.Advance(host), run: RunAsync, status: Status);
        var changePin = Ui.GhostButton(Strings.Unlock_ChangePin);
        changePin.Clicked += (_, _) => pinForm.View.IsVisible = !pinForm.View.IsVisible;

        unlock.Clicked += async (_, _) => await RunAsync(async () =>
        {
            if (person.SelectedItem is not StaffCredential who)
            {
                Status.Text = Strings.Unlock_ChooseName;
                return;
            }

            var result = await host.UnlockAsync(who, pin.Text ?? "");
            pin.Text = "";
            if (result.Unlocked)
            {
                Flow.Advance(host);
                return;
            }

            Status.Text = result.Outcome == UnlockOutcome.LockedOut
                ? Language.Format(Strings.Unlock_LockedOut, result.LockedUntilUtc?.ToLocalTime())
                : Language.Format(Strings.Unlock_Wrong, result.AttemptsRemaining);
        });

        // Checking a certificate needs no one unlocked: it shows only what the
        // paper itself says, verified against public keys, and nothing the
        // tablet holds. A teacher or a clinic clerk at the post can use it.
        var checkCertificate = Ui.SecondaryButton(Strings.Register_Check);
        checkCertificate.Clicked += (_, _) => Flow.Show(new CheckCertificatePage(host));

#if DEBUG
        // Debug builds only: the printer screen without unlocking, so printing
        // can be tested after a reinstall without a registrar's PIN. Release
        // builds keep the printer setting behind the unlock.
        var printer = Ui.GhostButton("Printer (debug)");
        printer.Clicked += (_, _) => Flow.Show(new PrinterPage(host));
        Build(Flow.LanguageSwitch(host), Heading(Strings.Unlock_Title), Ui.Card(Ui.Caption(Strings.Unlock_Who), person, Ui.Caption(Strings.Unlock_Pin), pin, unlock), changePin, pinForm.View, checkCertificate, printer);
#else
        Build(Flow.LanguageSwitch(host), Heading(Strings.Unlock_Title), Ui.Card(Ui.Caption(Strings.Unlock_Who), person, Ui.Caption(Strings.Unlock_Pin), pin, unlock), changePin, pinForm.View, checkCertificate);
#endif
    }
}
