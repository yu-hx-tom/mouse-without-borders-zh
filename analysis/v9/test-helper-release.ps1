$ErrorActionPreference='Stop'
$root=Split-Path (Split-Path $PSScriptRoot)
$out=Join-Path $PSScriptRoot 'helper-release-tests'
[IO.Directory]::CreateDirectory($out)|Out-Null
$csc=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
# Isolated name permits the real same-directory, same-session IPC trust check.
$exe=Join-Path $out 'MouseWithoutBorders.exe'
& $csc /nologo /noconfig /r:System.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll "/out:$exe" "$root\transfer\HelperNativeDrag.cs" "$root\transfer\NativeDragCapture.cs" "$PSScriptRoot\HelperReleaseTests.cs"
if($LASTEXITCODE -ne 0){throw 'Helper release test compilation failed'}
& $exe | Tee-Object -FilePath (Join-Path $out 'result.txt')
if($LASTEXITCODE -ne 0){throw 'Helper release regression failed'}
