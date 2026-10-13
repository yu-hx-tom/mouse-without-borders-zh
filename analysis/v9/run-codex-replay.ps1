# Replays Codex's 9.0.0.9 counterexamples against this source tree (fake platform only).
$ErrorActionPreference='Stop'
$root=Split-Path (Split-Path $PSScriptRoot)
$fx=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$sources=@('NativeDragSession','NativeDragCapture','NativeInputTag','HelperNativeDrag','NativeHost','NativeWire','NativeCaptureProbe','NativePlatform','NativeDragRuntime') | ForEach-Object { Join-Path "$root\transfer" ($_+'.cs') }
if(-not (Test-Path "$PSScriptRoot\runtime-tests\MwbTransfer.dll")){ & "$PSScriptRoot\test-native-runtime.ps1" | Out-Null }
foreach($arch in @('x86','x64')) {
    $out=Join-Path $PSScriptRoot "codex-replay-$arch"
    New-Item -ItemType Directory -Path $out -Force | Out-Null
    Copy-Item -LiteralPath "$PSScriptRoot\runtime-tests\MwbTransfer.dll" -Destination $out -Force
    $exe=Join-Path $out 'CodexReplay.exe'
    & "$fx\csc.exe" /nologo /noconfig /target:exe /main:CodexReplay "/platform:$arch" /r:System.dll /r:System.Core.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll "/r:$out\MwbTransfer.dll" "/out:$exe" @sources "$PSScriptRoot\NativeRuntimeTests.cs" "$PSScriptRoot\CodexReplay-9.0.0.9-on-9.0.0.10.cs"
    if($LASTEXITCODE -ne 0){throw 'Replay compilation failed'}
    & $exe | Tee-Object -FilePath "$out\results.txt"
    if($LASTEXITCODE -ne 0){throw "A Codex counterexample still reproduces: $arch"}
}
