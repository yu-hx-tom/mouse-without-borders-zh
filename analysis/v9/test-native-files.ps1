$ErrorActionPreference='Stop'
$root=Split-Path (Split-Path $PSScriptRoot)
$fx=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$out=Join-Path $PSScriptRoot 'file-tests'
[IO.Directory]::CreateDirectory($out) | Out-Null
$exe=Join-Path $out 'NativeFileRequestTests.exe'
& "$fx\csc.exe" /nologo /noconfig /target:exe /optimize+ /r:System.dll /r:System.Core.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll "/r:$fx\WPF\UIAutomationClient.dll" "/r:$fx\WPF\UIAutomationTypes.dll" "/r:$fx\WPF\WindowsBase.dll" "/out:$exe" "$root\transfer\Transfer.cs" "$root\transfer\DropTarget.cs" "$root\transfer\DragPreview.cs" "$root\transfer\NativeFileRequests.cs" "$PSScriptRoot\NativeFileRequestTests.cs"
if($LASTEXITCODE -ne 0){throw 'Native file tests failed to compile'}
& $exe (Join-Path $out ([Guid]::NewGuid().ToString('N')))
if($LASTEXITCODE -ne 0){throw 'Native file checks failed'}
