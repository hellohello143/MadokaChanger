param(
    [ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version='1.2.1',
    [string]$CompilerPath=$env:INNO_SETUP_COMPILER,
    [string]$PublishDirectory,
    [string]$OutputDirectory
)
$ErrorActionPreference='Stop'
$projectRoot=Split-Path -Parent $PSScriptRoot
if(!$PublishDirectory){$PublishDirectory=Join-Path $projectRoot 'artifacts\win-x64'}
if(!$OutputDirectory){$OutputDirectory=Join-Path $projectRoot 'artifacts\installer'}
$PublishDirectory=(Resolve-Path -LiteralPath $PublishDirectory).Path
$OutputDirectory=[IO.Path]::GetFullPath($OutputDirectory)
if(!$CompilerPath){
    $command=Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if($command){$CompilerPath=$command.Source}
    else {
        $candidates=@(foreach($major in '7','6'){foreach($base in @((Join-Path $env:LOCALAPPDATA 'Programs'),$env:ProgramFiles,${env:ProgramFiles(x86)})){if($base){Join-Path $base "Inno Setup $major\ISCC.exe"}}})
        $CompilerPath=$candidates | Where-Object {Test-Path -LiteralPath $_} | Select-Object -First 1
    }
}
if(!$CompilerPath -or !(Test-Path -LiteralPath $CompilerPath)){throw 'Install Inno Setup 6.7.3 or later (7.x works too), or pass -CompilerPath pointing to ISCC.exe.'}
foreach($file in @('MadokaChanger.exe','backend\madokachanger-ltk.exe','backend\ltk_patcher_host.exe','backend\ltk_patcher_dll.dll','licenses\LTK-PATCHER-LICENSE.md')){
    if(!(Test-Path -LiteralPath (Join-Path $PublishDirectory $file))){throw "Published app is incomplete: $file. Run scripts/publish.ps1 first."}
}
$manifest=Get-Content -LiteralPath (Join-Path $PublishDirectory 'backend\backend-manifest.json') -Raw | ConvertFrom-Json
foreach($file in $manifest.files){
    if((Get-FileHash -LiteralPath (Join-Path $PublishDirectory ('backend\'+$file.name)) -Algorithm SHA256).Hash -ne $file.bundledSha256){throw "Bundled backend hash mismatch: $($file.name)"}
}
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
# This is the existing standalone old-copy removal tool, not Inno's per-install unins000.exe.
& dotnet build (Join-Path $projectRoot 'tools\MadokaChanger.Uninstaller\MadokaChanger.Uninstaller.csproj') -c Release
if($LASTEXITCODE -ne 0){throw 'Standalone uninstaller build failed'}
$legacyUninstaller=Join-Path $OutputDirectory 'MadokaChanger-Uninstaller.exe'
Copy-Item -LiteralPath (Join-Path $projectRoot 'tools\MadokaChanger.Uninstaller\bin\Release\net48\MadokaChanger-Uninstaller.exe') -Destination $legacyUninstaller -Force
& $CompilerPath /Qp ("/DAppVersion=$Version") ("/DPublishDir=$PublishDirectory") ("/DOutputDir=$OutputDirectory") (Join-Path $projectRoot 'installer\MadokaChanger.iss')
if($LASTEXITCODE -ne 0){throw 'Installer compilation failed'}
$installer=Join-Path $OutputDirectory "MadokaChanger-Setup-$Version.exe"
if(!(Test-Path -LiteralPath $installer)){throw 'Installer was not created'}
$hash=(Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash.ToLowerInvariant()
$uninstallerHash=(Get-FileHash -LiteralPath $legacyUninstaller -Algorithm SHA256).Hash.ToLowerInvariant()
@("$hash  $(Split-Path -Leaf $installer)","$uninstallerHash  $(Split-Path -Leaf $legacyUninstaller)") | Set-Content -LiteralPath (Join-Path $OutputDirectory 'SHA256SUMS.txt') -Encoding ascii
Write-Output "Installer: $installer"
Write-Output "SHA-256: $hash"
Write-Output "Old-copy uninstaller: $legacyUninstaller"
Write-Output "SHA-256: $uninstallerHash"
