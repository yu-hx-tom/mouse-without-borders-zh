[CmdletBinding()]
param(
    [ValidateSet('Install','Restore','PrepareOnly','SelfTest')][string]$Mode='Install',
    [string]$OriginalDirectory,
    [string]$WorkDirectory
)
$ErrorActionPreference='Stop'
if($PSVersionTable.PSVersion.Major -lt 7){throw '请使用 PowerShell 7 运行。'}
$package=$PSScriptRoot
if($Mode -in @('Install','Restore')){
    $admin=([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    if(!$admin){
        $args='-NoProfile -ExecutionPolicy Bypass -File "'+$PSCommandPath+'" -Mode '+$Mode
        $p=Start-Process -FilePath (Join-Path $PSHOME 'pwsh.exe') -Verb RunAs -ArgumentList $args -Wait -PassThru
        exit $p.ExitCode
    }
}
if(!$OriginalDirectory){
    $svc=Get-CimInstance Win32_Service -Filter "Name='MouseWithoutBordersSvc'"
    if(!$svc){throw '请先安装微软官方 Mouse without Borders 2.2.1.0327 独立版。'}
    $path=if($svc.PathName -match '^"([^"]+)"'){$matches[1]}else{$svc.PathName}
    $OriginalDirectory=Split-Path $path
}
$OriginalDirectory=[IO.Path]::GetFullPath($OriginalDirectory)
$baseMain='907001750182086A11FF248625934465CC0ABC02A14654A7B07D811C7391D40F'
$baseHelper='D7E8F30B3F87373E89E8DEC1273F161C478E621E2450279A01A3D0914D754B4F'
$oldNative='06D35CDE2D28F62D2EA7DE3B1996951CAD39AD5234ECE2F1DC54931D1949A851'
$fixedMain='FD1C1DA728F60A4AB6D6F914806B70AA40AA5929D7EB09FA9F6FC4361DB481E7'
$nativeHelper='EFFAE49A48F73AA651A026B5920B800011483671279205AA90EB1AEAAD4EC981'
$main=Join-Path $OriginalDirectory 'MouseWithoutBorders.exe'
$helper=Join-Path $OriginalDirectory 'MouseWithoutBordersHelper.exe'
$mainHash=(Get-FileHash -LiteralPath $main).Hash
$helperHash=(Get-FileHash -LiteralPath $helper).Hash
if([Reflection.AssemblyName]::GetAssemblyName($main).Version -ne [Version]'2.2.1.327'){throw '仅支持微软独立版 2.2.1.0327。'}
if($mainHash -notin @($baseMain,$oldNative,$fixedMain)){throw '当前主程序版本不在支持范围。请先使用微软官方安装器恢复 2.2.1.0327 原版，再运行增强包。'}
$payloadManifest=Get-Content -LiteralPath "$package\release-payload\manifest.json" -Raw|ConvertFrom-Json -AsHashtable
foreach($name in @('MwbModernUi.dll','MwbTransfer.dll','MwbNativeDrag.dll')){
    if((Get-FileHash -LiteralPath "$package\release-payload\$name").Hash -ne $payloadManifest[$name]){throw "增强包校验失败：$name"}
}
if(!$WorkDirectory){$WorkDirectory=Join-Path ([IO.Path]::GetTempPath()) ('MWB-ZH-'+[Guid]::NewGuid().ToString('N'))}
$work=[IO.Path]::GetFullPath($WorkDirectory)
if(Test-Path -LiteralPath $work){throw '构建目录已存在，请指定一个新的空目录。'}
New-Item -ItemType Directory -Path "$work\original","$work\analysis\v9\candidate","$work\tools\cecil\lib\net40","$work\modern","$work\installer" -Force|Out-Null
$candidate=Join-Path $work 'analysis\v9\candidate'
foreach($name in @('build-zh.ps1','build-modern.ps1','build-transfer.ps1','suppress-transfer-logs.ps1')){Copy-Item -LiteralPath "$package\$name" -Destination $work}
foreach($name in @('patch-native-main.ps1','patch-native-helper.ps1','VerifyNativeCandidate.cs')){Copy-Item -LiteralPath "$package\analysis\v9\$name" -Destination "$work\analysis\v9"}
Copy-Item -LiteralPath "$package\analysis\translations.json" -Destination "$work\analysis"
Copy-Item -LiteralPath "$package\tools\cecil\lib\net40\Mono.Cecil.dll" -Destination "$work\tools\cecil\lib\net40"
Copy-Item -LiteralPath "$package\installer\Setup.cs","$package\installer\Setup.manifest" -Destination "$work\installer"
foreach($name in @('MwbModernUi.dll','MwbTransfer.dll','MwbNativeDrag.dll')){Copy-Item -LiteralPath "$package\release-payload\$name" -Destination $candidate}
# These dependencies stay on the user's computer; they are never bundled publicly.
foreach($name in @('Microsoft.ApplicationInsights.dll','Microsoft.Diagnostics.Tracing.EventSource.dll')){
    $dependency=Join-Path $OriginalDirectory $name
    if(Test-Path -LiteralPath $dependency){Copy-Item -LiteralPath $dependency -Destination $candidate}
}
Add-Type -LiteralPath "$work\tools\cecil\lib\net40\Mono.Cecil.dll"
Write-Host '正在本机准备增强程序；保留现有配对。'
if($mainHash -eq $baseMain){
    if($helperHash -ne $baseHelper){throw '微软原版辅助程序不匹配。请修复原版安装。'}
    Copy-Item -LiteralPath $main,$helper -Destination "$work\original"
    Copy-Item -LiteralPath "$candidate\MwbModernUi.dll" -Destination "$work\modern"
    & "$work\build-zh.ps1" | Out-Null
    & "$work\build-modern.ps1" | Out-Null
    $modernHash=(Get-FileHash -LiteralPath "$work\modern\MouseWithoutBorders.exe").Hash
    & "$work\build-transfer.ps1" -ExpectedSourceHash $modernHash -TransferLibrary "$candidate\MwbTransfer.dll" -Destination "$candidate\MouseWithoutBorders.base.exe" | Out-Null
    & "$work\analysis\v9\patch-native-helper.ps1" | Out-Null
    & "$work\analysis\v9\patch-native-main.ps1" | Out-Null
}else{
    if($helperHash -ne $nativeHelper){throw '当前 v9 辅助程序不匹配，请先修复安装。'}
    Copy-Item -LiteralPath $main,$helper -Destination $candidate
    if($mainHash -eq $oldNative){
        $m=[Mono.Cecil.ModuleDefinition]::ReadModule("$candidate\MouseWithoutBorders.exe")
        try{
            $sim=$m.Types|Where-Object FullName -eq 'MouseWithoutBorders.InputSimu'
            $hook=$m.Types|Where-Object FullName -eq 'MouseWithoutBorders.InputHook'
            $send=$sim.Methods|Where-Object Name -eq 'SendInputEx'
            $tag=@($send.Body.Instructions|Where-Object {$_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.Name -eq 'GetNormalMouseTag'})
            if($tag.Count -ne 1 -or $tag[0].Next.OpCode.Code -ne 'Stfld'){throw '键盘修复入口不匹配。'}
            $one=[Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Ldc_I4_1)
            $call=[Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Call,($hook.Methods|Where-Object Name -eq 'set_RealData'))
            $il=$send.Body.GetILProcessor();$il.InsertAfter($tag[0].Next,$one);$il.InsertAfter($one,$call)
            $m.Write("$candidate\patched.exe")
        }finally{$m.Dispose()}
        Move-Item -LiteralPath "$candidate\patched.exe" -Destination "$candidate\MouseWithoutBorders.exe" -Force
    }
    @('MouseWithoutBorders.InputSimu|SendInputEx')|Set-Content -LiteralPath "$candidate\jit-methods.txt" -Encoding utf8
}
$csc=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $csc /nologo /noconfig "/out:$candidate\VerifyNativeCandidate.exe" "$work\analysis\v9\VerifyNativeCandidate.cs"
if($LASTEXITCODE -ne 0){throw '程序验证器编译失败。'}
& "$candidate\VerifyNativeCandidate.exe" $candidate
if($LASTEXITCODE -ne 0){throw '增强程序验证失败。'}
$files=@('MouseWithoutBorders.exe','MwbModernUi.dll','MwbTransfer.dll','MouseWithoutBordersHelper.exe','MwbNativeDrag.dll')
$files|ForEach-Object {$_+'|'+(Get-FileHash -LiteralPath "$candidate\$_").Hash}|Set-Content -LiteralPath "$work\installer\payload.manifest" -Encoding utf8
$accepted=@($baseMain,$oldNative,$fixedMain,'785C2C5A68A1C1040DF4DAFC09398A286B703A94E39B1871007FB1E7961B47DD')
@(
    ('AcceptedExeHashes|'+($accepted -join ','))
    ('MouseWithoutBordersSvc.exe|'+(Get-FileHash -LiteralPath "$OriginalDirectory\MouseWithoutBordersSvc.exe").Hash)
    ('MouseWithoutBordersHelper.exe|'+($baseHelper+','+$nativeHelper))
)|Set-Content -LiteralPath "$work\installer\setup.metadata" -Encoding utf8
@'
using System.Reflection;
[assembly: AssemblyVersion("9.0.0.11")]
[assembly: AssemblyFileVersion("9.0.0.11")]
[assembly: AssemblyProduct("无界鼠标 中文增强版")]
'@|Set-Content -LiteralPath "$work\installer\AssemblyInfo.cs" -Encoding utf8
$compile=@('/nologo','/noconfig','/optimize+','/r:System.dll','/r:System.Core.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll','/r:System.Management.dll','/r:System.ServiceProcess.dll',"/resource:$work\installer\setup.metadata,setup.metadata","/resource:$work\installer\payload.manifest,payload.manifest")
foreach($name in $files){$compile+="/resource:$candidate\$name,payload.$name"}
$compile+=@("$work\installer\Setup.cs","$work\installer\AssemblyInfo.cs")
$setup=Join-Path $work 'MWB-ZH-local-setup.exe'
& $csc /target:exe "/out:$setup" "/win32manifest:$work\installer\Setup.manifest" @compile
if($LASTEXITCODE -ne 0){throw '本机增强安装器生成失败。'}
if($Mode -eq 'PrepareOnly'){Write-Host "准备完成（未安装）：$setup";return}
if($Mode -eq 'SelfTest'){& $setup --self-test;if($LASTEXITCODE -ne 0){throw '安装器自检失败。'};return}
& $setup $(if($Mode -eq 'Restore'){'--restore'}else{'--install'})
if($LASTEXITCODE -ne 0){throw '更新未完成，请查看上方的恢复提示。'}
