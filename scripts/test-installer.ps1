param(
    [Parameter(Mandatory)][string]$Installer,
    [string]$PublishDirectory,
    [string]$WorkDirectory
)
$ErrorActionPreference='Stop'
$projectRoot=Split-Path -Parent $PSScriptRoot
$Installer=(Resolve-Path -LiteralPath $Installer).Path
if(!$PublishDirectory){$PublishDirectory=Join-Path $projectRoot 'artifacts\win-x64'}
$PublishDirectory=(Resolve-Path -LiteralPath $PublishDirectory).Path
if(!$WorkDirectory){$WorkDirectory=Join-Path $projectRoot 'artifacts\installer-smoke'}
$WorkDirectory=[IO.Path]::GetFullPath($WorkDirectory)
New-Item -ItemType Directory -Force -Path $WorkDirectory | Out-Null
$runRoot=Join-Path $WorkDirectory ([Guid]::NewGuid().ToString('N'))
$target=Join-Path $runRoot 'installed-app'
if(!$target.StartsWith($WorkDirectory+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Test target escaped the test workspace'}
New-Item -ItemType Directory -Path $runRoot | Out-Null
$desktopLink=Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::DesktopDirectory)) 'MadokaChanger.lnk'
$desktopHash=if(Test-Path -LiteralPath $desktopLink){(Get-FileHash -LiteralPath $desktopLink -Algorithm SHA256).Hash}else{$null}
function Run-Hidden([string]$executable,[string[]]$arguments){
    $start=[Diagnostics.ProcessStartInfo]::new($executable)
    $start.UseShellExecute=$false;$start.CreateNoWindow=$true
    foreach($argument in $arguments){$start.ArgumentList.Add($argument)}
    $process=[Diagnostics.Process]::Start($start);$process.WaitForExit()
    if($process.ExitCode -ne 0){throw "Installer verification process exited $($process.ExitCode). Logs: $runRoot"}
}
$installArguments=@('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/SP-','/NOICONS','/TASKS=','/SMOKETEST=1',('/DIR='+$target))
Run-Hidden $Installer ($installArguments+('/LOG='+(Join-Path $runRoot 'install.log')))
$payload=@(Get-ChildItem -LiteralPath $PublishDirectory -Recurse -File | Where-Object Extension -ne '.pdb')
foreach($file in $payload){
    $relative=[IO.Path]::GetRelativePath($PublishDirectory,$file.FullName)
    $installed=Join-Path $target $relative
    if(!(Test-Path -LiteralPath $installed)){throw "Installer omitted $relative"}
    if((Get-FileHash -LiteralPath $installed -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash){throw "Installed payload differs: $relative"}
}
$marker=Join-Path $target 'upgrade-preserved.txt'
Set-Content -LiteralPath $marker -Value 'User-owned files are preserved.' -Encoding utf8
$sentinel=Join-Path $runRoot 'outside-install-data.txt'
Set-Content -LiteralPath $sentinel -Value 'Separate application data is preserved.' -Encoding utf8
Run-Hidden $Installer ($installArguments+('/LOG='+(Join-Path $runRoot 'upgrade.log')))
if(!(Test-Path -LiteralPath $marker)){throw 'Upgrade deleted a user-owned file'}
$uninstaller=(Resolve-Path -LiteralPath (Join-Path $target 'unins000.exe')).Path
if(!$uninstaller.StartsWith($target+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Uninstaller is outside the test installation'}
Run-Hidden $uninstaller @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',('/LOG='+(Join-Path $runRoot 'uninstall.log')))
if(Test-Path -LiteralPath (Join-Path $target 'MadokaChanger.exe')){throw 'Uninstall left the application executable behind'}
if(!(Test-Path -LiteralPath $marker) -or !(Test-Path -LiteralPath $sentinel)){throw 'Uninstall deleted untracked data'}
$currentDesktopHash=if(Test-Path -LiteralPath $desktopLink){(Get-FileHash -LiteralPath $desktopLink -Algorithm SHA256).Hash}else{$null}
if($desktopHash -ne $currentDesktopHash){throw 'Smoke test changed the real desktop shortcut'}
$result=[ordered]@{passed=$true;payloadFilesVerified=$payload.Count;upgradeVerified=$true;uninstallVerified=$true;untrackedDataPreserved=$true;desktopShortcutUnchanged=$true;appLaunched=$false;installerSha256=(Get-FileHash -LiteralPath $Installer -Algorithm SHA256).Hash.ToLowerInvariant()}
$result | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $runRoot 'result.json') -Encoding utf8
$result | ConvertTo-Json
