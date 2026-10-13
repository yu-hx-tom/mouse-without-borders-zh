param([switch]$ShellCount, [switch]$Interactive)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$compiler = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$wpf = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\WPF'
$source = Join-Path $root 'transfer\DropTarget.cs'
$name = if ($ShellCount -or $Interactive) { 'DropTargetIntegrationProbe' } else { 'DropTargetTests' }
$output = Join-Path $PSScriptRoot ($name + '.exe')
& $compiler /nologo /target:exe ('/out:' + $output) /r:System.Windows.Forms.dll /r:System.Drawing.dll ('/r:' + $wpf + '\UIAutomationClient.dll') ('/r:' + $wpf + '\UIAutomationTypes.dll') ('/r:' + $wpf + '\WindowsBase.dll') $source (Join-Path $PSScriptRoot ($name + '.cs'))
if ($LASTEXITCODE -ne 0) { throw 'DropTarget test compilation failed.' }
if ($ShellCount) { & $output --shell-count }
elseif ($Interactive) { & $output --interactive }
else { & $output }
if ($LASTEXITCODE -ne 0) { throw ('DropTarget test failed: ' + $LASTEXITCODE) }
