# Build an offline modern-UI candidate; never writes to the installed application.
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$source = Join-Path $root 'zh\MouseWithoutBorders.exe'
$uiDll = Join-Path $root 'modern\MwbModernUi.dll'
$dest = Join-Path $root 'modern\MouseWithoutBorders.exe'
$reportPath = Join-Path $root 'analysis\modern-verification.json'
Add-Type -Path (Join-Path $root 'tools\cecil\lib\net40\Mono.Cecil.dll')
$openedModules = [Collections.Generic.List[Mono.Cecil.ModuleDefinition]]::new()

function ReadModule([string]$path) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Required file missing: $path" }
    $module = [Mono.Cecil.ModuleDefinition]::ReadModule($path)
    $openedModules.Add($module)
    return $module
}
function BytesKey([byte[]]$bytes) { return [Convert]::ToBase64String($bytes) }
function OperandKey($operand) {
    if ($operand -is [Mono.Cecil.Cil.Instruction]) { return 'branch:' + $operand.Offset }
    if ($operand -is [Mono.Cecil.Cil.Instruction[]]) { return 'switch:' + (($operand | ForEach-Object Offset) -join ',') }
    if ($operand -is [Mono.Cecil.MemberReference]) {
        $scope = if ($operand -is [Mono.Cecil.TypeReference]) { $operand.Scope } else { $operand.DeclaringType.Scope }
        return ([string]$operand) + ' | scope:' + [string]$scope
    }
    return [string]$operand
}
function InstructionEqual($first, $second, [string]$where) {
    if ($first.OpCode.Code -ne $second.OpCode.Code -or (OperandKey $first.Operand) -cne (OperandKey $second.Operand)) {
        throw "Unexpected instruction change in $where : $first -> $second"
    }
}
function FindType($module, [string]$name) {
    $found = @($module.Types | Where-Object FullName -CEQ $name)
    if ($found.Count -ne 1) { throw "Expected one type: $name" }
    return $found[0]
}
function FindMethod($type, [string]$name) {
    $found = @($type.Methods | Where-Object Name -CEQ $name)
    if ($found.Count -ne 1) { throw "Expected one method: $($type.FullName)::$name" }
    return $found[0]
}
function FindUiMethod($type, [string]$name, [string]$argument) {
    $method = FindMethod $type $name
    if (-not $method.IsPublic -or -not $method.IsStatic -or $method.ReturnType.FullName -cne 'System.Void' -or
        $method.Parameters.Count -ne 1 -or $method.Parameters[0].ParameterType.FullName -cne $argument) {
        throw "Unexpected UI method signature: $($method.FullName)"
    }
    return $method
}
function AppendCtorCall($ctor, $reference) {
    if ($ctor.Parameters.Count -ne 0 -or $ctor.Body.ExceptionHandlers.Count -ne 0) { throw 'Unexpected constructor shape' }
    $instructions = $ctor.Body.Instructions
    $ret = $instructions[$instructions.Count - 1]
    if ($ret.OpCode.Code -ne [Mono.Cecil.Cil.Code]::Ret -or @($instructions | Where-Object { $_.OpCode.Code -eq [Mono.Cecil.Cil.Code]::Ret }).Count -ne 1) {
        throw 'Expected one final constructor return'
    }
    # These audited constructors are straight-line; do not silently skip a call through an early branch.
    if (@($instructions | Where-Object { $_.Operand -is [Mono.Cecil.Cil.Instruction] -or $_.Operand -is [Mono.Cecil.Cil.Instruction[]] }).Count -ne 0) {
        throw 'Unexpected constructor branch'
    }
    $il = $ctor.Body.GetILProcessor()
    $il.InsertBefore($ret, [Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Ldarg_0))
    $il.InsertBefore($ret, [Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Call, $reference))
}
function ReplaceWithUiCall($method, $reference) {
    if ($method.ReturnType.FullName -cne 'System.Void' -or $method.IsStatic) { throw "Unexpected layout signature: $($method.FullName)" }
    $method.Body = [Mono.Cecil.Cil.MethodBody]::new($method)
    if ($null -ne $reference) {
        $method.Body.Instructions.Add([Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Ldarg_0))
        $method.Body.Instructions.Add([Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Call, $reference))
    }
    $method.Body.Instructions.Add([Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Ret))
}
function AssertBodyMetadata($first, $second) {
    if ($first.InitLocals -ne $second.InitLocals -or $first.Variables.Count -ne $second.Variables.Count -or $first.ExceptionHandlers.Count -ne $second.ExceptionHandlers.Count) {
        throw 'Unexpected local or exception-handler change'
    }
    for ($i = 0; $i -lt $first.Variables.Count; $i++) {
        if ($first.Variables[$i].VariableType.FullName -cne $second.Variables[$i].VariableType.FullName) { throw 'Local type changed' }
    }
    for ($i = 0; $i -lt $first.ExceptionHandlers.Count; $i++) {
        $a = $first.ExceptionHandlers[$i]; $b = $second.ExceptionHandlers[$i]
        foreach ($property in @('HandlerType','CatchType','TryStart','TryEnd','HandlerStart','HandlerEnd','FilterStart')) {
            if ((OperandKey $a.$property) -cne (OperandKey $b.$property)) { throw "Exception handler changed: $property" }
        }
    }
}
function CheckUiBody($method, [string]$uiName) {
    $instructions = $method.Body.Instructions
    if ($uiName -eq 'None') {
        if ($instructions.Count -ne 1 -or $instructions[0].OpCode.Code -ne [Mono.Cecil.Cil.Code]::Ret) { throw 'Unexpected logo body' }
    } else {
        if ($instructions.Count -ne 3 -or $instructions[0].OpCode.Code -ne [Mono.Cecil.Cil.Code]::Ldarg_0 -or
            $instructions[1].OpCode.Code -ne [Mono.Cecil.Cil.Code]::Call -or $instructions[1].Operand.FullName -cne $script:uiRefs[$uiName].FullName -or
            $instructions[2].OpCode.Code -ne [Mono.Cecil.Cil.Code]::Ret) { throw "Unexpected layout body: $($method.FullName)" }
    }
    if ($method.Body.Variables.Count -ne 0 -or $method.Body.ExceptionHandlers.Count -ne 0) { throw 'Unexpected layout-body metadata' }
}
function CompareType($first, $second) {
    if ($first.FullName -cne $second.FullName -or $first.Attributes -ne $second.Attributes -or [string]$first.BaseType -cne [string]$second.BaseType -or
        $first.Methods.Count -ne $second.Methods.Count -or $first.Fields.Count -ne $second.Fields.Count -or $first.NestedTypes.Count -ne $second.NestedTypes.Count) {
        throw "Type metadata changed: $($first.FullName)"
    }
    for ($i = 0; $i -lt $first.Fields.Count; $i++) {
        $a = $first.Fields[$i]; $b = $second.Fields[$i]
        if ($a.FullName -cne $b.FullName -or $a.Attributes -ne $b.Attributes -or [string]$a.Constant -cne [string]$b.Constant -or
            (BytesKey $a.InitialValue) -cne (BytesKey $b.InitialValue)) { throw "Field changed: $($a.FullName)" }
    }
    for ($i = 0; $i -lt $first.Methods.Count; $i++) {
        $a = $first.Methods[$i]; $b = $second.Methods[$i]; $script:methodCount++
        if ($a.FullName -cne $b.FullName -or $a.Attributes -ne $b.Attributes -or $a.ImplAttributes -ne $b.ImplAttributes -or $a.HasBody -ne $b.HasBody) {
            throw "Method metadata changed: $($a.FullName)"
        }
        if (-not $a.HasBody) { continue }
        $change = $script:changes[$a.FullName]
        if ($change -and $change.Kind -eq 'Replace') {
            CheckUiBody $b $change.UiMethod
            $script:changedCount++
            continue
        }
        AssertBodyMetadata $a.Body $b.Body
        $oldIns = $a.Body.Instructions; $newIns = $b.Body.Instructions
        if ($change -and $change.Kind -eq 'Append') {
            if ($newIns.Count -ne $oldIns.Count + 2) { throw 'Constructor instruction-count mismatch' }
            for ($j = 0; $j -lt $oldIns.Count - 1; $j++) { InstructionEqual $oldIns[$j] $newIns[$j] $a.FullName }
            $p = $oldIns.Count - 1
            if ($newIns[$p].OpCode.Code -ne [Mono.Cecil.Cil.Code]::Ldarg_0 -or $newIns[$p+1].OpCode.Code -ne [Mono.Cecil.Cil.Code]::Call -or
                $newIns[$p+1].Operand.FullName -cne $script:uiRefs[$change.UiMethod].FullName -or $newIns[$p+2].OpCode.Code -ne [Mono.Cecil.Cil.Code]::Ret) {
                throw 'Constructor UI tail mismatch'
            }
            $script:changedCount++
        } else {
            if ($oldIns.Count -ne $newIns.Count) { throw "Unexpected method size change: $($a.FullName)" }
            for ($j = 0; $j -lt $oldIns.Count; $j++) {
                if ($oldIns[$j].Offset -ne $newIns[$j].Offset) { throw "Instruction offset changed: $($a.FullName)" }
                InstructionEqual $oldIns[$j] $newIns[$j] $a.FullName
            }
            $script:unchangedCount++
        }
    }
    for ($i = 0; $i -lt $first.NestedTypes.Count; $i++) { CompareType $first.NestedTypes[$i] $second.NestedTypes[$i] }
}

