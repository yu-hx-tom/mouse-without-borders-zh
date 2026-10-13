# Build an offline file-transfer candidate from the reviewed modern-UI executable.
# No installed program, service, registry, firewall or pairing configuration is changed.
[CmdletBinding()]
param(
    [string]$Source = (Join-Path $PSScriptRoot 'modern\MouseWithoutBorders.exe'),
    [string]$TransferLibrary = (Join-Path $PSScriptRoot 'transfer\MwbTransfer.dll'),
    [string]$Destination = (Join-Path $PSScriptRoot 'transfer\MouseWithoutBorders.exe'),
    [string]$ReportPath = (Join-Path $PSScriptRoot 'analysis\transfer-verification.json'),
    [string]$ExpectedSourceHash = 'B7F242BC5714BE07E41A32BB5475855D0878100F635546EF04601CD27680D457'
)
$ErrorActionPreference = 'Stop'
if($ExpectedSourceHash -notmatch '^[A-Fa-f0-9]{64}$'){throw 'Invalid source hash'}
Add-Type -Path (Join-Path $PSScriptRoot 'tools\cecil\lib\net40\Mono.Cecil.dll')
$opened = [Collections.Generic.List[Mono.Cecil.ModuleDefinition]]::new()
$script:changed = @{}
$script:expectedBodies = @{}
$script:widened = 0

