param([switch]$Confirmed, [switch]$SkipRegistration, [int]$WaitForProcessId = 0)
$ErrorActionPreference = 'Stop'
if (-not $Confirmed) { throw 'Uninstall requires an explicit confirmation from ClassMonitorSetup.' }
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
$registrationPath = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\ClassMonitor-$component"
$registrationBefore = $null
if (-not $SkipRegistration -and (Test-Path -LiteralPath $registrationPath)) { $registrationBefore = Get-ItemProperty -LiteralPath $registrationPath }
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
foreach ($name in $moved) { Remove-Item -LiteralPath (Join-Path $backup $name) }
Remove-Item -LiteralPath $backup
if (@(Get-ChildItem -LiteralPath $installRoot -Force).Count -eq 0) { Remove-Item -LiteralPath $installRoot }
# User settings, recordings, school enrollment and unrelated files are deliberately retained.
