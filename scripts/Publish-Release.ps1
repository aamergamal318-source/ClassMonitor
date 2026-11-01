param([switch]$Publish)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
Push-Location -LiteralPath $projectRoot
try {
    [xml]$properties = Get-Content -LiteralPath Directory.Build.props
    $version = [string]$properties.Project.PropertyGroup.Version
    if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'A stable major.minor.patch version is required.' }
    if ($Publish -and (git status --porcelain --untracked-files=no)) { throw 'Commit tracked changes before publishing.' }
    dotnet build ClassMonitor.sln -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    dotnet run --project tests/UpdateChecks -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Update checks failed.' }
    foreach ($component in @('Agent', 'Teacher', 'Updater')) {
        $folder = $component.ToLowerInvariant()
        dotnet publish "src/ClassMonitor.$component" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false -o "release/ClassMonitor/$folder"
        if ($LASTEXITCODE -ne 0) { throw "Publishing $component failed." }
    }
    dotnet run --project tests/UpdateChecks -c Release -- --worker release/ClassMonitor/updater/ClassMonitorUpdater.exe
    if ($LASTEXITCODE -ne 0) { throw 'Self-contained updater checks failed.' }
    Copy-Item -LiteralPath LICENSE, README.md, CHANGELOG.md, UPDATE-GUIDE.md, DIAGNOSTICS.md, DEVELOPMENT-PLAN.md, dist/DEPLOY.txt, scripts/Diagnose-Student.cmd -Destination release/ClassMonitor
    $package = Join-Path $projectRoot 'release/ClassMonitor-windows-x64.zip'
    Compress-Archive -LiteralPath release/ClassMonitor -DestinationPath $package -Force
    dotnet run --project tests/UpdateChecks -c Release -- --verify-package $package
    if ($LASTEXITCODE -ne 0) { throw 'Packaged executables failed verification.' }
    Write-Output "Package ready: $package"
    if ($Publish) {
        $targetCommit = git rev-parse HEAD
        if ($LASTEXITCODE -ne 0) { throw 'Cannot identify release commit.' }
        $notes = Join-Path $projectRoot 'release/stable-release-notes.md'
        @"
ClassMonitor $version

Startup checks for the latest stable GitHub release, with in-app download, SHA-256 verification, installation, restart and immediate-failure rollback. Both teacher and student applications support updates. Existing pre-1.1.0 installations need this package once to enable future in-app updates. Includes student and teacher screen diagnostics; the reported black-screen issue on affected computers remains under investigation.

Read UPDATE-GUIDE.md and DEVELOPMENT-PLAN.md in the package.
"@ | Set-Content -LiteralPath $notes -Encoding UTF8
        gh release create "v$version" $package --repo aamergamal318-source/ClassMonitor --target $targetCommit --title "ClassMonitor $version" --notes-file $notes
        if ($LASTEXITCODE -ne 0) { throw 'GitHub release failed.' }
    }
}
finally { Pop-Location }