function ReadModule([string]$path) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Required file missing: $path" }
    $module = [Mono.Cecil.ModuleDefinition]::ReadModule($path)
    $opened.Add($module)
    return $module
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
function FindSignature($type, [string]$name, [string]$returnType, [string[]]$arguments) {
    $method = FindMethod $type $name
    if (-not $method.IsPublic -or -not $method.IsStatic -or $method.ReturnType.FullName -cne $returnType -or
        $method.Parameters.Count -ne $arguments.Count) { throw "Unexpected extension API: $($method.FullName)" }
    for ($n = 0; $n -lt $arguments.Count; $n++) {
        if ($method.Parameters[$n].ParameterType.FullName -cne $arguments[$n]) { throw "Unexpected extension parameter: $($method.FullName)" }
    }
    return $method
}
function AtOffset($method, [int]$offset, [string]$code, [string]$operand = '') {
    $found = @($method.Body.Instructions | Where-Object Offset -EQ $offset)
    if ($found.Count -ne 1 -or [string]$found[0].OpCode.Code -cne $code -or
        ($operand.Length -gt 0 -and [string]$found[0].Operand -cne $operand)) {
        throw "Unexpected audited instruction: $($method.FullName) offset $offset ($code $operand)"
    }
    return $found[0]
}
function NewInstruction([string]$opcode, $operand = $null) {
    $op = [Mono.Cecil.Cil.OpCodes].GetField($opcode).GetValue($null)
    if ($null -eq $operand) { return [Mono.Cecil.Cil.Instruction]::Create($op) }
    return [Mono.Cecil.Cil.Instruction]::Create($op, $operand)
}
function WidenBranches($method) {
    foreach ($instruction in $method.Body.Instructions) {
        if ($instruction.OpCode.OperandType -eq [Mono.Cecil.Cil.OperandType]::ShortInlineBrTarget) {
            $name = ([string]$instruction.OpCode.Code) -replace '_S$', ''
            $field = [Mono.Cecil.Cil.OpCodes].GetField($name)
            if ($null -eq $field) { throw "Cannot expand branch $($instruction.OpCode)" }
            $instruction.OpCode = $field.GetValue($null)
            $script:widened++
        }
    }
}
function BytesKey([byte[]]$bytes) { return [Convert]::ToBase64String($bytes) }
function AttributeKey($provider) {
    return (@($provider.CustomAttributes | ForEach-Object { $_.Constructor.FullName + ':' + (BytesKey $_.GetBlob()) }) -join '|')
}
function InstructionTargetIndex($method, $instruction) {
    if ($null -eq $instruction) { return -1 }
    $index = $method.Body.Instructions.IndexOf($instruction)
    if ($index -lt 0) { throw 'Branch/exception target outside its method body' }
    return $index
}
function OperandKey($method, $operand) {
    if ($null -eq $operand) { return '' }
    if ($operand -is [Mono.Cecil.Cil.Instruction]) { return 'branch:' + (InstructionTargetIndex $method $operand) }
    if ($operand -is [Mono.Cecil.Cil.Instruction[]]) {
        return 'switch:' + (($operand | ForEach-Object { InstructionTargetIndex $method $_ }) -join ',')
    }
    if ($operand -is [Mono.Cecil.Cil.VariableDefinition]) { return 'local:' + $operand.Index + ':' + $operand.VariableType.FullName }
    if ($operand -is [Mono.Cecil.ParameterDefinition]) { return 'arg:' + $operand.Index + ':' + $operand.ParameterType.FullName }
    if ($operand -is [Mono.Cecil.MemberReference]) {
        $scope = if ($operand -is [Mono.Cecil.TypeReference]) { $operand.Scope } else { $operand.DeclaringType.Scope }
        return ([string]$operand) + ' | scope:' + [string]$scope
    }
    return [string]$operand
}
function BodyKey($method) {
    if (-not $method.HasBody) { return 'NO_BODY' }
    $lines = [Collections.Generic.List[string]]::new()
    $lines.Add('InitLocals:' + $method.Body.InitLocals)
    foreach ($v in $method.Body.Variables) { $lines.Add('Local:' + $v.Index + ':' + $v.VariableType.FullName) }
    foreach ($i in $method.Body.Instructions) { $lines.Add(([string]$i.OpCode.Code) + ' ' + (OperandKey $method $i.Operand)) }
    foreach ($h in $method.Body.ExceptionHandlers) {
        $lines.Add('EH:' + $h.HandlerType + ':' + [string]$h.CatchType + ':' +
            (InstructionTargetIndex $method $h.TryStart) + ':' + (InstructionTargetIndex $method $h.TryEnd) + ':' +
            (InstructionTargetIndex $method $h.HandlerStart) + ':' + (InstructionTargetIndex $method $h.HandlerEnd) + ':' +
            (InstructionTargetIndex $method $h.FilterStart))
    }
    return ($lines -join "`n")
}
function CompareType($a, $b) {
    if ($a.FullName -cne $b.FullName -or $a.Attributes -ne $b.Attributes -or [string]$a.BaseType -cne [string]$b.BaseType -or
        $a.Methods.Count -ne $b.Methods.Count -or $a.Fields.Count -ne $b.Fields.Count -or $a.NestedTypes.Count -ne $b.NestedTypes.Count -or
        $a.Properties.Count -ne $b.Properties.Count -or $a.Events.Count -ne $b.Events.Count -or
        (AttributeKey $a) -cne (AttributeKey $b)) { throw "Type metadata changed: $($a.FullName)" }
    for ($i = 0; $i -lt $a.Fields.Count; $i++) {
        $x = $a.Fields[$i]; $y = $b.Fields[$i]
        if ($x.FullName -cne $y.FullName -or $x.Attributes -ne $y.Attributes -or [string]$x.Constant -cne [string]$y.Constant -or
            (BytesKey $x.InitialValue) -cne (BytesKey $y.InitialValue) -or (AttributeKey $x) -cne (AttributeKey $y)) {
            throw "Field changed: $($x.FullName)"
        }
    }
    for ($i = 0; $i -lt $a.Properties.Count; $i++) {
        $x=$a.Properties[$i]; $y=$b.Properties[$i]
        if ($x.FullName -cne $y.FullName -or $x.Attributes -ne $y.Attributes -or [string]$x.GetMethod -cne [string]$y.GetMethod -or
            [string]$x.SetMethod -cne [string]$y.SetMethod -or (AttributeKey $x) -cne (AttributeKey $y)) { throw 'Property metadata changed' }
    }
    for ($i = 0; $i -lt $a.Events.Count; $i++) {
        $x=$a.Events[$i]; $y=$b.Events[$i]
        if ($x.FullName -cne $y.FullName -or $x.Attributes -ne $y.Attributes -or [string]$x.AddMethod -cne [string]$y.AddMethod -or
            [string]$x.RemoveMethod -cne [string]$y.RemoveMethod -or (AttributeKey $x) -cne (AttributeKey $y)) { throw 'Event metadata changed' }
    }
    for ($i = 0; $i -lt $a.Methods.Count; $i++) {
        $x=$a.Methods[$i]; $y=$b.Methods[$i]; $script:methodCount++
        if ($x.FullName -cne $y.FullName -or $x.Attributes -ne $y.Attributes -or $x.ImplAttributes -ne $y.ImplAttributes -or
            $x.HasBody -ne $y.HasBody -or (AttributeKey $x) -cne (AttributeKey $y) -or $x.Parameters.Count -ne $y.Parameters.Count) {
            throw "Method metadata changed: $($x.FullName)"
        }
        for ($n=0; $n -lt $x.Parameters.Count; $n++) {
            $p=$x.Parameters[$n]; $q=$y.Parameters[$n]
            if ($p.Name -cne $q.Name -or $p.Attributes -ne $q.Attributes -or [string]$p.Constant -cne [string]$q.Constant -or
                (AttributeKey $p) -cne (AttributeKey $q)) { throw 'Parameter metadata changed' }
        }
        if (-not $x.HasBody) { continue }
        if ($script:changed.ContainsKey($x.FullName)) {
            if ((BodyKey $y) -cne $script:expectedBodies[$x.FullName]) { throw "Written patch differs from planned instructions: $($x.FullName)" }
            $script:changedCount++
        } else {
            if ((BodyKey $x) -cne (BodyKey $y)) { throw "Unapproved method-body change: $($x.FullName)" }
            $script:unchangedCount++
        }
    }
    for ($i=0; $i -lt $a.NestedTypes.Count; $i++) { CompareType $a.NestedTypes[$i] $b.NestedTypes[$i] }
}

