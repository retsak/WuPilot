# Releasing WuPilot

WuPilot publishes self-contained Windows installers through GitHub Actions. A semantic version tag builds, tests, packages, and creates a GitHub Release containing native x64 and ARM64 installers plus SHA-256 checksum files.

## Release artifacts

Each release contains:

- `WuPilot-VERSION-win-x64-setup.exe` for Intel and AMD 64-bit Windows;
- `WuPilot-VERSION-win-arm64-setup.exe` for Windows on Arm;
- one `.sha256` file beside each installer.

The installers use a stable application ID, so a newer release upgrades an existing installation in place. They install WuPilot under the 64-bit Program Files directory, create a Start Menu shortcut, offer an optional desktop shortcut, register an uninstaller, and can launch WuPilot after setup.

Setup also offers an enabled-by-default automatic stable-release check. This writes `HKLM\SOFTWARE\WuPilot\AutomaticUpdateChecks`; it does not install a service or scheduled task. The application checks only while it is running and keeps manual checks available.

The installer requests administrator access because WuPilot itself is elevation-first. It supports unattended deployment with the standard Inno Setup switches:

```powershell
WuPilot-0.3.0-win-x64-setup.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART
```

## Create a release

1. Confirm `main` is green and the working tree is clean.
2. Choose the next `MAJOR.MINOR.PATCH` version.
3. Create and push an annotated tag:

   ```powershell
   git tag -a v0.3.0 -m "WuPilot 0.3.0"
   git push origin v0.3.0
   ```

4. Follow the **Windows release** workflow in GitHub Actions.
5. Download both installers from the resulting GitHub Release.
6. Verify each checksum and perform the Windows validation matrix before announcing the release.

For releases after `v0.2.0`, install the prior updater-capable public release and exercise **About > Check for updates**. For the first updater release, use a lower-version test build to exercise discovery and download, and separately validate a manual in-place upgrade from public `v0.1.0`. Confirm native architecture selection, two-source SHA-256 validation, unsigned/signed publisher messaging, clean app shutdown, and in-place installer replacement.

The workflow rejects tags that do not exactly match `vMAJOR.MINOR.PATCH`. The tag version is passed into the application assembly and installer metadata, so no source file needs a manual version edit for a release.

## Code signing

Unsigned installers work, but Windows identifies their publisher as unknown and Microsoft Defender SmartScreen may show a reputation warning. Public releases should be Authenticode-signed with a trusted code-signing certificate.

Configure these GitHub Actions repository secrets:

- `WINDOWS_SIGNING_CERTIFICATE_BASE64` — base64 text of a password-protected PFX;
- `WINDOWS_SIGNING_CERTIFICATE_PASSWORD` — the PFX password.

To create the base64 value in PowerShell:

```powershell
[Convert]::ToBase64String(
    [IO.File]::ReadAllBytes('C:\secure\wupilot-signing.pfx')
) | Set-Clipboard
```

When both secrets are available, the workflow:

1. signs the x64 and ARM64 `WuPilot.exe` files before packaging;
2. builds the installers;
3. signs and timestamp-verifies both installer executables; and
4. calculates checksums from the final signed files.

The PFX is written only to the temporary GitHub-hosted runner and is not uploaded as an artifact.

## Local installer build

Install Inno Setup 7, then run:

```powershell
./scripts/Build-WuPilotInstaller.ps1 -Platform x64 -Version 0.3.0
./scripts/Build-WuPilotInstaller.ps1 -Platform ARM64 -Version 0.3.0
```

Use `-InnoCompilerPath` when `ISCC.exe` is not in a standard installation path. Use `-SkipAppBuild` only when the matching self-contained publish directory already exists and has been verified.

Local outputs are written to `artifacts/installer`. The script verifies the required WinUI resources and writes a checksum beside the installer.

## Installer implementation

`installer/WuPilot.iss` is parameterized with:

- `AppVersion` — the three-part semantic version;
- `AppArch` — `x64` or `arm64`.

The x64 installer is intentionally blocked on Arm64 devices so users install the native ARM64 build. The ARM64 installer runs only on Arm64 Windows. Both require Windows 10 version 1809 or newer, matching the application's minimum target.

The release workflow pins Inno Setup 7.0.2 by SHA-256 and also verifies that the downloaded compiler installer has a valid Authenticode signature from `Pyrsys B.V.` before execution.
