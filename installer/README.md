# Local per-user installer

This original WinForms bootstrapper installs **one chosen component** into
`%LOCALAPPDATA%\Programs\ClassMonitor\Teacher` or `...\Agent`. It requires an
explicit checkbox and Install click. It does not launch the installed agent,
enroll a device, change firewall rules, register a service/scheduled task or enable
startup. School enrollment and administrator-controlled trust remain separate.

Build after the product ZIP has been created:

```powershell
dotnet publish installer/ClassMonitor.Installer -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false -p:PayloadPath="C:\absolute\ClassMonitor-windows-x64.zip" -o release/installer
```

The ZIP must have exact entries:

```text
ClassMonitor/teacher/ClassMonitorTeacher.exe
ClassMonitor/agent/ClassMonitorAgent.exe
ClassMonitor/updater/ClassMonitorUpdater.exe
ClassMonitor/LICENSE
```

README, UPDATE-GUIDE and DIAGNOSTICS are optional copied documentation. The chosen
application's embedded Windows file version must match the installer's version.
The package is embedded at build time; there is no internet download in setup.
Without `PayloadPath`, the development bootstrapper builds but disables Install.
Do not distribute a payload-free binary as a functioning installer.

Headless payload-presence verification (does not install or open a window):

```powershell
release/installer/ClassMonitorSetup.exe --verify-payload release/installer/payload-report.json
```

The report confirms resource presence/size, not publisher authenticity. Binaries
are unsigned unless an authorized release pipeline signs them separately. No
signing credentials were supplied in this environment.

Core tests (never perform a real installation or registry write):

```powershell
dotnet build ClassMonitor.sln -c Release
dotnet build installer/ClassMonitor.Installer -c Release
dotnet run --project installer/InstallerChecks -c Release
```

The tests mock HKCU registration and use isolated `installer/InstallerChecks/artifacts`
directories. They exercise Teacher/Agent install-upgrade-uninstall, rollback,
locked files, exact ZIP extraction, invalid versions, unsafe receipts and the
PowerShell uninstall worker with registry operations disabled.

Production setup registers a per-user Apps entry. Uninstall prompts explicitly,
starts a hidden PowerShell worker, waits for setup to exit and removes only names
owned by the component's validated receipt. Close running ClassMonitor processes
before upgrading/removing. It preserves settings, recordings, enrollment,
unrelated files and previous update backups. It rejects filesystem roots and
linked/junction directories. Failed rollback retains recovery copies rather than
deleting them. It is not an MSI, school-wide service installer, GPO/Intune package,
ACL provisioning tool or production certification.
