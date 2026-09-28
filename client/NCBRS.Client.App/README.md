# NCBRS.Client.App — Tier-1 MAUI shell

The .NET MAUI application health workers run on the tablet (WS-B, B1 decision:
MAUI). It is the **shell only**: screens, the encrypted local store, PIN entry
and certificate printing. All registration logic lives in
`NCBRS.Client.Core`, which this project references — the shell holds none of its
own, so nothing on the device can drift from the centre.

## Status

It builds and runs for Android (the tablet) and Windows (a desktop dev loop),
and the device path works end to end. The screen shown always follows the
tablet's stage (`Pages/Flow.cs`), never a remembered navigation stack:

1. **Handover.** A district officer signs in, picks the facility, confirms it
   by name and enrols the tablet. The enrolment is checked against the
   registry, and the officer's session is ended afterwards.
2. **Registrar sign-in.** Done once, in the system browser. The account is
   checked against the tablet's facility straight away.
3. **Setting up.** The tablet draws its first block of numbers, fetches the
   certificate checks and the staff PINs, and asks for the registrar's PIN if
   nobody at the facility has one yet.
4. **Unlock.** The registrar picks their name and enters their PIN, with no
   signal needed.
5. **Register and sync.** The registration form is a placeholder until the B4
   first cut.

Anything that goes wrong has a way out on the tablet: **Sign in as someone
else**, or **Wrong facility: hand over again**, which needs the officer and
revokes the tablet at the registry first.

All the app state lives in `Services/DeviceHost.cs`, which saves after every
act; every rule lives in the core. See `client/INTEGRATION.md`.

Signing in on the emulator: `adb reverse` both ports (below). **Keycloak asks
for the account every time** (`prompt=login`), because the tablet's browser is
shared.

**Heads:** Android and Windows only. The fleet is Android; the Windows head
exists so a screen can be worked on without an emulator. iOS and Mac Catalyst
are not targets.

**It is deliberately excluded from `NCBRS.slnx`.** Most CI jobs build the
solution without the MAUI workload. CI's **Build (MAUI Android)** job builds this
project on its own.

## Building

Needs the MAUI workloads, plus the Android SDK and a JDK for the Android head
(Android Studio installs both; so does the CI runner image):

```bash
dotnet workload install maui-android maui-windows
```

```bash
dotnet build client/NCBRS.Client.App -f net10.0-android
```

```bash
dotnet build client/NCBRS.Client.App -f net10.0-windows10.0.19041.0
```

## Running on the emulator

Start an emulator (`emulator -list-avds` names them), then build, install and
launch in one step:

```bash
dotnet build client/NCBRS.Client.App -f net10.0-android -t:Run
```

**Reaching the dev stack.** Point the emulator's `localhost` at the host rather
than using `10.0.2.2`:

```bash
adb reverse tcp:5259 tcp:5259
```

```bash
adb reverse tcp:8080 tcp:8080
```

Keycloak puts the address it was reached on into each token's issuer, and the
API trusts `http://localhost:8080/realms/ncbrs`. A token fetched through
`10.0.2.2` names a different issuer and the API refuses every request.

Cleartext HTTP is allowed to `localhost` in **Debug builds only**
(`Platforms/Android/Resources/xml/network_security_config_debug.xml`, applied
from `MainApplication.cs`). A Release build has no such config, so Android
refuses cleartext entirely.

## Things set on purpose

- **No backup and no device-to-device transfer** (`allowBackup="false"` and
  `data_extraction_rules.xml`). The store holds this device's identity and its
  unsynced births. Restored onto a second tablet, it would be two devices
  claiming one identity and one BRN cursor, re-handing numbers already printed
  on slips.
- **Unpackaged Windows build** (`WindowsPackageType=None`): it is a dev loop,
  not a distribution.
