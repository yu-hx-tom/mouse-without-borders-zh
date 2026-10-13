# v9.0.0.7 coordinator replay (fake platform). Builds an isolated MwbTransfer
# reference in its own output folder; never touches candidate/setup or the system.
$ErrorActionPreference='Stop'
$root=Split-Path (Split-Path $PSScriptRoot)
$fx=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$csc=Join-Path $fx 'csc.exe'
$out=Join-Path $PSScriptRoot 'runtime-tests'
[IO.Directory]::CreateDirectory($out)|Out-Null
$t="$root\transfer"
& $csc /nologo /noconfig /target:library /r:System.dll /r:System.Core.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll "/r:$fx\WPF\UIAutomationClient.dll" "/r:$fx\WPF\UIAutomationTypes.dll" "/r:$fx\WPF\WindowsBase.dll" "/out:$out\MwbTransfer.dll" "$t\Transfer.cs" "$t\DropTarget.cs" "$t\DragPreview.cs" "$t\NativeFileRequests.cs"
if($LASTEXITCODE -ne 0){throw 'Reference transfer library failed to compile'}
$results=@()
foreach($arch in @('x86','x64')){
    $exe=Join-Path $out "NativeRuntimeTests-$arch.exe"
    & $csc /nologo /noconfig /target:exe /optimize+ "/platform:$arch" /r:System.dll /r:System.Core.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll "/r:$out\MwbTransfer.dll" "/out:$exe" "$t\NativeDragSession.cs" "$t\NativeDragCapture.cs" "$t\NativeInputTag.cs" "$t\HelperNativeDrag.cs" "$t\NativeHost.cs" "$t\NativeWire.cs" "$t\NativeCaptureProbe.cs" "$t\NativePlatform.cs" "$t\NativeDragRuntime.cs" "$PSScriptRoot\NativeRuntimeTests.cs"
    if($LASTEXITCODE -ne 0){throw "Runtime replay failed to compile: $arch"}
    $result=& $exe
    if($LASTEXITCODE -ne 0){throw "Runtime replay assertions failed: $arch"}
    Write-Output $result
    $results+=[ordered]@{Architecture=$arch;Result="$result"}
}
[ordered]@{Scope='Coordinator replay with fake platform; no input/window/socket. Not a substitute for dual-PC acceptance.';Pass=$true;Results=$results}|ConvertTo-Json -Depth 4|Set-Content -LiteralPath (Join-Path $out 'verification.json') -Encoding UTF8
