# Local per-user installer

This original WinForms bootstrapper is the one file people download. It installs **one
chosen component** into `%LOCALAPPDATA%\Programs\ClassMonitor\Teacher` or `...\Agent`,
for the current Windows account, after an explicit checkbox and Install click. For a
student computer it requires a computer password, of which only a slow salted hash is
stored; the teacher uses that password to add the computer. It adds Start menu and
optional desktop shortcuts. Sign-in start and a firewall rule are visible choices, ticked
by default for a student computer because the teacher cannot reach it otherwise, and the
firewall rule shows the Windows administrator prompt. It never starts monitoring: that
needs the teacher to add the computer and start a lesson.

Its administrator tools run elevated from this copy of setup, never from the student's
writable program folder: import an enrollment file (needed for power and program commands) after comparing fingerprints, and
remove website and internet blocks left by an interrupted lesson. Run them from a trusted
copy that students cannot change.

Build after the product ZIP has been created:

```powershell
dotnet publish installer/ClassMonitor.Installer -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false -p:PayloadPath="C:\absolute\ClassMonitor-windows-x64.zip" -o release/installer
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

Production setup registers a per-user Apps entry that runs the installed `Uninstall.ps1`.
It asks for confirmation and removes only names owned by the component's validated receipt,
its shortcuts, its sign-in start value, its firewall rule (with the administrator prompt)
and previous-version copies kept by updates. Close running ClassMonitor processes before
upgrading/removing. It preserves settings, recordings, enrollment and unrelated files. It rejects filesystem roots and
linked/junction directories. Failed rollback retains recovery copies rather than
deleting them. It is not an MSI, school-wide service installer, GPO/Intune package,
ACL provisioning tool or production certification.
