# Dot-source this file from build-transfer.ps1 before taking expected BodyKey snapshots.
# This mutates only the supplied offline Cecil module; it never writes files or live state.
# Scope: transfer/clipboard transport diagnostics, not general keyboard, pairing or service logs.
function Get-MwbTransferLogMethods {
    param([Mono.Cecil.ModuleDefinition]$Module)
    $commonNames = @('CheckClipboardEx','ConnectAndGetData','ReceiveAndProcessClipboardData',
        'ShakeHand','ConnectToRemoteClipboardSocket','SendDropBegin','SignalBigClipboardData')
    $socketNames = @('AcceptConnectionAndSendClipboardData','SendOrReceiveClipboardData','SendClipboardData','SendFileEx','SendFile')
    function VisitTransferType($type) {
        foreach ($method in $type.Methods) {
            if (-not $method.HasBody) { continue }
            $selected = ($type.FullName -ceq 'MouseWithoutBorders.Common' -and
                ($commonNames -ccontains $method.Name -or $method.Name -cmatch '^DragDropStep[0-9]+(?:Ex|_2)?$')) -or
                ($type.FullName -ceq 'MouseWithoutBorders.SocketStuff' -and $socketNames -ccontains $method.Name) -or
                ($type.FullName.StartsWith('MouseWithoutBorders.Common/') -and
                    ($method.Name -cmatch '^<(CheckClipboardEx|DragDropStep05Ex)>b__'))
            if ($selected) { $method }
        }
        foreach ($nested in $type.NestedTypes) { VisitTransferType $nested }
    }
    foreach ($type in $Module.Types) { VisitTransferType $type }
}
function Test-MwbTransferDiagnosticCall {
    param([Mono.Cecil.Cil.Instruction]$Instruction)
    if ($Instruction.OpCode.Code -notin @([Mono.Cecil.Cil.Code]::Call,[Mono.Cecil.Cil.Code]::Callvirt) -or
        $Instruction.Operand -isnot [Mono.Cecil.MethodReference]) { return $false }
    $method=$Instruction.Operand
    return ($method.DeclaringType.FullName -ceq 'MouseWithoutBorders.Common' -and
            $method.Name -cin @('Log','TelemetryLogTrace')) -or
        ($method.DeclaringType.FullName -ceq 'Microsoft.ApplicationInsights.TelemetryClient' -and
            $method.Name -cin @('TrackEvent','TrackException','TrackTrace'))
}
function Expand-MwbPrivacyBranches {
    param([Mono.Cecil.MethodDefinition]$Method)
    foreach ($instruction in $Method.Body.Instructions) {
        if ($instruction.OpCode.OperandType -eq [Mono.Cecil.Cil.OperandType]::ShortInlineBrTarget) {
            $longName=([string]$instruction.OpCode.Code)-replace '_S$',''
            $field=[Mono.Cecil.Cil.OpCodes].GetField($longName)
            if ($null -eq $field) { throw "Cannot expand privacy branch: $($instruction.OpCode)" }
            $instruction.OpCode=$field.GetValue($null)
        }
    }
}
function Suppress-MwbTransferLogs {
    [CmdletBinding()]
    param([Parameter(Mandatory=$true)][Mono.Cecil.ModuleDefinition]$Module)
    if ($Module.Assembly.Name.Name -cne 'MouseWithoutBorders') { throw 'Expected the MWB main executable module' }
    $changed=[Collections.Generic.List[object]]::new()
    $total=0
    foreach ($method in @(Get-MwbTransferLogMethods -Module $Module)) {
        $calls=@($method.Body.Instructions | Where-Object { Test-MwbTransferDiagnosticCall $_ })
        if ($calls.Count -eq 0) { continue }
        $records=[Collections.Generic.List[object]]::new()
        $processor=$method.Body.GetILProcessor()
        foreach ($instruction in $calls) {
            $reference=$instruction.Operand
            if ($reference.ReturnType.FullName -cne 'System.Void') { throw 'Unexpected diagnostic return value' }
            $popCount=$reference.Parameters.Count
            if ($reference.HasThis) { $popCount++ }
            $records.Add([pscustomobject]@{OriginalOffset=$instruction.Offset;Call=$reference.FullName;PoppedValues=$popCount})
            # Keep the original instruction object as the first pop, preserving existing targets.
            $instruction.OpCode=if($popCount -eq 0){[Mono.Cecil.Cil.OpCodes]::Nop}else{[Mono.Cecil.Cil.OpCodes]::Pop}
            $instruction.Operand=$null
            $cursor=$instruction
            for($n=1;$n -lt $popCount;$n++) {
                $pop=[Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Pop)
                $processor.InsertAfter($cursor,$pop);$cursor=$pop
            }
        }
        Expand-MwbPrivacyBranches $method
        $total+=$calls.Count
        $changed.Add([pscustomobject]@{Method=$method;FullName=$method.FullName;Kind='SuppressTransferDiagnosticCalls';Calls=$records.ToArray()})
    }

    # The unchanged helper forwards copied-file names through IPC. Filter only those messages;
    # retain its general startup, clipboard-hook, screenshot and failure diagnostics.
    $helper=@($Module.Types | Where-Object FullName -CEQ 'MouseWithoutBorders.ClipboardHelper')
    if ($helper.Count -ne 1) { throw 'Expected ClipboardHelper type' }
    $receivers=@($helper[0].Methods | Where-Object Name -CEQ 'MouseWithoutBorders.IClipboardHelper.SendLog')
    if ($receivers.Count -ne 1) { throw 'Expected helper log receiver' }
    $receiver=$receivers[0]
    $prefixes=@('File from clipboard: ','GetFileDropList returned no file.','GetFileDropList returned null.')
    if (@($receiver.Body.Instructions | Where-Object { $_.OpCode.Code -eq [Mono.Cecil.Cil.Code]::Ldstr -and $_.Operand -ceq $prefixes[0] }).Count) {
        throw 'Helper transfer log filter is already present'
    }
    $starts=@($Module.GetMemberReferences() | Where-Object FullName -CEQ 'System.Boolean System.String::StartsWith(System.String)')
    if ($starts.Count -ne 1) { throw 'Expected existing String.StartsWith reference' }
    $first=$receiver.Body.Instructions[0];$processor=$receiver.Body.GetILProcessor()
    $earlyReturn=[Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Ret)
    $injected=[Collections.Generic.List[Mono.Cecil.Cil.Instruction]]::new()
    $injected.Add([Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Ldarg_1))
    $injected.Add([Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Brfalse,$first))
    foreach ($prefix in $prefixes) {
        $injected.Add([Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Ldarg_1))
        $injected.Add([Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Ldstr,$prefix))
        $injected.Add([Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Callvirt,$starts[0]))
        $injected.Add([Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Brtrue,$earlyReturn))
    }
    $injected.Add([Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Br,$first))
    $injected.Add($earlyReturn)
    foreach ($instruction in $injected) { $processor.InsertBefore($first,$instruction) }
    Expand-MwbPrivacyBranches $receiver
    $changed.Add([pscustomobject]@{Method=$receiver;FullName=$receiver.FullName;Kind='FilterHelperFileDiagnostics';Calls=@();Prefixes=$prefixes})

    # Manual diagnostic dumps reflect static Common fields. Exclude clipboard/drag state
    # from that export without clearing or changing the fields used by actual transfers.
    $common=@($Module.Types | Where-Object FullName -CEQ 'MouseWithoutBorders.Common')[0]
    $dump=@($common.Methods | Where-Object Name -CEQ 'PrivateDump')
    if ($dump.Count -ne 1) { throw 'Expected private dump visitor' }
    $dump=$dump[0]
    $skipFields=@('lastDragDropFile','lastClipboardObject','bytes','m','lastMachineWithClipboardData',
        '<LastIDWithClipboardData>k__BackingField','lastClipboardEventTime','clipboardCopiedTime','clipboardThreadOld',
        'isDragging','isDroping','dragMachine','dropMachineID','DragDropStep05ExCalledByIpc')
    if ($dump.Parameters.Count -ne 6 -or $dump.Parameters[2].ParameterType.FullName -cne 'System.String' -or
        $dump.ReturnType.FullName -cne 'System.Boolean') { throw 'Unexpected PrivateDump signature' }
    $equals=@($Module.GetMemberReferences() | Where-Object FullName -CEQ 'System.Boolean System.String::op_Equality(System.String,System.String)')
    if ($equals.Count -ne 1) { throw 'Expected existing string equality reference' }
    $first=$dump.Body.Instructions[0];$processor=$dump.Body.GetILProcessor()
    $skip=[Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Ldc_I4_0)
    foreach ($field in $skipFields) {
        foreach ($instruction in @(
            [Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Ldarg_2),
            [Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Ldstr,$field),
            [Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Call,$equals[0]),
            [Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Brtrue,$skip))) {
            $processor.InsertBefore($first,$instruction)
        }
    }
    $processor.InsertBefore($first,[Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Br,$first))
    $processor.InsertBefore($first,$skip)
    $processor.InsertBefore($first,[Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Ret))
    Expand-MwbPrivacyBranches $dump
    $changed.Add([pscustomobject]@{Method=$dump;FullName=$dump.FullName;Kind='ExcludeClipboardStateFromManualDump';Calls=@();Fields=$skipFields})
    foreach ($method in @(Get-MwbTransferLogMethods -Module $Module)) {
        if (@($method.Body.Instructions | Where-Object { Test-MwbTransferDiagnosticCall $_ }).Count) {
            throw "Transfer diagnostic call remains: $($method.FullName)"
        }
    }
    return [pscustomobject]@{ModifiedMethods=$changed.ToArray();SuppressedCalls=$total;HelperPrefixes=$prefixes;ExcludedDumpFields=$skipFields}
}
