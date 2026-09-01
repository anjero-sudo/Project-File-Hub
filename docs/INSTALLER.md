# Windows installer

Project File Hub publishes two Windows x64 artifacts for each release:

- `ProjectFileHub-Setup-X.Y.Z-win-x64.exe` for a registered current-user installation;
- `ProjectFileHub-X.Y.Z-win-x64.zip` for portable use.

Both artifacts have adjacent `.sha256` files. The installer is currently unsigned; signing is a separate release gate and must not be inferred from a successful build or install test.

## Installation model

- Technology: Inno Setup 6.
- Scope: current Windows user; no administrator prompt is required.
- Default root: `%LOCALAPPDATA%\Programs\ProjectFileHub`.
- Replaceable payload: `<install root>\app`.
- Uninstaller: `<install root>\uninstall\unins000.exe` plus the standard Windows “Installed apps” entry.
- Stable AppId: `{7370CC21-B0E6-48EF-92D4-B25D513BD1CC}`.
- Shortcuts: Start Menu plus an optional desktop shortcut.

The payload has its own `app` subdirectory so an upgrade can remove only installer-owned application files before writing the new self-contained runtime. This prevents stale DLL/XBF/PRI files without using a wildcard against the user-selected install root. Legacy manually deployed version directories, if present, are not deleted automatically.

Application state remains outside the installation root and is deliberately preserved during upgrades and uninstallation:

- `%LOCALAPPDATA%\ProjectFileHub`
- `%APPDATA%\Anjero\ProjectFileHub`

If the user already enabled the application’s Windows-startup setting, Setup migrates the existing `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\ProjectFileHub` value to the newly installed executable. Uninstall removes that value only when it still targets the installation being removed.

## Local build

Install Inno Setup 6, or point `INNO_SETUP_COMPILER` to `ISCC.exe`. Then run:

```powershell
.\eng\run-core-tests.ps1
.\eng\build.ps1 -Configuration Release
.\eng\package-release.ps1 -Configuration Release -Runtime win-x64
.\eng\build-installer.ps1 -Runtime win-x64
.\eng\test-installer.ps1 -Runtime win-x64
```

`build-installer.ps1` verifies the application version and required WinUI resources before compiling the installer. It writes the installer and checksum under `artifacts\release`.

`test-installer.ps1` refuses to overwrite an already registered Project File Hub installation. In a clean profile it verifies:

1. silent current-user installation;
2. required EXE, DLL, XBF, and PRI resources;
3. version, publisher, install location, and uninstall registration;
4. same-AppId in-place upgrade and removal of a stale payload probe;
5. installed executable startup and responsive main window;
6. shortcut creation/removal;
7. silent uninstall;
8. preservation of an application-data probe outside the install root.

For a developer machine that already has manual shortcuts, use `-SkipShortcuts`. Use `-SkipLaunch` when a local profile must not be opened by the isolated test executable. GitHub Actions runs the complete clean-profile path.

## GitHub release gate

The tag must exactly match `Directory.Build.props`. The release workflow performs the core tests, Release build, portable-package startup smoke, installer compilation, installer install/upgrade/start/uninstall smoke, and only then creates the GitHub Release with all four artifacts.

Code signing is intentionally not simulated. When an organization-controlled certificate becomes available, both the application and Setup/uninstaller must be signed and the workflow must make a valid Authenticode status a required gate.
