$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$fx=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$runner=Join-Path $PSScriptRoot 'DragPreviewTests.exe'
$output=Join-Path $PSScriptRoot 'drag-preview-tests'
& "$fx\csc.exe" /nologo /target:exe /platform:x64 /optimize+ "/out:$runner" /r:System.Windows.Forms.dll /r:System.Drawing.dll "/r:$fx\WPF\UIAutomationClient.dll" "/r:$fx\WPF\UIAutomationTypes.dll" "/r:$fx\WPF\WindowsBase.dll" "$root\transfer\DropTarget.cs" "$root\transfer\DragPreview.cs" "$root\transfer\Transfer.cs" "$PSScriptRoot\DragPreviewTests.cs"
if($LASTEXITCODE -ne 0){throw 'Drag preview test compilation failed'}
& $runner $output | Tee-Object -FilePath (Join-Path $PSScriptRoot 'drag-preview-test-results.txt')
if($LASTEXITCODE -ne 0){throw 'Drag preview tests failed'}
