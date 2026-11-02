# Build, package, install and recover

## Status and prerequisites
Use an authorized Windows development machine and the SDK selected by the root
`global.json` (.NET 10). Existing MIT history and source projects are retained.
See `ARCHITECTURE.md`, ADRs, `SECURITY.md` and `PRIVACY.md` before deployment.

No cross-vendor external review is claimed. No signing credentials or WiX/Inno
compiler were provided. A custom per-user .NET bootstrapper is supplied instead.

## Local build and verification

```powershell
dotnet build ClassMonitor.sln -c Release
dotnet run --project tests/UpdateChecks -c Release
dotnet run --project tests/LoadProbe -c Release -- --seconds 5
dotnet build installer/ClassMonitor.Installer -c Release
dotnet run --project installer/InstallerChecks -c Release
```

The coordinator must also run enrollment/settings/security integration suites.
Do not run visual fixtures unless explicitly requested. Benchmark measurements
are documented in `BENCHMARK_RESULTS.md` with limitations.

## Package
`scripts/Publish-Release.ps1` runs the checks, builds the compressed self-contained
executables, packages them, embeds that package in `ClassMonitorSetup.exe` and verifies
it. Without `-Publish` nothing leaves this computer. With `-Publish` it refuses any
uncommitted or untracked file and an existing tag, then attaches both files to a stable
release: setup for people to download, and the ZIP for in-app updates to read. A stable
release reaches installed copies, so it needs explicit approval and the release gates in
`PROGRESS.md`. Keep unsigned packages labeled accordingly.

## Explicit per-user installation
After approval, the user runs ClassMonitorSetup, chooses Teacher or Student agent
and confirms installation. Files go under `%LOCALAPPDATA%\Programs\ClassMonitor`
(about 85 MB); setup writes that user's Apps entry and shortcuts. Sign-in start and a
firewall rule happen only when ticked, and installation never grants monitoring
permission. Each Windows account on a shared computer installs and enrolls separately. Administrative enrollment and trust provisioning are separate,
documented in `ENROLLMENT.md`; never substitute setup for enrollment authorization.

The legacy `agent.json` and `teacher.json` shapes remain readable, but their password
is ignored and grants nothing, so every student computer enrolls once after upgrading.
Agent settings now live in `%LOCALAPPDATA%\ClassMonitor`; the old ProgramData file is ignored.
The elevated scheduled task from earlier versions is no longer created; its `--uninstall`
removes it where the account has the rights. Run setup once on v1.0.0 installations,
which have no updater. Back up existing settings through approved administration
procedures; do not publish school secrets or student data.

## Upgrade, uninstall and recovery
Existing in-app updates retain the file path/settings, keep a previous binary and update the installed version shown in Apps.
Bootstrapper upgrades require the recognized component receipt and closed running
files; failed operations restore prior owned files and registry snapshots. A failed
rollback keeps recovery files in `.rollback-*` rather than deleting them.

Uninstall from Windows Installed Apps and confirm the prompt. It removes only
validated component-owned filenames, shortcuts, the sign-in value, previous-version
copies and, with the administrator prompt, the firewall rule. Settings, recordings,
school enrollment and unknown files remain. If blocks from an interrupted lesson remain,
it says so and setup's **Remove leftover restrictions** clears them. Authorized
administrators must handle retention/deletion and school trust revocation deliberately.

Real install and uninstall of the Teacher role were exercised on one development
computer. Clean-machine deployment and a locked running application remain acceptance gates.
Multi-user/system services, MSI/GPO/Intune distribution, administrative ACL
provisioning and signed rollout are not delivered by the per-user bootstrapper.
The setup window shows English and Arabic together; Hebrew is not included there.
