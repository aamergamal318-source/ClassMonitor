param([switch]$Publish)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
Push-Location -LiteralPath $projectRoot
try {
    [xml]$properties = Get-Content -LiteralPath Directory.Build.props
    $version = [string]$properties.Project.PropertyGroup.Version
    if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'A stable major.minor.patch version is required.' }
    # Untracked sources are compiled too, so anything not committed or ignored would ship without being in the tag.
    if ($Publish -and (git status --porcelain)) { throw 'Commit or ignore every change before publishing.' }
    if ($Publish -and (git ls-remote --tags origin "refs/tags/v$version")) { throw "Tag v$version already exists." }
    if (Test-Path -LiteralPath release) { Remove-Item -LiteralPath release -Recurse -Force }
    $singleFile = @('-c','Release','-r','win-x64','--self-contained','true','-p:PublishSingleFile=true','-p:IncludeNativeLibrariesForSelfExtract=true','-p:EnableCompressionInSingleFile=true','-p:DebugType=None','-p:DebugSymbols=false')
    dotnet build ClassMonitor.sln -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    foreach ($checks in @('tests/UpdateChecks', 'tests/SecurityChecks', 'tests/IntegrationChecks', 'tests/ConsoleChecks')) {
        dotnet run --project $checks -c Release
        if ($LASTEXITCODE -ne 0) { throw "$checks failed." }
    }
    foreach ($component in @('Agent', 'Teacher', 'Updater')) {
        $folder = $component.ToLowerInvariant()
        dotnet publish "src/ClassMonitor.$component" @singleFile -o "release/ClassMonitor/$folder"
        if ($LASTEXITCODE -ne 0) { throw "Publishing $component failed." }
    }
    dotnet run --project tests/UpdateChecks -c Release -- --worker release/ClassMonitor/updater/ClassMonitorUpdater.exe
    if ($LASTEXITCODE -ne 0) { throw 'Self-contained updater checks failed.' }
    Copy-Item -LiteralPath LICENSE, README.md, CHANGELOG.md, UPDATE-GUIDE.md, DIAGNOSTICS.md, DEVELOPMENT-PLAN.md, dist/DEPLOY.txt, scripts/Diagnose-Student.cmd -Destination release/ClassMonitor
    $package = Join-Path $projectRoot 'release/ClassMonitor-windows-x64.zip'
    Compress-Archive -LiteralPath release/ClassMonitor -DestinationPath $package -Force
    dotnet run --project tests/UpdateChecks -c Release -- --verify-package $package
    if ($LASTEXITCODE -ne 0) { throw 'Packaged executables failed verification.' }
    # The single installer people download. The ZIP stays attached too: in-app update reads it from the release.
    dotnet run --project installer/InstallerChecks -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Installer checks failed.' }
    dotnet publish installer/ClassMonitor.Installer @singleFile "-p:PayloadPath=$package" -o release/installer
    if ($LASTEXITCODE -ne 0) { throw 'Publishing setup failed.' }
    $setup = Join-Path $projectRoot 'release/installer/ClassMonitorSetup.exe'
    $report = Join-Path $projectRoot 'release/installer/payload-report.json'
    $check = Start-Process -FilePath $setup -ArgumentList @('--verify-payload', "`"$report`"") -PassThru -Wait
    if ($check.ExitCode -ne 0) { throw 'Setup has no embedded package.' }
    $payload = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
    if (-not $payload.PayloadPresent -or $payload.Bytes -ne (Get-Item -LiteralPath $package).Length) { throw 'Setup does not embed this package.' }
    Write-Output "Setup ready: $setup"
    Write-Output "Package ready: $package"
    if ($Publish) {
        $targetCommit = git rev-parse HEAD
        if ($LASTEXITCODE -ne 0) { throw 'Cannot identify release commit.' }
        $notes = Join-Path $projectRoot 'release/stable-release-notes.md'
        @"
ClassMonitor $version

Download ClassMonitorSetup.exe. It installs the teacher console or the student agent on any computer; later updates come from inside the program. ClassMonitor-windows-x64.zip is read by the in-app updater, so it does not need to be downloaded.

Read UPDATE-GUIDE.md and DEVELOPMENT-PLAN.md in the package.
"@ | Set-Content -LiteralPath $notes -Encoding UTF8
        gh release create "v$version" $setup $package --repo aamergamal318-source/ClassMonitor --target $targetCommit --title "ClassMonitor $version" --notes-file $notes
        if ($LASTEXITCODE -ne 0) { throw 'GitHub release failed.' }
    }
}
finally { Pop-Location }
