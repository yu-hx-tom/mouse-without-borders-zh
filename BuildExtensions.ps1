# Build only this project's own libraries. No Microsoft host binaries are distributed.
[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
$root=$PSScriptRoot
$out=Join-Path $root 'release-payload'
$unsigned=Join-Path $root 'build\unsigned'
New-Item -ItemType Directory -Path $out,$unsigned -Force|Out-Null
Add-Type -LiteralPath "$root\tools\cecil\lib\net40\Mono.Cecil.dll"
$key=Join-Path $root 'tools\modern-ui.snk'
if(!(Test-Path -LiteralPath $key)){
    $rsa=[Security.Cryptography.RSACryptoServiceProvider]::new(2048)
    try{$rsa.PersistKeyInCsp=$false;[IO.File]::WriteAllBytes($key,$rsa.ExportCspBlob($true))}finally{$rsa.Dispose()}
}
$fx=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$csc=Join-Path $fx 'csc.exe'
function Sign-Library([string]$name){
    $m=[Mono.Cecil.ModuleDefinition]::ReadModule("$unsigned\$name.dll")
    try{$w=[Mono.Cecil.WriterParameters]::new();$w.StrongNameKeyBlob=[IO.File]::ReadAllBytes($key);$m.Write("$out\$name.dll",$w)}finally{$m.Dispose()}
}
$refs=@('/nologo','/noconfig','/target:library','/optimize+','/r:System.dll','/r:System.Core.dll','/r:System.Windows.Forms.dll','/r:System.Drawing.dll')
& $csc @refs "/out:$unsigned\MwbModernUi.dll" "$root\modern\MwbModernUi.cs"
if($LASTEXITCODE -ne 0){throw 'UI compilation failed'};Sign-Library 'MwbModernUi'
& $csc @refs "/r:$fx\WPF\UIAutomationClient.dll" "/r:$fx\WPF\UIAutomationTypes.dll" "/r:$fx\WPF\WindowsBase.dll" "/out:$unsigned\MwbTransfer.dll" "$root\transfer\Transfer.cs" "$root\transfer\DropTarget.cs" "$root\transfer\DragPreview.cs" "$root\transfer\NativeFileRequests.cs"
if($LASTEXITCODE -ne 0){throw 'Transfer compilation failed'};Sign-Library 'MwbTransfer'
$native=@('NativeDragSession','NativeDragCapture','NativeInputTag','HelperNativeDrag','NativeHost','NativeWire','NativeCaptureProbe','NativePlatform','NativeDragRuntime')|ForEach-Object {"$root\transfer\$_.cs"}
& $csc @refs "/r:$out\MwbTransfer.dll" "/out:$unsigned\MwbNativeDrag.dll" @native
if($LASTEXITCODE -ne 0){throw 'Native extension compilation failed'};Sign-Library 'MwbNativeDrag'
$hashes=@{};foreach($f in Get-ChildItem -LiteralPath $out -Filter '*.dll'){$hashes[$f.Name]=(Get-FileHash -LiteralPath $f.FullName).Hash}
$hashes|ConvertTo-Json|Set-Content -LiteralPath "$out\manifest.json" -Encoding utf8
Write-Host 'Own libraries built. Use pristine Microsoft 2.2.1.0327 files to prepare a host with this newly generated assembly key.'
