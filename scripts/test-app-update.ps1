param(
    [string]$OldPublishDirectory,
    [string]$OldSetupPath,
    [string]$NewSetupPath,
    [ValidatePattern('^\d+\.\d+\.\d+$')][string]$ExpectedOldVersion = '1.2.0',
    [ValidatePattern('^\d+\.\d+\.\d+$')][string]$ExpectedNewVersion = '1.2.1',
    [string]$ExpectedOldCoreSha256,
    [string]$WorkspaceRoot,
    [string]$DotnetPath,
    [switch]$CheckOnly,
    [switch]$SeedFromPublish
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
if (!$OldPublishDirectory) { $OldPublishDirectory = Join-Path $repo "artifacts\releases\v$ExpectedOldVersion\publish" }
if (!$OldSetupPath) { $OldSetupPath = Join-Path $repo "artifacts\releases\v$ExpectedOldVersion\MadokaChanger-Setup-$ExpectedOldVersion.exe" }
if (!$NewSetupPath) { $NewSetupPath = Join-Path $repo "artifacts\releases\v$ExpectedNewVersion\MadokaChanger-Setup-$ExpectedNewVersion.exe" }
if (!$WorkspaceRoot) { $WorkspaceRoot = Join-Path $repo ('artifacts\update-verification\' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff')) }
if (!$DotnetPath) {
    $dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($dotnetCommand) { $DotnetPath = $dotnetCommand.Source }
    else { throw 'Pass -DotnetPath or put the .NET SDK dotnet.exe on PATH.' }
}

$OldPublishDirectory = (Resolve-Path -LiteralPath $OldPublishDirectory).Path
if (!$SeedFromPublish -and !$CheckOnly) { $OldSetupPath = (Resolve-Path -LiteralPath $OldSetupPath).Path }
$sourceCore = Join-Path $OldPublishDirectory 'MadokaChanger.Core.dll'
$sourceCoreHash = (Get-FileHash -LiteralPath $sourceCore -Algorithm SHA256).Hash
if (!$ExpectedOldCoreSha256) { $ExpectedOldCoreSha256 = $sourceCoreHash }
$verifierProject = Join-Path $repo 'tools\MadokaChanger.UpdateVerify\MadokaChanger.UpdateVerify.csproj'
$verifierDll = Join-Path $repo 'tools\MadokaChanger.UpdateVerify\bin\Release\net10.0-windows\MadokaChanger.UpdateVerify.dll'
if (!(Test-Path -LiteralPath $NewSetupPath) -and !$CheckOnly) { throw "New setup not found: $NewSetupPath" }
if (Test-Path -LiteralPath $WorkspaceRoot) { throw "Workspace already exists; choose a fresh -WorkspaceRoot: $WorkspaceRoot" }
New-Item -ItemType Directory -Path $WorkspaceRoot | Out-Null
$WorkspaceRoot = (Resolve-Path -LiteralPath $WorkspaceRoot).Path
$installDirectory = Join-Path $WorkspaceRoot 'installed-app'
$dataDirectory = Join-Path $WorkspaceRoot 'isolated-user-data'
New-Item -ItemType Directory -Path $dataDirectory | Out-Null

& $DotnetPath build $verifierProject -c Release
if ($LASTEXITCODE -ne 0) { throw 'Could not build the headless old-updater harness.' }
if (!(Test-Path -LiteralPath $verifierDll)) { throw "Verifier assembly not found: $verifierDll" }

function Invoke-OldUpdater([string]$mode) {
    $expectedLatestArgument = if ($CheckOnly) { 'latest' } else { $ExpectedNewVersion }
    $arguments = @($verifierDll, $OldPublishDirectory, $dataDirectory, $ExpectedOldVersion, $expectedLatestArgument, $mode)
    if ($ExpectedOldCoreSha256) { $arguments += $ExpectedOldCoreSha256 }
    & $DotnetPath @arguments
    if ($LASTEXITCODE -ne 0) { throw "Old v$ExpectedOldVersion updater $mode failed with exit code $LASTEXITCODE." }
}

if ($CheckOnly) {
    Invoke-OldUpdater 'check-only'
    Write-Output "Check-only verification complete. Results: $(Join-Path $dataDirectory 'update-verification.json')"
    return
}

