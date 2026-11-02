param([switch]$Confirmed, [switch]$SkipRegistration, [int]$WaitForProcessId = 0)
$ErrorActionPreference = 'Stop'
function Show-Notice([string]$Text, [string]$Buttons = 'OK', [string]$Icon = 'Information') {
    Add-Type -AssemblyName System.Windows.Forms
    return [System.Windows.Forms.MessageBox]::Show($Text, 'ClassMonitor uninstall', $Buttons, $Icon)
}
$committed = $false
try {
$installRoot = [IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\')
if ($installRoot.Length -le ([IO.Path]::GetPathRoot($installRoot)).Length + 1) { throw 'Unsafe installation root.' }
$directory = [IO.DirectoryInfo]::new($installRoot)
while ($null -ne $directory) {
    if ($directory.Exists -and (($directory.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)) { throw 'Linked installation root is not supported.' }
    $directory = $directory.Parent
}
$manifestPath = Join-Path $installRoot 'install-manifest.json'
$receipt = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($receipt.Product -ne 'ClassMonitor.ManagedDesktop' -or $receipt.Schema -ne 1 -or $receipt.Component -notin @(0,1)) { throw 'Unrecognized installation receipt.' }
$component = if ($receipt.Component -eq 0) { 'Teacher' } else { 'Agent' }
$application = "ClassMonitor$component.exe"
if (-not $Confirmed) {
    $answer = Show-Notice "Remove ClassMonitor $component? User settings, school enrollment and recordings will be retained." 'YesNo' 'Question'
    if ("$answer" -ne 'Yes') { exit 0 }
}
$allowed = @($application,'ClassMonitorUpdater.exe','ClassMonitorSetup.exe','Uninstall.ps1','LICENSE','README.md','UPDATE-GUIDE.md','DIAGNOSTICS.md')
$names = @($receipt.Files.PSObject.Properties.Name)
if ($names.Count -eq 0 -or $application -notin $names) { throw 'Missing owned application.' }
foreach ($name in $names) { if ($name -notin $allowed -or [IO.Path]::GetFileName($name) -cne $name) { throw 'Unsafe owned file name.' } }
if ($WaitForProcessId -gt 0) {
    $parent = Get-Process -Id $WaitForProcessId -ErrorAction SilentlyContinue
    if ($null -ne $parent -and -not $parent.WaitForExit(60000)) { throw 'Installer did not close; no files were removed.' }
}
$names += 'install-manifest.json'
foreach ($name in $names) {
    $path = Join-Path $installRoot $name
    if (Test-Path -LiteralPath $path) { $probe = [IO.File]::Open($path,[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None); $probe.Dispose() }
}
$backup = Join-Path $installRoot ('.uninstall-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($backup) | Out-Null
$moved = @()
$netsh = Join-Path ([Environment]::GetFolderPath('System')) 'netsh.exe'
$registrationPath = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\ClassMonitor-$component"
try {
    foreach ($name in $names) {
        $path = Join-Path $installRoot $name
        if (Test-Path -LiteralPath $path) { Move-Item -LiteralPath $path -Destination (Join-Path $backup $name); $moved += $name }
    }
    if (-not $SkipRegistration -and (Test-Path -LiteralPath $registrationPath)) { Remove-Item -LiteralPath $registrationPath }
}
catch {
    foreach ($name in $moved) { Move-Item -LiteralPath (Join-Path $backup $name) -Destination (Join-Path $installRoot $name) }
    throw
}
$committed = $true
foreach ($name in $moved) { Remove-Item -LiteralPath (Join-Path $backup $name) }
Remove-Item -LiteralPath $backup
# Previous-version copies kept by in-app updates belong to this application.
Get-ChildItem -LiteralPath $installRoot -Filter "$application.backup-*" -File -Force | Remove-Item -Force
if (@(Get-ChildItem -LiteralPath $installRoot -Force).Count -eq 0) { Remove-Item -LiteralPath $installRoot }
if (-not $SkipRegistration) {
    $label = if ($component -eq 'Teacher') { 'ClassMonitor Teacher' } else { 'ClassMonitor Student' }
    foreach ($folder in @([Environment]::GetFolderPath('Programs'), [Environment]::GetFolderPath('DesktopDirectory'))) {
        $link = Join-Path $folder "$label.lnk"
        if (Test-Path -LiteralPath $link) { Remove-Item -LiteralPath $link }
    }
    if ($component -eq 'Agent') { Remove-ItemProperty -LiteralPath 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'ClassMonitorAgent' -ErrorAction SilentlyContinue }
    $rule = "$label ($env:USERNAME)"
    & $netsh advfirewall firewall show rule "name=$rule" | Out-Null
    if ($LASTEXITCODE -eq 0) {
        try { Start-Process $netsh -ArgumentList @('advfirewall','firewall','delete','rule',"`"name=$rule`"") -Verb RunAs -WindowStyle Hidden -Wait }
        catch { Show-Notice "The firewall rule '$rule' remains because the administrator prompt was declined. It allows only the removed program." | Out-Null }
    }
    # Blocks left by a crash need administrator rights to remove, so point to the tool instead of asking again.
    $hosts = Join-Path ([Environment]::GetFolderPath('System')) 'drivers\etc\hosts'
    $leftHosts = (Test-Path -LiteralPath $hosts) -and (Select-String -LiteralPath $hosts -Pattern '^# ClassMonitor:[a-f0-9]{32}:Begin' -Quiet)
    & $netsh advfirewall firewall show rule name=ClassMonitor-Session-TCP | Out-Null
    if ($leftHosts -or $LASTEXITCODE -eq 0) { Show-Notice 'Website or internet blocks from an interrupted lesson remain on this computer. An administrator can remove them with ClassMonitorSetup.exe, Remove leftover restrictions.' | Out-Null }
}
# User settings, recordings, school enrollment and unrelated files are deliberately retained.
}
catch {
    if ($Confirmed) { throw }
    $summary = if ($committed) { 'ClassMonitor was removed, but cleanup did not finish.' } else { 'Nothing was removed. Close ClassMonitor and try again.' }
    Show-Notice "$summary`n`n$($_.Exception.Message)" 'OK' 'Warning' | Out-Null
    exit 1
}