try {
    $module = ReadModule $source
    $ui = ReadModule $uiDll
    if ($ui.Assembly.Name.Name -cne 'MwbModernUi') { throw 'UI library assembly must be named MwbModernUi' }
    if (-not $ui.Assembly.Name.HasPublicKey -or $ui.Assembly.Name.PublicKeyToken.Length -ne 8 -or
        -not ($ui.Attributes -band [Mono.Cecil.ModuleAttributes]::StrongNameSigned)) {
        throw 'The strong-named application requires our signed UI dependency. Run build-modern-ui.ps1 first.'
    }
    if (@($module.AssemblyReferences | Where-Object Name -CEQ 'MwbModernUi').Count) { throw 'Source already has modern UI reference' }
    $style = FindType $ui 'MwbModernUi.Style'
    if (-not $style.IsPublic) { throw 'UI Style type must be public' }
    $uiRefs = @{}
    foreach ($name in @('Apply','Relayout','Arrange')) {
        $uiRefs[$name] = $module.ImportReference((FindUiMethod $style $name 'System.Windows.Forms.Form'))
    }
    $uiRefs['ApplyMachine'] = $module.ImportReference((FindUiMethod $style 'ApplyMachine' 'System.Windows.Forms.Control'))
    $form = FindType $module 'MouseWithoutBorders.frmMatrix'
    $machine = FindType $module 'MouseWithoutBorders.Machine'
    $changes = @{}
    foreach ($entry in @(@($form,'Apply'), @($machine,'ApplyMachine'))) {
        $ctor = FindMethod $entry[0] '.ctor'
        AppendCtorCall $ctor $uiRefs[$entry[1]]
        $changes[$ctor.FullName] = @{ Kind='Append'; UiMethod=$entry[1] }
    }
    foreach ($entry in @(@('frmMatrix_Resize','Relayout'), @('ArrangeMachines','Arrange'), @('PaintMyLogo','None'))) {
        $method = FindMethod $form $entry[0]
        $reference = if ($entry[1] -eq 'None') { $null } else { $uiRefs[$entry[1]] }
        ReplaceWithUiCall $method $reference
        $changes[$method.FullName] = @{ Kind='Replace'; UiMethod=$entry[1] }
    }
    $module.Write($dest)
    $before = ReadModule $source
    $after = ReadModule $dest
    if ($before.Assembly.Name.FullName -cne $after.Assembly.Name.FullName -or $before.Mvid -ne $after.Mvid -or
        $before.Kind -ne $after.Kind -or $before.Architecture -ne $after.Architecture -or $before.Attributes -ne $after.Attributes -or
        [string]$before.EntryPoint -cne [string]$after.EntryPoint -or $before.Types.Count -ne $after.Types.Count) { throw 'Assembly identity or module metadata changed' }
    $beforeRefs = @($before.AssemblyReferences | ForEach-Object FullName)
    $afterRefs = @($after.AssemblyReferences | ForEach-Object FullName)
    if ($afterRefs.Count -ne $beforeRefs.Count + 1) { throw 'Unexpected assembly reference count' }
    foreach ($ref in $beforeRefs) { if ($afterRefs -cnotcontains $ref) { throw "Assembly reference changed: $ref" } }
    $newRefs = @($after.AssemblyReferences | Where-Object { $beforeRefs -cnotcontains $_.FullName })
    if ($newRefs.Count -ne 1 -or $newRefs[0].FullName -cne $ui.Assembly.Name.FullName) { throw 'Unexpected external dependency' }
    $methodCount = 0; $changedCount = 0; $unchangedCount = 0
    for ($i = 0; $i -lt $before.Types.Count; $i++) { CompareType $before.Types[$i] $after.Types[$i] }
    if ($changedCount -ne 5) { throw 'Expected exactly five explicitly approved UI method changes' }
    if ($before.Resources.Count -ne $after.Resources.Count) { throw 'Resource count changed' }
    for ($i = 0; $i -lt $before.Resources.Count; $i++) {
        $a = $before.Resources[$i]; $b = $after.Resources[$i]
        if ($a.Name -cne $b.Name -or $a.Attributes -ne $b.Attributes -or $a.ResourceType -ne $b.ResourceType) { throw 'Resource metadata changed' }
        if ($a -isnot [Mono.Cecil.EmbeddedResource]) { throw "Unaudited non-embedded resource: $($a.Name)" }
        if ((BytesKey $a.GetResourceData()) -cne (BytesKey $b.GetResourceData())) { throw "Resource bytes changed: $($a.Name)" }
    }
    $report = [ordered]@{
        Pass = $true
        Source = $source
        Candidate = $dest
        UiLibrary = $uiDll
        AssemblyIdentity = $after.Assembly.Name.FullName
        AddedAssemblyReference = $newRefs[0].FullName
        UiLibraryHasStrongName = $true
        VerifiedMethods = $methodCount
        UnchangedMethodBodies = $unchangedCount
        ChangedUiMethods = @($changes.Keys | Sort-Object | ForEach-Object { @{ Method=$_; Kind=$changes[$_].Kind; UiMethod=$changes[$_].UiMethod } })
        UnchangedEmbeddedResources = $before.Resources.Count
        SourceSHA256 = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
        CandidateSHA256 = (Get-FileHash -LiteralPath $dest -Algorithm SHA256).Hash
        UiLibrarySHA256 = (Get-FileHash -LiteralPath $uiDll -Algorithm SHA256).Hash
        OriginalMicrosoftSignaturePreserved = $false
        RuntimeVerified = $false
        LiveFilesModified = $false
    }
    $report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $reportPath -Encoding utf8
    $report | ConvertTo-Json -Depth 6
} catch {
    [ordered]@{ Pass=$false; Error=$_.Exception.Message; Source=$source; Candidate=$dest; LiveFilesModified=$false } |
        ConvertTo-Json | Set-Content -LiteralPath $reportPath -Encoding utf8
    throw
} finally {
    foreach ($opened in $openedModules) { $opened.Dispose() }
}
