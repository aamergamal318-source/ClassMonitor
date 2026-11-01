# Build, package, install and recover

## Status and prerequisites
Use an authorized Windows development machine and the SDK selected by the root
`global.json`. The QA/release branch was verified on the existing SDK9.0.203;
the coordinator is migrating to workspace-local .NET10 and must rerun integrated
tests after merging. Existing MIT history, source projects and settings are retained.
See `ARCHITECTURE.md`, ADRs, `SECURITY.md` and `PRIVACY.md` before deployment.

Claude Code is not installed, external Codex CLI is unauthenticated and REA MCP is
not connected in this environment. Native isolated agents performed this work;
no cross-product external review is claimed. No signing credentials or WiX/Inno
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
Build the Teacher, Agent and Updater self-contained Windows x64 executables into
the existing `ClassMonitor/{teacher,agent,updater}` ZIP layout. Preserve LICENSE
and include product/security/privacy/enrollment/update/deployment documentation.
Then embed that ZIP into the bootstrapper using `installer/README.md` commands.
Verify both package entries and `--verify-payload` report. Keep unsigned local
packages labeled internal/prerelease until signing and release gates are met.
Public GitHub publishing requires explicit approval.

## Explicit per-user installation
After approval, the user runs ClassMonitorSetup, chooses Teacher or Student agent
and confirms installation. Files go under `%LOCALAPPDATA%\Programs\ClassMonitor`;
setup writes only that user's Apps registration. Installation does not start a
student agent, create persistence, modify firewall rules or grant monitoring
permission. Administrative enrollment and trust provisioning are separate,
documented in `ENROLLMENT.md`; never substitute setup for enrollment authorization.

The existing legacy `agent.json` and `teacher.json` shapes remain readable, but
insecure/default authentication does not confer enrollment. Follow the explicit
security migration. Back up existing settings through approved administration
procedures; do not publish school secrets or student data.

## Upgrade, uninstall and recovery
Existing in-app updates retain the file path/settings and keep a previous binary.
Bootstrapper upgrades require the recognized component receipt and closed running
files; failed operations restore prior owned files and registry snapshots. A failed
rollback keeps recovery files in `.rollback-*` rather than deleting them.

Uninstall from Windows Installed Apps or `ClassMonitorSetup.exe --uninstall`.
Confirm the dialog. A hidden PowerShell worker waits for setup exit and removes
only validated component-owned filenames. Settings, recordings, school enrollment,
unknown files and previous update backups remain. Authorized administrators must
handle retention/deletion and school trust revocation deliberately.

Installer core and PowerShell file removal were tested only in isolated paths
with registry mocked/disabled. Real HKCU registration/uninstall, locked installed
application behavior and clean-machine deployment remain acceptance gates.
Multi-user/system services, MSI/GPO/Intune distribution, administrative ACL
provisioning and signed rollout are not delivered by the per-user bootstrapper.
The bootstrapper's own English UI is not yet localized; the parent application
localization work does not automatically localize this separate executable.
