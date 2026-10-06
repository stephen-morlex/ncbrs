using NCBRS.Certificates;
using NCBRS.Client.App.Services;
using NCBRS.Client.Certificates;
using NCBRS.Client.Localization;
#if ANDROID
using ZXing.Net.Maui;
using ZXing.Net.Maui.Controls;
#endif

namespace NCBRS.Client.App.Pages;

/// <summary>
/// Checking a certificate (B8), with no network: scan its QR code, or type or
/// paste the code. The verdict is the centre's own verifier's, run against the
/// bundle this tablet last downloaded (<see cref="CertificateCheck"/>); this page
/// only shows it.
///
/// The three answers look different on purpose, and "cannot be checked here"
/// is never green: a tablet whose list of withdrawn certificates is out of date
/// has found nothing wrong, but is in no position to approve either.
/// </summary>
public sealed class CheckCertificatePage : FlowPage
{
    private readonly DeviceHost _host;
    private readonly Entry _code = new()
    {
        Placeholder = "NCBRS1.",
        Keyboard = Keyboard.Plain,
        IsSpellCheckEnabled = false,
        IsTextPredictionEnabled = false,
        // A certificate code is Latin letters whatever the tablet's language.
        FlowDirection = FlowDirection.LeftToRight,
    };
    private readonly Label _camera = Ui.Caption("");
    private readonly VerticalStackLayout _answer = new() { Spacing = Space.Sm };
    private readonly Border _result;
    private readonly Button _again = Ui.GhostButton(Strings.Check_Again);

#if ANDROID
    private CameraBarcodeReaderView? _reader;
#endif

    public CheckCertificatePage(DeviceHost host) : base(Strings.Check_Title)
    {
        _host = host;
        _again.IsVisible = false;
        _result = new Border
        {
            Padding = Space.Lg,
            StrokeThickness = 1,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = Radius.Card },
            IsVisible = false,
            Content = _answer,
        };

        var check = Ui.PrimaryButton(Strings.Check_Button);
        check.Clicked += (_, _) => Check(_code.Text);
        _code.Completed += (_, _) => Check(_code.Text);
        _again.Clicked += (_, _) => Reset();

        var back = Ui.GhostButton(Strings.Common_GoBack);
        // Unlocked, the bottom bar is the way back; before, this is the only one.
        back.IsVisible = host.UnlockedAs is null;
        back.Clicked += (_, _) => Flow.Advance(host);

        var views = new List<View>
        {
            Heading(Strings.Check_Title),
            Note(Strings.Check_Offline),
            _camera,
        };

#if ANDROID
        _reader = new CameraBarcodeReaderView
        {
            HeightRequest = 280,
            IsDetecting = false,
            Options = new BarcodeReaderOptions
            {
                Formats = BarcodeFormat.QrCode,
                AutoRotate = true,
                Multiple = false,
            },
        };
        _reader.BarcodesDetected += (_, args) =>
        {
            var scanned = args.Results.FirstOrDefault()?.Value;
            if (scanned is null)
            {
                return;
            }

            // One reading per scan: stop before the next frame reports it again.
            _reader.IsDetecting = false;
            MainThread.BeginInvokeOnMainThread(() => Check(scanned));
        };
        views.Add(_reader);
#else
        _camera.Text = Strings.Check_NoCamera;
#endif

        views.AddRange([
            new VerticalStackLayout { Spacing = Space.Sm, Children = { Ui.FieldLabel(Strings.Check_TypeLabel), Ui.Input(_code) } },
            check, Status, _result, _again, back]);
        BuildFor(host, Pages.Section.Home, [.. views]);
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
#if ANDROID
        // Asked here, when the checker opens the screen, not at install: the
        // camera is used for nothing else, and typing always works without it.
        var permission = await Permissions.CheckStatusAsync<Permissions.Camera>();
        if (permission != PermissionStatus.Granted)
        {
            permission = await Permissions.RequestAsync<Permissions.Camera>();
        }

        if (permission == PermissionStatus.Granted && _reader is not null)
        {
            _camera.Text = Strings.Check_Scan;
            _reader.IsDetecting = !_result.IsVisible;
        }
        else
        {
            _camera.Text = Strings.Check_CameraDenied;
            if (_reader is not null)
            {
                _reader.IsVisible = false;
            }
        }
#else
        await Task.CompletedTask;
#endif
    }

    protected override void OnDisappearing()
    {
#if ANDROID
        // Release the camera: the page is replaced, not kept, when the flow moves on.
        if (_reader is not null)
        {
            _reader.IsDetecting = false;
            _reader.Handler?.DisconnectHandler();
        }
#endif
        base.OnDisappearing();
    }

    private void Check(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            Status.Text = Strings.Check_Empty;
            return;
        }

        Status.Text = "";
        var bundle = _host.Session?.Sync.Bundle ?? CachedVerificationBundle.Empty;
        Show(CertificateCheck.Check(bundle, code, DateTime.UtcNow));
    }

    private void Show(CertificateReading reading)
    {
        var (ink, paper) = reading.Verdict switch
        {
            OfflineVerdict.Valid => (Ui.PrimaryStrong, Ui.PrimaryTint),
            OfflineVerdict.Unknown => (Ui.Theme.PendingForeground, Ui.Theme.PendingBackground),
            _ => (Ui.Danger, Ui.Danger.WithAlpha(0.06f)),
        };

        _answer.Clear();
        _answer.Add(new Label { Text = reading.Headline, FontSize = 22, FontFamily = Ui.SemiBold, TextColor = ink });
        _answer.Add(Ui.Body(reading.Explanation));
        _answer.Add(new Label { Text = reading.WhatToDo, FontSize = 16, FontFamily = Ui.SemiBold, TextColor = Ui.Text });

        if (reading.Facts.Count > 0)
        {
            _answer.Add(new Label { Text = Strings.Check_Facts, FontSize = 14, FontFamily = Ui.SemiBold, TextColor = ink });
            foreach (var fact in reading.Facts)
            {
                _answer.Add(new Label
                {
                    FontSize = 15,
                    FontFamily = Ui.Regular,
                    TextColor = Ui.Text,
                    FormattedText = new FormattedString
                    {
                        Spans =
                        {
                            new Span { Text = fact.Label + ": " },
                            new Span { Text = fact.Value, FontFamily = Ui.SemiBold },
                        },
                    },
                });
            }
        }

        if (reading.ListDownloaded is { } downloaded)
        {
            _answer.Add(Ui.Caption(downloaded));
        }

        _result.Stroke = ink;
        _result.BackgroundColor = paper;
        _result.IsVisible = true;
        _again.IsVisible = true;

        MainThread.BeginInvokeOnMainThread(async () =>
        {
            if (Scroller is not null)
            {
                await Scroller.ScrollToAsync(_result, ScrollToPosition.Start, animated: true);
            }
        });
    }

    private void Reset()
    {
        _code.Text = "";
        Status.Text = "";
        _answer.Clear();
        _result.IsVisible = false;
        _again.IsVisible = false;
#if ANDROID
        if (_reader is { IsVisible: true })
        {
            _reader.IsDetecting = true;
        }
#endif
        _ = Scroller?.ScrollToAsync(0, 0, animated: true);
    }
}