try {
    $sourceHash = (Get-FileHash -LiteralPath $Source -Algorithm SHA256).Hash
    if ($sourceHash -cne $expectedSourceHash) { throw "Unreviewed source executable: $sourceHash" }
    if ([IO.Path]::GetFullPath($Source) -ieq [IO.Path]::GetFullPath($Destination)) { throw 'Source and candidate must be separate files' }
    $module = ReadModule $Source
    $library = ReadModule $TransferLibrary
    if ($library.Assembly.Name.Name -cne 'MwbTransfer' -or -not $library.Assembly.Name.HasPublicKey -or
        $library.Assembly.Name.PublicKeyToken.Length -ne 8 -or -not ($library.Attributes -band [Mono.Cecil.ModuleAttributes]::StrongNameSigned)) {
        throw 'Expected the strong-named MwbTransfer dependency'
    }
    if (@($module.AssemblyReferences | Where-Object Name -CEQ 'MwbTransfer').Count) { throw 'Source already references MwbTransfer' }
    $transfer = FindType $library 'MwbTransfer.Transfer'
    if (-not $transfer.IsPublic) { throw 'MwbTransfer.Transfer must be public' }
    $beginRef = $module.ImportReference((FindSignature $transfer 'BeginDrop' 'System.Void' @('System.String','System.IntPtr','System.Action`1<System.String>')))
    $localDropRef = $module.ImportReference((FindSignature $transfer 'BeginLocalDrop' 'System.Void' @('System.String','System.IntPtr')))
    $sendRef = $module.ImportReference((FindSignature $transfer 'SendFile' 'System.Boolean' @('System.Net.Sockets.Socket','System.IO.Stream','System.String')))
    $receiveRef = $module.ImportReference((FindSignature $transfer 'TryReceiveFile' 'System.Boolean' @('System.String','System.Net.Sockets.Socket','System.IO.Stream','System.IO.Stream','System.String','System.String','System.Int64')))
    $previewBeginRef = $module.ImportReference((FindSignature $transfer 'BeginDragPreview' 'System.Void' @('System.IntPtr')))
    $previewEndRef = $module.ImportReference((FindSignature $transfer 'EndDragPreview' 'System.Void' @()))
    $common = FindType $module 'MouseWithoutBorders.Common'
    $sockets = FindType $module 'MouseWithoutBorders.SocketStuff'
    $drop = FindMethod $common 'DragDropStep10'
    $getPeer = FindMethod $common 'get_LastMachineWithClipboardData'
    $getMain = FindMethod $common 'get_MainForm'
    $request = FindMethod $common 'GetRemoteClipboard'
    $handleRefs = @($module.GetMemberReferences() | Where-Object FullName -CEQ 'System.IntPtr System.Windows.Forms.Control::get_Handle()')
    $actionRefs = @($module.GetMemberReferences() | Where-Object FullName -CEQ 'System.Void System.Action`1<System.String>::.ctor(System.Object,System.IntPtr)')
    if ($handleRefs.Count -ne 1 -or $actionRefs.Count -ne 1 -or -not $getPeer.IsStatic -or -not $getMain.IsStatic -or -not $request.IsStatic) {
        throw 'Unreviewed caller/delegate shape'
    }
    # The audited setter is the only writer of IsDroping. Hook its transitions,
    # not the mouse-move hot path; switching, cancelling and releasing all pass here.
    $dropState = FindMethod $common 'set_IsDroping'
    if (-not $dropState.IsStatic -or $dropState.Parameters.Count -ne 1 -or
        $dropState.Parameters[0].ParameterType.FullName -cne 'System.Boolean' -or
        $dropState.ReturnType.FullName -cne 'System.Void' -or $dropState.Body.Instructions.Count -ne 3 -or
        $dropState.Body.ExceptionHandlers.Count -ne 0) { throw 'Unexpected IsDroping setter shape' }
    [void](AtOffset $dropState 0x0000 'Ldarg_0')
    [void](AtOffset $dropState 0x0001 'Stsfld' 'System.Boolean MouseWithoutBorders.Common::isDroping')
    $stateTail = AtOffset $dropState 0x0006 'Ret'
    $stateTail.OpCode = [Mono.Cecil.Cil.OpCodes]::Ldarg_0
    $stateHandle = NewInstruction 'Callvirt' $handleRefs[0]
    $stateEnd = NewInstruction 'Call' $previewEndRef
    foreach ($new in @((NewInstruction 'Brfalse' $stateEnd),(NewInstruction 'Call' $getMain),
        (NewInstruction 'Dup'),(NewInstruction 'Brtrue' $stateHandle),(NewInstruction 'Pop'),(NewInstruction 'Ret'),
        $stateHandle,(NewInstruction 'Call' $previewBeginRef),(NewInstruction 'Ret'),$stateEnd,(NewInstruction 'Ret'))) {
        $dropState.Body.Instructions.Add($new)
    }
    $script:changed[$dropState.FullName] = 'Start/stop read-only target preview on shared drag state; preserve original field write'
    $oldCount = $drop.Body.Instructions.Count
    $anchor = AtOffset $drop 0x0059 'Ldstr' 'desktop'
    $oldRequest = AtOffset $drop 0x005e 'Call' $request.FullName
    $getDragMachine = FindMethod $common 'get_DragMachine'
    $getMachine = FindMethod $common 'get_MachineID'
    $getDragFile = FindMethod $common 'get_LastDragDropFile'
    $emptyRefs=@($module.GetMemberReferences() | Where-Object FullName -CEQ 'System.Boolean System.String::IsNullOrEmpty(System.String)')
    if($emptyRefs.Count -ne 1) { throw 'Unexpected legacy string API reference' }
    $emptyRef=$emptyRefs[0]
    $remoteDispatch = NewInstruction 'Call' $getPeer
    $anchor.OpCode = [Mono.Cecil.Cil.OpCodes]::Call; $anchor.Operand = $getDragMachine
    $cursor = $anchor; $il = $drop.Body.GetILProcessor()
    foreach ($new in @((NewInstruction 'Call' $getMachine),(NewInstruction 'Bne_Un' $remoteDispatch),
        (NewInstruction 'Call' $getDragFile),(NewInstruction 'Call' $emptyRef),(NewInstruction 'Brtrue' $remoteDispatch),
        (NewInstruction 'Call' $getDragFile),(NewInstruction 'Call' $getMain),(NewInstruction 'Callvirt' $handleRefs[0]),
        (NewInstruction 'Call' $localDropRef),(NewInstruction 'Ret'),$remoteDispatch,
        (NewInstruction 'Call' $getMain), (NewInstruction 'Callvirt' $handleRefs[0]),
        (NewInstruction 'Ldnull'), (NewInstruction 'Ldftn' $request), (NewInstruction 'Newobj' $actionRefs[0]))) {
        $il.InsertAfter($cursor,$new); $cursor=$new
    }
    $oldRequest.Operand = $beginRef
    WidenBranches $drop
    if ($drop.Body.Instructions.Count -ne $oldCount+16) { throw 'Unexpected drop instruction count' }
    $script:changed[$drop.FullName] = 'Fresh target dispatch; returning source uses local copy instead of requesting its own clipboard'

    # Native OLE ends after the initial helper capture. Keep the existing logical
    # drag until a real release; source-screen return is another target change.
    $getDragging=FindMethod $common 'get_IsDragging'
    $setDragging=FindMethod $common 'set_IsDragging'
    $getDropping=FindMethod $common 'get_IsDroping'
    $getDown=FindMethod $common 'get_MouseDown'
    $setDown=FindMethod $common 'set_MouseDown'
    $changeDrop=FindMethod $common 'ChangeDropMachine'
    $returnDrag=FindMethod $common 'DragDropStep11'
    [void](AtOffset $returnDrag 0x0029 'Call' 'System.Void MouseWithoutBorders.Common::set_LastDragDropFile(System.String)')
    $returnDrag.Body=[Mono.Cecil.Cil.MethodBody]::new($returnDrag)
    $resume=NewInstruction 'Ldc_I4_1'; $done=NewInstruction 'Ret'
    foreach($new in @((NewInstruction 'Call' $getDragging),(NewInstruction 'Brtrue' $resume),
        (NewInstruction 'Call' $getDropping),(NewInstruction 'Brfalse' $done),$resume,
        (NewInstruction 'Call' $setDragging),(NewInstruction 'Call' $changeDrop),$done)) { $returnDrag.Body.Instructions.Add($new) }
    $script:changed[$returnDrag.FullName]='Continue captured drag on source return; preserve file while an asynchronous drop request is pending'

    $newDestination=@($common.Fields | Where-Object Name -CEQ 'newDesMachineID')[0]
    $dropDestination=@($common.Fields | Where-Object Name -CEQ 'dropMachineID')[0]
    $sendEnd=FindMethod $common 'SendClipboardBeatDragDropEnd'
    $sendBegin=FindMethod $common 'SendDropBegin'
    [void](AtOffset $changeDrop 0x002a 'Stsfld' $dropDestination.FullName)
    $changeDrop.Body=[Mono.Cecil.Cil.MethodBody]::new($changeDrop)
    $localTarget=NewInstruction 'Ldc_I4_1'
    foreach($new in @((NewInstruction 'Ldc_I4_0'),(NewInstruction 'Call' $dropState),
        (NewInstruction 'Call' $sendEnd),(NewInstruction 'Ldsfld' $newDestination),(NewInstruction 'Stsfld' $dropDestination),
        (NewInstruction 'Ldsfld' $newDestination),(NewInstruction 'Call' $getMachine),(NewInstruction 'Beq' $localTarget),
        (NewInstruction 'Call' $sendBegin),(NewInstruction 'Ret'),$localTarget,(NewInstruction 'Call' $dropState),(NewInstruction 'Ret'))) {
        $changeDrop.Body.Instructions.Add($new)
    }
    $script:changed[$changeDrop.FullName]='Deactivate old local preview and update drop destination on every local or remote switch'

    # The helper can complete after the cursor has already returned to this PC.
    # The old SendPackage path has no loopback delivery for a local destination.
    $beginStart=AtOffset $sendBegin 0x0000 'Ldstr' 'SendDropBegin...'
    $beginIl=$sendBegin.Body.GetILProcessor()
    foreach($new in @((NewInstruction 'Ldsfld' $dropDestination),(NewInstruction 'Call' $getMachine),
        (NewInstruction 'Bne_Un' $beginStart),(NewInstruction 'Ldc_I4_1'),
        (NewInstruction 'Call' $dropState),(NewInstruction 'Ret'))) { $beginIl.InsertBefore($beginStart,$new) }
    WidenBranches $sendBegin
    $script:changed[$sendBegin.FullName]='Dispatch a late helper capture locally when its target is this machine'

    $acceptDrop=FindMethod $common 'DragDropStep08_2'
    $acceptStart=AtOffset $acceptDrop 0x001b 'Ldc_I4_1'
    $getDestination=FindMethod $common 'get_DesMachineID'
    $acceptIl=$acceptDrop.Body.GetILProcessor()
    foreach($new in @((NewInstruction 'Call' $getDown),(NewInstruction 'Brfalse' $acceptStart),
        (NewInstruction 'Call' $getDestination),(NewInstruction 'Call' $getMachine),(NewInstruction 'Beq' $acceptStart),
        (NewInstruction 'Ldc_I4_1'),(NewInstruction 'Call' $setDragging),
        (NewInstruction 'Call' $getDestination),(NewInstruction 'Stsfld' $newDestination),
        (NewInstruction 'Call' $changeDrop),(NewInstruction 'Ret'))) { $acceptIl.InsertBefore($acceptStart,$new) }
    WidenBranches $acceptDrop
    $script:changed[$acceptDrop.FullName]='Reroute a delayed remote-source capture to the controller current screen while its button remains held'

    $switch=FindMethod $common 'PrepareToSwitchToMachine'
    $pullStart=AtOffset $switch 0x0156 'Call' 'System.Int64 MouseWithoutBorders.Common::GetTick()'
    $switchCounter=AtOffset $switch 0x0188 'Ldsflda' 'System.Int32 MouseWithoutBorders.Common::switchCount'
    $switchIl=$switch.Body.GetILProcessor()
    foreach($new in @((NewInstruction 'Call' $getDragging),(NewInstruction 'Brtrue' $switchCounter),
        (NewInstruction 'Call' $getDropping),(NewInstruction 'Brtrue' $switchCounter),
        (NewInstruction 'Call' $getDown),(NewInstruction 'Brtrue' $switchCounter))) { $switchIl.InsertBefore($pullStart,$new) }
    # machineswitched (77) asks the remote target to auto-pull the clipboard too.
    # Guard the branch target itself, including the interval before helper capture.
    $notifyStart=AtOffset $switch 0x017c 'Ldsfld' 'MouseWithoutBorders.ID MouseWithoutBorders.Common::desMachineID'
    $oldDestination=$notifyStart.Operand
    $notifyStart.OpCode=[Mono.Cecil.Cil.OpCodes]::Call; $notifyStart.Operand=$getDragging
    $notifyCursor=$notifyStart
    foreach($new in @((NewInstruction 'Brtrue' $switchCounter),(NewInstruction 'Call' $getDropping),
        (NewInstruction 'Brtrue' $switchCounter),(NewInstruction 'Call' $getDown),
        (NewInstruction 'Brtrue' $switchCounter),(NewInstruction 'Ldsfld' $oldDestination))) {
        $switchIl.InsertAfter($notifyCursor,$new); $notifyCursor=$new
    }
    WidenBranches $switch
    $script:changed[$switch.FullName]='Suppress local and remote switch-triggered clipboard pulls during active or not-yet-captured held drags'

    $press=FindMethod $common 'DragDropStep01'
    $pressStart=AtOffset $press 0x0010 'Ldc_I4_1'
    $pressIl=$press.Body.GetILProcessor()
    foreach($new in @((NewInstruction 'Ldc_I4_0'),(NewInstruction 'Call' $setDragging),
        (NewInstruction 'Ldc_I4_0'),(NewInstruction 'Call' $dropState))) { $pressIl.InsertBefore($pressStart,$new) }
    $cancelCheck=AtOffset $press 0x0054 'Call' $getDropping.FullName
    $cancelBranch=AtOffset $press 0x0059 'Brfalse_S'
    # A right release must end a controller-only drag too, not only a receiver preview.
    $cancelCheck.OpCode=[Mono.Cecil.Cil.OpCodes]::Call; $cancelCheck.Operand=$sendEnd
    $cancelBranch.OpCode=[Mono.Cecil.Cil.OpCodes]::Ldc_I4_0; $cancelBranch.Operand=$null
    $cancelCursor=$cancelBranch
    foreach($new in @((NewInstruction 'Call' $setDragging),(NewInstruction 'Ldc_I4_0'),(NewInstruction 'Call' $setDown))) {
        $pressIl.InsertAfter($cancelCursor,$new); $cancelCursor=$new
    }
    WidenBranches $press
    $script:changed[$press.FullName]='Reset logical gesture on a new press and clear both drag/drop flags on right-button cancellation'

    $capture=FindMethod $common 'DragDropStep05Ex'
    $captureStart=AtOffset $capture 0x0000 'Newobj' 'System.Void MouseWithoutBorders.Common/<>c__DisplayClass261_0::.ctor()'
    $captureIl=$capture.Body.GetILProcessor()
    foreach($new in @((NewInstruction 'Call' $getDown),(NewInstruction 'Brtrue' $captureStart),
        (NewInstruction 'Call' $getDropping),(NewInstruction 'Brtrue' $captureStart),
        (NewInstruction 'Ldnull'),(NewInstruction 'Starg' $capture.Parameters[0]))) { $captureIl.InsertBefore($captureStart,$new) }
    WidenBranches $capture
    $script:changed[$capture.FullName]='Reject a helper result after logical release while still acknowledging and hiding the helper'

    $send = FindMethod $sockets 'SendFileEx'
    if (-not $send.IsStatic -or $send.Parameters.Count -ne 3 -or $send.ReturnType.FullName -cne 'System.Boolean') { throw 'Unexpected SendFileEx shape' }
    $send.Body = [Mono.Cecil.Cil.MethodBody]::new($send)
    foreach ($new in @((NewInstruction 'Ldarg_0'),(NewInstruction 'Ldarg_1'),(NewInstruction 'Ldarg_2'),
        (NewInstruction 'Call' $sendRef),(NewInstruction 'Ret'))) { $send.Body.Instructions.Add($new) }
    $script:changed[$send.FullName] = 'Forward file payload to progress-aware sender; handshake unchanged'

    $receive = FindMethod $common 'ReceiveAndProcessClipboardData'
    if (-not $receive.IsStatic -or $receive.Parameters.Count -ne 5 -or $receive.Body.Variables.Count -ne 17 -or
        $receive.Body.Variables[5].VariableType.FullName -cne 'System.String' -or $receive.Body.Variables[8].VariableType.FullName -cne 'System.Int64') {
        throw 'Unexpected receive locals/signature'
    }
    $oldCount = $receive.Body.Instructions.Count
    $continue = AtOffset $receive 0x01d5 'Call' 'System.Globalization.CultureInfo System.Globalization.CultureInfo::get_CurrentCulture()'
    $return = AtOffset $receive 0x077a 'Ret'
    [void](AtOffset $receive 0x01d3 'Stloc_S' 'V_5')
    $il = $receive.Body.GetILProcessor()
    foreach ($new in @((NewInstruction 'Ldarg_0'),(NewInstruction 'Ldarg_1'),(NewInstruction 'Ldarg_2'),(NewInstruction 'Ldarg_3'),
        (NewInstruction 'Ldarg_S' $receive.Parameters[4]),(NewInstruction 'Ldloc_S' $receive.Body.Variables[5]),
        (NewInstruction 'Ldloc_S' $receive.Body.Variables[8]),(NewInstruction 'Call' $receiveRef),
        (NewInstruction 'Brfalse' $continue),(NewInstruction 'Leave' $return))) { $il.InsertBefore($continue,$new) }
    WidenBranches $receive
    if ($receive.Body.Instructions.Count -ne $oldCount+10) { throw 'Unexpected receive instruction count' }
    $script:changed[$receive.FullName] = 'Dispatch parsed desktop files to atomic receiver; other clipboard paths unchanged'
    . (Join-Path $PSScriptRoot 'suppress-transfer-logs.ps1')
    $privacy = Suppress-MwbTransferLogs -Module $module
    # Step11 was replaced above, removing its one old cancellation log call.
    if ($privacy.SuppressedCalls -ne 90) { throw 'Unexpected transfer diagnostic call count' }
    foreach ($entry in $privacy.ModifiedMethods) {
        if ($script:changed.ContainsKey($entry.FullName)) { $script:changed[$entry.FullName] += '; ' + $entry.Kind }
        else { $script:changed[$entry.FullName] = $entry.Kind }
        $script:expectedBodies[$entry.FullName] = BodyKey $entry.Method
    }
    foreach ($method in @($drop,$send,$receive,$dropState,$returnDrag,$changeDrop,$switch,$press,$capture,$sendBegin,$acceptDrop)) { $script:expectedBodies[$method.FullName] = BodyKey $method }
    [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($Destination)))
    $module.Write($Destination)

    $before=ReadModule $Source; $after=ReadModule $Destination
    if ($before.Assembly.Name.FullName -cne $after.Assembly.Name.FullName -or $before.Mvid -ne $after.Mvid -or
        $before.Kind -ne $after.Kind -or $before.Architecture -ne $after.Architecture -or $before.Attributes -ne $after.Attributes -or
        [string]$before.EntryPoint -cne [string]$after.EntryPoint -or $before.Types.Count -ne $after.Types.Count -or
        (AttributeKey $before.Assembly) -cne (AttributeKey $after.Assembly) -or (AttributeKey $before) -cne (AttributeKey $after)) {
        throw 'Assembly identity/module metadata changed'
    }
    $beforeRefs=@($before.AssemblyReferences|ForEach-Object FullName); $afterRefs=@($after.AssemblyReferences|ForEach-Object FullName)
    if ($afterRefs.Count -ne $beforeRefs.Count+1) { throw 'Unexpected assembly reference count' }
    foreach ($ref in $beforeRefs) { if ($afterRefs -cnotcontains $ref) { throw "Assembly reference changed: $ref" } }
    $newRefs=@($after.AssemblyReferences | Where-Object {$beforeRefs -cnotcontains $_.FullName})
    if ($newRefs.Count -ne 1 -or $newRefs[0].FullName -cne $library.Assembly.Name.FullName) { throw 'Unexpected added dependency' }
    $script:methodCount=0; $script:changedCount=0; $script:unchangedCount=0
    for ($i=0; $i -lt $before.Types.Count; $i++) { CompareType $before.Types[$i] $after.Types[$i] }
    if ($script:changedCount -ne $script:changed.Count) { throw 'Approved transfer/privacy method count mismatch' }
    if ($before.Resources.Count -ne $after.Resources.Count) { throw 'Resource count changed' }
    for ($i=0; $i -lt $before.Resources.Count; $i++) {
        $a=$before.Resources[$i]; $b=$after.Resources[$i]
        if ($a.Name -cne $b.Name -or $a.Attributes -ne $b.Attributes -or $a.ResourceType -ne $b.ResourceType -or
            $a -isnot [Mono.Cecil.EmbeddedResource] -or (BytesKey $a.GetResourceData()) -cne (BytesKey $b.GetResourceData())) {
            throw "Resource changed: $($a.Name)"
        }
    }
    $report=[ordered]@{
        Pass=$true; Source=$Source; Candidate=$Destination; TransferLibrary=$TransferLibrary
        AssemblyIdentity=$after.Assembly.Name.FullName; AddedAssemblyReference=$newRefs[0].FullName
        TransferLibraryHasStrongName=$true; VerifiedMethods=$script:methodCount; UnchangedMethodBodies=$script:unchangedCount
        ChangedMethods=@($script:changed.Keys | Sort-Object | ForEach-Object {@{Method=$_;Change=$script:changed[$_]}})
        ExpandedShortBranches=$script:widened; UnchangedEmbeddedResources=$before.Resources.Count
        SourceSHA256=$sourceHash; CandidateSHA256=(Get-FileHash -LiteralPath $Destination -Algorithm SHA256).Hash
        TransferLibrarySHA256=(Get-FileHash -LiteralPath $TransferLibrary -Algorithm SHA256).Hash
        SuppressedTransferDiagnosticCalls=$privacy.SuppressedCalls
        FilteredHelperLogPrefixes=$privacy.HelperPrefixes; ExcludedManualDumpFields=$privacy.ExcludedDumpFields
        OriginalMicrosoftSignaturePreserved=$false; RuntimeVerified=$false; LiveFilesModified=$false
    }
    $report | ConvertTo-Json -Depth 7 | Set-Content -LiteralPath $ReportPath -Encoding utf8
    $report | ConvertTo-Json -Depth 7
} catch {
    [ordered]@{Pass=$false;Error=$_.Exception.Message;Source=$Source;Candidate=$Destination;LiveFilesModified=$false} |
        ConvertTo-Json | Set-Content -LiteralPath $ReportPath -Encoding utf8
    throw
} finally {
    foreach ($item in $opened) { $item.Dispose() }
}