# /SMOKETEST disables shortcuts and uninstall registration. /DIR confines the actual
# installer payload to this fresh workspace. /SKIPLTK prevents any dependency step.
$silentArgs = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SMOKETEST=1', '/SKIPLTK=1', "/DIR=`"$installDirectory`"")
if ($SeedFromPublish) {
    New-Item -ItemType Directory -Path $installDirectory | Out-Null
    Get-ChildItem -LiteralPath $OldPublishDirectory -Force | Copy-Item -Destination $installDirectory -Recurse
} else {
    $oldInstall = Start-Process -FilePath $OldSetupPath -ArgumentList ($silentArgs + "/LOG=`"$(Join-Path $WorkspaceRoot 'old-install.log')`"") -WindowStyle Hidden -Wait -PassThru
    if ($oldInstall.ExitCode -ne 0) { throw "Old setup exited with code $($oldInstall.ExitCode)." }
}
$oldExe = Join-Path $installDirectory 'MadokaChanger.exe'
if (!(Test-Path -LiteralPath $oldExe)) { throw 'Old setup did not install MadokaChanger.exe in the isolated directory.' }
$installedOld = [Diagnostics.FileVersionInfo]::GetVersionInfo($oldExe).ProductVersion
if (!$installedOld.StartsWith($ExpectedOldVersion, [StringComparison]::Ordinal)) { throw "Expected installed v$ExpectedOldVersion, got '$installedOld'." }
$installedOldCoreHash = (Get-FileHash -LiteralPath (Join-Path $installDirectory 'MadokaChanger.Core.dll') -Algorithm SHA256).Hash
if ($installedOldCoreHash -ne $sourceCoreHash) { throw 'Installed old updater differs from the saved payload under test.' }

# Representative user data stays in a dedicated root passed only to the old updater.
# Its settings and library marker must remain byte-for-byte intact through setup.
$settings = Join-Path $dataDirectory 'settings.json'
$library = Join-Path $dataDirectory 'library'
New-Item -ItemType Directory -Path $library -Force | Out-Null
'{"settings":{"libraryFolder":"library","language":"default","reducedMotion":true},"favorites":[103],"selections":{"103":{"championId":103,"skinId":103001,"chromaId":null,"form":null}},"customSkins":[]}' | Set-Content -LiteralPath $settings -Encoding utf8
'preserve-this-library-entry' | Set-Content -LiteralPath (Join-Path $library 'fixture.txt') -Encoding utf8
$beforeSettings = (Get-FileHash -LiteralPath $settings -Algorithm SHA256).Hash
$beforeLibrary = (Get-FileHash -LiteralPath (Join-Path $library 'fixture.txt') -Algorithm SHA256).Hash

Invoke-OldUpdater 'download'
$verification = Get-Content -LiteralPath (Join-Path $dataDirectory 'update-verification.json') -Raw | ConvertFrom-Json
if (!$verification.updateAvailable -or $verification.githubLatest -ne $ExpectedNewVersion) { throw 'Old updater did not discover the expected newer GitHub release.' }
if ($verification.downloadedSha256 -ne $verification.githubSha256) { throw 'Downloaded installer SHA-256 differs from GitHub release metadata.' }

$newSetup = (Resolve-Path -LiteralPath $NewSetupPath).Path
$newHash = (Get-FileHash -LiteralPath $newSetup -Algorithm SHA256).Hash.ToLowerInvariant()
if ($newHash -ne $verification.downloadedSha256) { throw 'The downloaded official setup differs from the locally supplied setup.' }
$newInstall = Start-Process -FilePath $verification.downloadedSetup -ArgumentList ($silentArgs + "/LOG=`"$(Join-Path $WorkspaceRoot 'new-install.log')`"") -WindowStyle Hidden -Wait -PassThru
if ($newInstall.ExitCode -ne 0) { throw "New setup exited with code $($newInstall.ExitCode)." }

$newExe = Join-Path $installDirectory 'MadokaChanger.exe'
$newCore = Join-Path $installDirectory 'MadokaChanger.Core.dll'
$newBackend = Join-Path $installDirectory 'backend\madokachanger-ltk.exe'
foreach ($requiredFile in @($newExe, $newCore, $newBackend)) {
    if (!(Test-Path -LiteralPath $requiredFile)) { throw "Updated installation is missing $requiredFile" }
}
$installedNew = [Diagnostics.FileVersionInfo]::GetVersionInfo($newExe).ProductVersion
if (!$installedNew.StartsWith($ExpectedNewVersion, [StringComparison]::Ordinal)) { throw "Expected installed v$ExpectedNewVersion, got '$installedNew'." }
if ((Get-FileHash -LiteralPath $settings -Algorithm SHA256).Hash -ne $beforeSettings) { throw 'Settings fixture changed during the isolated upgrade.' }
if ((Get-FileHash -LiteralPath (Join-Path $library 'fixture.txt') -Algorithm SHA256).Hash -ne $beforeLibrary) { throw 'Library fixture changed during the isolated upgrade.' }
if ((Get-FileHash -LiteralPath $sourceCore -Algorithm SHA256).Hash -ne $sourceCoreHash) { throw 'The saved old updater was modified by the test.' }

$result = [ordered]@{
    result = 'PASS'
    oldUpdaterVersion = $verification.oldUpdater
    oldCoreSha256 = $sourceCoreHash.ToLowerInvariant()
    seededFromPublish = [bool]$SeedFromPublish
    githubLatestVersion = $verification.githubLatest
    oldUpdaterDiscoveredUpdate = $verification.updateAvailable
    downloadedInstallerSha256 = $verification.downloadedSha256
    installedVersionAfterUpgrade = $installedNew
    isolatedInstallDirectory = $installDirectory
    isolatedDataDirectory = $dataDirectory
    settingsPreserved = $true
    libraryFixturePreserved = $true
    updateCommandExecuted = $false
    appRestarted = $false
    setupFlags = $silentArgs
}
$resultPath = Join-Path $WorkspaceRoot 'full-update-verification.json'
$result | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $resultPath -Encoding utf8
$result | ConvertTo-Json -Depth 5
Write-Output "Full verification report: $resultPath"
