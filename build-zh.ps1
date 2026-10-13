$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
Add-Type -Path "$root/tools/cecil/lib/net40/Mono.Cecil.dll"
$source = "$root/original/MouseWithoutBorders.exe"
$dest = "$root/zh/MouseWithoutBorders.exe"
New-Item -ItemType Directory -Force "$root/zh" | Out-Null
$map = Get-Content "$root/analysis/translations.json" -Raw | ConvertFrom-Json -AsHashtable
$m = [Mono.Cecil.ModuleDefinition]::ReadModule($source)
$changes = @()
$allowed = '^MouseWithoutBorders\.(frmMatrix|Machine|frmAbout|SettingsForm|SettingsFormPage|SetupPage\w*|frmMessage|frmScreen)$'
foreach ($t in $m.Types) {
    if ($t.FullName -notmatch $allowed) { continue }
    foreach ($method in $t.Methods) {
        if (-not $method.HasBody) { continue }
        foreach ($ins in $method.Body.Instructions) {
            if ($ins.OpCode.Name -ne 'ldstr' -or -not $map.ContainsKey([string]$ins.Operand)) { continue }
            $old = [string]$ins.Operand
            if ($old -in @('Disable','Enable','Ctrl*3','All','Myself')) { throw "Logic-sensitive string: $old" }
            if ($ins.Next.ToString() -match 'set_Name|GetObject|GetString|set_Font') { throw "Resource or identifier string: $old" }
            $new = [string]$map[$old]
            $p1 = @([regex]::Matches($old,'\{\d+(?:[^}]*)\}') | ForEach-Object Value)
            $p2 = @([regex]::Matches($new,'\{\d+(?:[^}]*)\}') | ForEach-Object Value)
            if (($p1 -join '|') -cne ($p2 -join '|')) { throw "Placeholder mismatch: $old" }
            $changes += [pscustomobject]@{Method=$method.FullName;Offset=$ins.Offset;Before=$old;After=$new}
            $ins.Operand = $new
        }
    }
}
if ($changes.Count -lt 60) { throw 'Translation coverage unexpectedly low' }
$dnsResource = 'MouseWithoutBorders.frmMatrix.resources'
$dnsKey = 'textBoxDNS.Text'
$dnsText = "通常请使用电脑名称连接。若名称无法解析为 IP 地址，可在下方填写名称与 IP 的对应关系。程序会结合此映射和 DNS 查询结果进行连接。`r`n注意：若电脑使用动态 IP，地址变化后需要更新这里的映射。"
for($k=0;$k -lt $m.Resources.Count;$k++) {
    if($m.Resources[$k].Name -cne $dnsResource){continue}
    $oldResource=$m.Resources[$k]
    $reader=[Resources.ResourceReader]::new($oldResource.GetResourceStream())
    $stream=[IO.MemoryStream]::new();$writer=[Resources.ResourceWriter]::new($stream)
    $entry=$reader.GetEnumerator();$found=$false
    while($entry.MoveNext()) {
        if([string]$entry.Key -ceq $dnsKey){$writer.AddResource($dnsKey,$dnsText);$found=$true}
        else {$type='';$data=[byte[]]@();$reader.GetResourceData([string]$entry.Key,[ref]$type,[ref]$data);$writer.AddResourceData([string]$entry.Key,$type,$data)}
    }
    if(-not $found){throw 'DNS resource key missing'}
    $writer.Generate()
    $m.Resources[$k]=[Mono.Cecil.EmbeddedResource]::new($dnsResource,$oldResource.Attributes,$stream.ToArray())
    $reader.Dispose();$writer.Dispose();$stream.Dispose()
}
# Preserve module/assembly identity; only explicitly mapped UI literals change.
$m.Write($dest)
$changes | ConvertTo-Json -Depth 5 | Set-Content "$root/analysis/changes.json" -Encoding utf8
$before = [Mono.Cecil.ModuleDefinition]::ReadModule($source)
$after = [Mono.Cecil.ModuleDefinition]::ReadModule($dest)
if ($before.Assembly.Name.FullName -cne $after.Assembly.Name.FullName) { throw 'Assembly identity changed' }
if ($before.Mvid -ne $after.Mvid) { throw 'MVID changed' }
if ($before.Types.Count -ne $after.Types.Count) { throw 'Type count changed' }
$approved = @{}
foreach($c in $changes) { $approved[$c.Method+'|'+$c.Offset]=$c }
$verified = 0; $methodCount = 0
function OperandKey($o) {
    if($o -is [Mono.Cecil.Cil.Instruction]){return 'branch:'+ $o.Offset}
    if($o -is [Mono.Cecil.Cil.Instruction[]]){return 'switch:'+ (($o | ForEach-Object Offset) -join ',')}
    return [string]$o
}
function CompareType($a,$b) {
    if($a.FullName -cne $b.FullName -or $a.Attributes -ne $b.Attributes -or $a.Methods.Count -ne $b.Methods.Count -or $a.Fields.Count -ne $b.Fields.Count){throw 'Type mismatch'}
    for($k=0;$k -lt $a.Methods.Count;$k++) {
        $x=$a.Methods[$k];$y=$b.Methods[$k];$script:methodCount++
        if($x.FullName -cne $y.FullName -or $x.Attributes -ne $y.Attributes -or $x.ImplAttributes -ne $y.ImplAttributes -or $x.HasBody -ne $y.HasBody){throw 'Method mismatch'}
        if(-not $x.HasBody){continue}
        if($x.Body.Instructions.Count -ne $y.Body.Instructions.Count -or $x.Body.ExceptionHandlers.Count -ne $y.Body.ExceptionHandlers.Count){throw 'Body shape changed'}
        for($j=0;$j -lt $x.Body.Instructions.Count;$j++) {
            $u=$x.Body.Instructions[$j];$v=$y.Body.Instructions[$j]
            if($u.OpCode.Code -ne $v.OpCode.Code -or $u.Offset -ne $v.Offset){throw 'Instruction changed'}
            $key=$x.FullName+'|'+$u.Offset
            if($approved.ContainsKey($key)) {
                if([string]$u.Operand -cne $approved[$key].Before -or [string]$v.Operand -cne $approved[$key].After){throw 'Translation mismatch'}
                $script:verified++
            } elseif((OperandKey $u.Operand) -cne (OperandKey $v.Operand)) {throw "Unexpected operand change $key"}
        }
    }
    if($a.NestedTypes.Count -ne $b.NestedTypes.Count){throw 'Nested types changed'}
    for($k=0;$k -lt $a.NestedTypes.Count;$k++){CompareType $a.NestedTypes[$k] $b.NestedTypes[$k]}
}
for($k=0;$k -lt $before.Types.Count;$k++){CompareType $before.Types[$k] $after.Types[$k]}
if($before.Resources.Count -ne $after.Resources.Count){throw 'Resource count changed'}
for($k=0;$k -lt $before.Resources.Count;$k++) {
    $x=$before.Resources[$k];$y=$after.Resources[$k]
    if($x.Name -cne $y.Name){throw 'Resource identity changed'}
    if($x -is [Mono.Cecil.EmbeddedResource]) {
        $h1=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($x.GetResourceData()))
        $h2=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($y.GetResourceData()))
        if($x.Name -ceq $dnsResource) {
            $rx=[Resources.ResourceReader]::new($x.GetResourceStream());$ry=[Resources.ResourceReader]::new($y.GetResourceStream())
            $all=@{};$ey=$ry.GetEnumerator();while($ey.MoveNext()){$all[[string]$ey.Key]=$true}
            $ex=$rx.GetEnumerator();$count=0
            while($ex.MoveNext()) {
                $count++;$key=[string]$ex.Key
                if(-not $all.ContainsKey($key)){throw 'Resource entry missing'}
                if($key -ceq $dnsKey){$en=$ry.GetEnumerator();$got=$null;while($en.MoveNext()){if([string]$en.Key -ceq $key){$got=$en.Value}};if($got -cne $dnsText){throw 'DNS translation mismatch'}}
                else {$tx='';$ty='';$dx=[byte[]]@();$dy=[byte[]]@();$rx.GetResourceData($key,[ref]$tx,[ref]$dx);$ry.GetResourceData($key,[ref]$ty,[ref]$dy);if($tx -cne $ty -or [Convert]::ToBase64String($dx) -cne [Convert]::ToBase64String($dy)){throw 'Non-text resource changed'}}
            }
            if($count -ne $all.Count){throw 'Extra resource entry'}
            $rx.Dispose();$ry.Dispose()
        } elseif($h1 -cne $h2){throw 'Resource content changed'}
    }
}
if($verified -ne $changes.Count){throw 'Verification count mismatch'}
$report=[pscustomobject]@{Pass=$true;TranslatedOccurrences=$verified;TranslatedResourceStrings=1;VerifiedMethods=$methodCount;UnchangedEmbeddedResources=($before.Resources.Count-1);AssemblyIdentity=$after.Assembly.Name.FullName;OriginalSHA256=(Get-FileHash $source).Hash;PatchedSHA256=(Get-FileHash $dest).Hash;OriginalMicrosoftSignaturePreserved=$false}
$report | ConvertTo-Json | Set-Content "$root/analysis/verification.json" -Encoding utf8
$report | Format-List
$m.Dispose();$before.Dispose();$after.Dispose()
