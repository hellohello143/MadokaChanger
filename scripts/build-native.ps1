param([string]$Target = '')
$ErrorActionPreference='Stop'
$projectRoot=Split-Path -Parent $PSScriptRoot
$arguments=@('build','--manifest-path',(Join-Path $projectRoot 'native\Cargo.toml'),'--release','--locked')
if($Target){$arguments+=@('--target',$Target)}
& cargo @arguments
if($LASTEXITCODE -ne 0){throw 'Native LTK adapter build failed. Install Rust and the matching C/C++ compiler toolchain.'}
$targetRoot=if($env:CARGO_TARGET_DIR){$env:CARGO_TARGET_DIR}else{Join-Path $projectRoot 'native\target'}
if($Target){$targetRoot=Join-Path $targetRoot $Target}
$binary=Join-Path $targetRoot 'release\madokachanger-ltk.exe'
New-Item -ItemType Directory -Force (Join-Path $projectRoot 'native\bin') | Out-Null
Copy-Item -LiteralPath $binary -Destination (Join-Path $projectRoot 'native\bin\madokachanger-ltk.exe') -Force
Write-Output 'Native adapter built. scripts/publish.ps1 also prepares the bundled host and DLL.'
