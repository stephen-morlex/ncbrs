using NCBRS.Client.App.Services;
using NCBRS.Client.Auth;
using NCBRS.Client.Storage;

namespace NCBRS.Client.App.Pages;

/// <summary>A registrar signs the tablet in once; it then keeps an offline sign-in for weeks without signal.</summary>
public sealed class RegistrarSignInPage : FlowPage
{
    public RegistrarSignInPage(DeviceHost host) : base("Sign in")
    {
        var signIn = new Button { Text = "Registrar: sign in" };
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
            Heading($"Tablet {host.State.Identity?.DeviceId} is enrolled"),
            Note($"Enrolled to {host.EnrolledTo}."),
            Note("A registrar or community health worker at this facility signs it in once. It then stays signed in "
                 + "while offline, and syncs whenever there is signal."),
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
        var someoneElse = new Button { Text = "Sign in as someone else", BackgroundColor = Colors.Gray, IsVisible = host.State.OfflineToken is not null };
        var again = new Button { Text = "Wrong facility: hand over again (district officer)", BackgroundColor = Colors.Gray };

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
                new Label { Text = "Not the right facility or account?", FontAttributes = FontAttributes.Bold },
                new Label
                {
                    FontSize = 13,
                    Text = $"This tablet is enrolled to {host.EnrolledTo}. A registrar from another facility cannot use it. "
                           + "If it was handed over to the wrong facility, a district officer revokes it here and hands it over again.",
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

    public ProvisionPage(DeviceHost host) : base("Setting up")
    {
        _host = host;
        _pin = new PinForm(host, askCurrent: false, onSet: () => Flow.Advance(host), run: RunAsync, status: Status);
        var retry = new Button { Text = "Try again" };
        retry.Clicked += async (_, _) => await RunAsync(ProvisionAsync);
        Build(
            Heading("Setting up this tablet"),
            Note($"Enrolled to {host.EnrolledTo}. Drawing a block of registration numbers, the certificate checks, and who can unlock it."),
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
            ? "Nobody at this facility has an offline PIN yet. Set yours to unlock the tablet."
            : string.Join("\n", problems);
    }
}

/// <summary>Set (or change) the signed-in registrar's PIN at the centre. Needs signal.</summary>
public sealed class PinForm
{
    public PinForm(DeviceHost host, bool askCurrent, Action onSet, Func<Func<Task>, Task> run, Label status)
    {
        var current = new Entry { Placeholder = "Current PIN (blank if you have never set one)", IsPassword = true, Keyboard = Keyboard.Numeric, IsVisible = askCurrent };
        var pin = new Entry { Placeholder = "New PIN (6 to 12 digits)", IsPassword = true, Keyboard = Keyboard.Numeric };
        var again = new Entry { Placeholder = "New PIN again", IsPassword = true, Keyboard = Keyboard.Numeric };
        var set = new Button { Text = "Set my PIN" };
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
                status.Text = "The two PINs are different.";
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
                new Label { Text = "Your offline PIN", FontAttributes = FontAttributes.Bold },
                new Label { Text = "Set at the registry, so it works on any tablet at this facility. Not a run or a repeated digit.", FontSize = 13 },
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
    public UnlockPage(DeviceHost host) : base("Unlock")
    {
        var person = new Picker
        {
            Title = "Who are you?",
            ItemsSource = host.State.Staff.ToList(),
            ItemDisplayBinding = new Binding(nameof(StaffCredential.DisplayName)),
        };
        var pin = new Entry { Placeholder = "PIN", IsPassword = true, Keyboard = Keyboard.Numeric };
        var unlock = new Button { Text = "Unlock" };
        var pinForm = new PinForm(host, askCurrent: true, onSet: () => Flow.Advance(host), run: RunAsync, status: Status);
        var changePin = new Button { Text = "Set or change my PIN (needs signal)", BackgroundColor = Colors.Gray };
        changePin.Clicked += (_, _) => pinForm.View.IsVisible = !pinForm.View.IsVisible;

        unlock.Clicked += async (_, _) => await RunAsync(async () =>
        {
            if (person.SelectedItem is not StaffCredential who)
            {
                Status.Text = "Choose your name.";
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
                ? $"Too many wrong PINs. Locked until {result.LockedUntilUtc?.ToLocalTime():HH:mm}."
                : $"Wrong PIN. {result.AttemptsRemaining} tries left before the tablet locks.";
        });

        Build(Heading("Unlock"), person, pin, unlock, changePin, pinForm.View);
    }
}
