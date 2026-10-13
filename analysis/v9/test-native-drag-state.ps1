$ErrorActionPreference='Stop'
$root=Split-Path (Split-Path $PSScriptRoot)
$csc=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$out=Join-Path $PSScriptRoot 'state-tests'
[IO.Directory]::CreateDirectory($out) | Out-Null
$results=@()
foreach($arch in @('x86','x64')) {
    $exe=Join-Path $out "NativeDragStateTests-$arch.exe"
    & $csc /nologo /target:exe /optimize+ "/platform:$arch" "/out:$exe" "$root\transfer\NativeDragSession.cs" "$root\transfer\NativeDragCapture.cs" "$root\transfer\NativeInputTag.cs" "$PSScriptRoot\NativeDragStateTests.cs"
    if($LASTEXITCODE -ne 0){throw "Native drag state test compilation failed: $arch"}
    $result=& $exe
    if($LASTEXITCODE -ne 0){throw "Native drag state assertions failed: $arch"}
    $results += [ordered]@{Architecture=$arch;Result="$result";SHA256=(Get-FileHash -LiteralPath $exe).Hash}
    Write-Output $result
}
[ordered]@{Scope='Isolated lifecycle and ABI checks; not installed and not network/OLE integration';Pass=$true;Results=$results} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $out 'verification.json') -Encoding UTF8
