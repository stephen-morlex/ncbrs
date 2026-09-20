# NCBRS.Client.App — Tier-1 MAUI shell (scaffold)

The .NET MAUI application health workers run on the tablet (WS-B, B1 decision:
MAUI). It is the **shell only**: screens, the encrypted local store, PIN entry
and certificate printing. All registration logic lives in
`NCBRS.Client.Core`, which this project references — the shell holds none of its
own, so nothing on the device can drift from the centre.

## Status: scaffold

This is the NCBRS-specific skeleton — the project file, the DI entry point
(`MauiProgram`), the app root (`App`), and a `MainPage` that drives
`FacilityClient` to register a birth. It is **not yet buildable as-is** because
the MAUI platform heads and resource assets are template boilerplate that is
not committed here (see below).

**It is deliberately excluded from `NCBRS.slnx`.** The main solution is built by
CI, which has no MAUI workload; a MAUI project in that solution would fail every
build. Build this one on its own, in a device-tooling environment.

## Completing and building it

Requires a machine with the MAUI workload and, for Android, the Android SDK —
none of which the central-tier CI/dev box has.

```bash
dotnet workload install maui
```

Then add the boilerplate the template generates (pure MAUI scaffolding, no NCBRS
content): scaffold a throwaway app and copy its folders in —

```bash
dotnet new maui -n _tmp
cp -r _tmp/Platforms client/NCBRS.Client.App/Platforms      # MainActivity, WinUI head, entitlements
cp -r _tmp/Resources  client/NCBRS.Client.App/Resources     # AppIcon, SplashScreen, Fonts, Images
rm -rf _tmp
```

(Keep this folder's `Resources/Styles/Colors.xaml` and `Styles.xaml`.) Then:

```bash
dotnet build client/NCBRS.Client.App -f net10.0-android
```

Align the `TargetFrameworks` and `SupportedOSPlatformVersion` in the `.csproj`
with the workload version if they differ from what `dotnet new maui` produced.

## How it wires to the core

`MauiProgram.CreateMauiApp` is where the shell composes the client-core after
the registrar unlocks: restore the device key, BRN block + cursor and outbox
from the encrypted store, build a `FacilityClient`, and add an HTTP client for
`BuildSignedUpload()` and a printer for provisional slips. `MainPage` shows the
registration call end to end. See [`../INTEGRATION.md`](../INTEGRATION.md) for
the full lifecycle and what the shell must persist.
