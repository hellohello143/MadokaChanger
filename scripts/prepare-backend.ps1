param([string]$ResourceDirectory)
$ErrorActionPreference='Stop'
$projectRoot=Split-Path -Parent $PSScriptRoot
$backendDirectory=Join-Path $projectRoot 'backend'
$revision='1a3d4f900054a739f833b6a028e87caa58455fbb'
$expected=@{
    'ltk_patcher_host.exe'='23fa1aaeda1a0c743da44227f179084e1fb81c7cd262144a3c8604ffc49d3bc1'
    'ltk_patcher_dll.dll'='6d419057e6667994ba752ad0fb089b363db98267618644d7f7b6632441a21d74'
}

# LTK-PATCHER-LICENSE.md requires removing the upstream signature when bundling.
# ImageHlp edits certificate metadata only; executable sections are checked below.
if (-not ('MadokaChanger.Packaging.Certificates' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.ComponentModel;
using Microsoft.Win32.SafeHandles;
namespace MadokaChanger.Packaging {
    public static class Certificates {
        [DllImport("imagehlp.dll", SetLastError=true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ImageEnumerateCertificates(SafeFileHandle file, ushort type, out uint count, [Out] uint[] indices, uint length);
        [DllImport("imagehlp.dll", SetLastError=true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ImageRemoveCertificate(SafeFileHandle file, uint index);
        public static void Remove(string path) {
            using (var file=File.Open(path,FileMode.Open,FileAccess.ReadWrite,FileShare.Read)) {
                var indices=new uint[32];
                for (int attempt=0;attempt<32;attempt++) {
                    uint count;
                    if (!ImageEnumerateCertificates(file.SafeFileHandle,0xff,out count,indices,(uint)indices.Length)) throw new Win32Exception();
                    if (count==0) return;
                    if (!ImageRemoveCertificate(file.SafeFileHandle,indices[0])) throw new Win32Exception();
                }
                throw new InvalidDataException("Too many certificates.");
            }
        }
        public static void AssertSectionsUnchanged(string original,string bundled) {
            var before=File.ReadAllBytes(original);var after=File.ReadAllBytes(bundled);
            int pe=BitConverter.ToInt32(before,0x3c);
            int sections=BitConverter.ToUInt16(before,pe+6);
            int table=pe+24+BitConverter.ToUInt16(before,pe+20);
            for(int i=0;i<sections;i++) {
                int entry=table+i*40;
                int size=BitConverter.ToInt32(before,entry+16),offset=BitConverter.ToInt32(before,entry+20);
                if(offset<0 || size<0 || offset>after.Length-size) throw new InvalidDataException("Invalid PE section.");
                for(int j=0;j<size;j++) if(before[offset+j]!=after[offset+j]) throw new InvalidDataException("Executable section changed.");
            }
        }
    }
}
'@
}

New-Item -ItemType Directory -Force -Path $backendDirectory | Out-Null
$manifestPath=Join-Path $backendDirectory 'backend-manifest.json'
$existing=if(Test-Path -LiteralPath $manifestPath){Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json}else{$null}
$records=@()
foreach($name in @('ltk_patcher_host.exe','ltk_patcher_dll.dll')) {
    $target=Join-Path $backendDirectory $name
    $url="https://raw.githubusercontent.com/LeagueToolkit/ltk-manager/$revision/src-tauri/resources/$name"
    $previous=if($existing){$existing.files | Where-Object name -eq $name}else{$null}
    if($existing -and $existing.revision -eq $revision -and $previous -and (Test-Path -LiteralPath $target) -and
       (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -eq $previous.bundledSha256 -and
       (Get-AuthenticodeSignature -LiteralPath $target).Status -eq 'NotSigned') {
        $records+=$previous
        continue
    }
    $temporary=[System.IO.Path]::GetTempFileName()
    try {
        if($ResourceDirectory){Copy-Item -LiteralPath (Join-Path $ResourceDirectory $name) -Destination $temporary -Force}
        else{Invoke-WebRequest -Uri $url -OutFile $temporary}
        if((Get-FileHash -LiteralPath $temporary -Algorithm SHA256).Hash -ne $expected[$name]){throw "Upstream SHA-256 mismatch: $name"}
        Copy-Item -LiteralPath $temporary -Destination $target -Force
        [MadokaChanger.Packaging.Certificates]::Remove($target)
        [MadokaChanger.Packaging.Certificates]::AssertSectionsUnchanged($temporary,$target)
        if((Get-AuthenticodeSignature -LiteralPath $target).Status -ne 'NotSigned'){throw "Upstream signature remains: $name"}
        $records+=[pscustomobject]@{name=$name;source=$url;upstreamSha256=$expected[$name];bundledSha256=(Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant()}
    }
    finally{Remove-Item -LiteralPath $temporary -Force}
}
[ordered]@{revision=$revision;license='../licenses/LTK-PATCHER-LICENSE.md';upstreamSignaturesRemoved=$true;executableSectionsUnchanged=$true;files=$records} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $manifestPath -Encoding utf8
Write-Output 'Built-in host and DLL are ready; no end-user backend setup is required.'
