# Compile and sign only our own local UI library, then build the offline MWB candidate.
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$source = Join-Path $root 'modern\MwbModernUi.cs'
$keyPath = Join-Path $root 'tools\modern-ui.snk'
$unsignedDir = Join-Path $root 'modern\unsigned'
$unsigned = Join-Path $unsignedDir 'MwbModernUi.dll'
$signed = Join-Path $root 'modern\MwbModernUi.dll'
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$reportPath = Join-Path $root 'analysis\modern-ui-signature.json'
foreach ($file in @($source,$keyPath,$csc)) { if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Required file missing: $file" } }
New-Item -ItemType Directory -Path $unsignedDir -Force | Out-Null
& $csc /nologo /target:library /optimize+ /r:System.Windows.Forms.dll /r:System.Drawing.dll ("/out:"+$unsigned) $source
if ($LASTEXITCODE -ne 0) { throw "UI library compilation failed: $LASTEXITCODE" }
Add-Type -Path (Join-Path $root 'tools\cecil\lib\net40\Mono.Cecil.dll')
$module = [Mono.Cecil.ModuleDefinition]::ReadModule($unsigned)
try {
    $writer = [Mono.Cecil.WriterParameters]::new()
    $writer.StrongNameKeyBlob = [IO.File]::ReadAllBytes($keyPath)
    $module.Write($signed,$writer)
} finally { $module.Dispose() }
if (-not ('ModernUiSignature.Native' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
namespace ModernUiSignature {
    public static class Native {
        [DllImport("mscoree.dll", CharSet=CharSet.Unicode, SetLastError=true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool StrongNameSignatureVerificationEx(
            string filePath,
            [MarshalAs(UnmanagedType.Bool)] bool forceVerification,
            [MarshalAs(UnmanagedType.Bool)] out bool wasVerified);
    }
}
'@
}
$name = [Reflection.AssemblyName]::GetAssemblyName($signed)
$token = ($name.GetPublicKeyToken() | ForEach-Object { $_.ToString('x2') }) -join ''
if ($token.Length -ne 16) { throw 'Signed UI library has no strong-name token' }
$wasVerified = $false
$valid = [ModernUiSignature.Native]::StrongNameSignatureVerificationEx($signed,$true,[ref]$wasVerified)
$lastError = [Runtime.InteropServices.Marshal]::GetLastWin32Error()
$report = [ordered]@{
    Pass = ($valid -and $wasVerified)
    AssemblyIdentity = $name.FullName
    StrongNameToken = $token
    ForceVerification = $true
    SignatureVerificationSucceeded = $valid
    WasVerified = $wasVerified
    VerificationWin32Error = $lastError
    LibrarySHA256 = (Get-FileHash -LiteralPath $signed -Algorithm SHA256).Hash
    AuthenticodeSigned = $false
    Scope = 'Locally generated strong-name signature for our UI library only; not Microsoft Authenticode'
}
$report | ConvertTo-Json | Set-Content -LiteralPath $reportPath -Encoding utf8
if (-not $report.Pass) { throw "Forced strong-name signature verification failed; Win32 error $lastError" }
$report | ConvertTo-Json
& (Join-Path $root 'build-modern.ps1')
