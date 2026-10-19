param([switch]$FrameworkDependent,[string]$OutputDirectory)
$ErrorActionPreference='Stop'
$projectRoot=Split-Path -Parent $PSScriptRoot
if(!$OutputDirectory){$OutputDirectory=Join-Path $projectRoot 'artifacts\win-x64'}
$OutputDirectory=[IO.Path]::GetFullPath($OutputDirectory)
& (Join-Path $PSScriptRoot 'prepare-backend.ps1')
if(!(Test-Path (Join-Path $projectRoot 'native\bin\madokachanger-ltk.exe'))){& (Join-Path $PSScriptRoot 'build-native.ps1')}
& dotnet build (Join-Path $projectRoot 'MadokaChanger.sln') -c Release
if($LASTEXITCODE -ne 0){throw 'Build failed'}
& dotnet test (Join-Path $projectRoot 'tests\MadokaChanger.Tests\MadokaChanger.Tests.csproj') -c Release --no-build
if($LASTEXITCODE -ne 0){throw 'Tests failed'}
$selfContained=if($FrameworkDependent){'false'}else{'true'}
& dotnet publish (Join-Path $projectRoot 'src\MadokaChanger.App\MadokaChanger.App.csproj') -c Release -r win-x64 --self-contained $selfContained -p:PublishReadyToRun=true -o $OutputDirectory
if($LASTEXITCODE -ne 0){throw 'Publish failed'}
Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md'),(Join-Path $projectRoot 'THIRD_PARTY_NOTICES.md'),(Join-Path $projectRoot 'LICENSE') -Destination $OutputDirectory
$licenseDestination=Join-Path $OutputDirectory 'licenses'
New-Item -ItemType Directory -Force $licenseDestination | Out-Null
Get-ChildItem -LiteralPath (Join-Path $projectRoot 'licenses') | Copy-Item -Destination $licenseDestination -Recurse -Force
