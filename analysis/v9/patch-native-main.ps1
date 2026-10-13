$ErrorActionPreference='Stop'
$root=Split-Path (Split-Path $PSScriptRoot)
$out=Join-Path $PSScriptRoot 'candidate'
Add-Type -Path "$root\tools\cecil\lib\net40\Mono.Cecil.dll"
$m=[Mono.Cecil.ModuleDefinition]::ReadModule("$out\MouseWithoutBorders.base.exe")
$n=[Mono.Cecil.ModuleDefinition]::ReadModule("$out\MwbNativeDrag.dll")
$t=[Mono.Cecil.ModuleDefinition]::ReadModule("$out\MwbTransfer.dll")
function Types($ts){foreach($type in $ts){$type;Types $type.NestedTypes}}
function Method($type,$name){$a=@($type.Methods|Where-Object Name -CEQ $name);if($a.Count -ne 1){throw "Ambiguous $($type.FullName) $name"};$a[0]}
function I($name,$operand=$null){$op=[Mono.Cecil.Cil.OpCodes].GetField($name).GetValue($null);if($null -eq $operand){[Mono.Cecil.Cil.Instruction]::Create($op)}else{[Mono.Cecil.Cil.Instruction]::Create($op,$operand)}}
function Ref($name){$m.ImportReference((Method $runtime $name))}
function TRef($name){$m.ImportReference((Method $transfer $name))}
function Mark($method){$script:edited[$method.FullName]=$method;foreach($i in $method.Body.Instructions){if($i.OpCode.OperandType -eq 'ShortInlineBrTarget'){$i.OpCode=[Mono.Cecil.Cil.OpCodes].GetField(([string]$i.OpCode.Code -replace '_S$','')).GetValue($null)}}}
function ReplaceBody($method,$instructions){$method.Body=[Mono.Cecil.Cil.MethodBody]::new($method);foreach($i in $instructions){$method.Body.Instructions.Add($i)};Mark $method}
function InsertAt($method,$anchor,$instructions){
    $copy=I 'Nop';$copy.OpCode=$anchor.OpCode;$copy.Operand=$anchor.Operand
    $anchor.OpCode=[Mono.Cecil.Cil.OpCodes]::Nop;$anchor.Operand=$null
    $il=$method.Body.GetILProcessor();$last=$anchor
    foreach($i in @($instructions)+@($copy)){$il.InsertAfter($last,$i);$last=$i}
    Mark $method
}
function CallSite($method,$name){$a=@($method.Body.Instructions|Where-Object {$_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.Name -ceq $name});if($a.Count -ne 1){throw "Ambiguous call $name in $($method.FullName)"};$a[0]}
function Body($method){if(!$method.HasBody){return 'NO_BODY'};(@($method.Body.Variables|ForEach-Object {"local:$_"})+@($method.Body.Instructions|ForEach-Object {"$_"})+@($method.Body.ExceptionHandlers|ForEach-Object {"$($_.HandlerType):$($_.TryStart):$($_.TryEnd):$($_.HandlerStart):$($_.HandlerEnd)"})) -join "`n"}
$script:edited=@{}
try {
    $all=@(Types $m.Types);$common=$all|Where-Object FullName -CEQ 'MouseWithoutBorders.Common'
    $runtime=$n.Types|Where-Object FullName -CEQ 'MwbTransfer.NativeDragRuntime'
    $transfer=$t.Types|Where-Object FullName -CEQ 'MwbTransfer.Transfer'
    $sockets=$all|Where-Object FullName -CEQ 'MouseWithoutBorders.SocketStuff'
    $hook=$all|Where-Object FullName -CEQ 'MouseWithoutBorders.InputHook'
    $sim=$all|Where-Object FullName -CEQ 'MouseWithoutBorders.InputSimu'
    $data=$all|Where-Object FullName -CEQ 'MouseWithoutBorders.DATA'
    $before=@{};foreach($method in $all.Methods){$before[$method.FullName]=Body $method}

    ReplaceBody (Method $common 'DragDropStep01') @((I 'Ldarg_0'),(I 'Call' (Ref 'LocalMouse')),(I 'Ret'))
    ReplaceBody (Method $common 'DragDropStep05Ex') @((I 'Ldarg_0'),(I 'Call' (Ref 'CaptureReply')),(I 'Ret'))
    foreach($name in @('DragDropStep02','DragDropStep03','DragDropStep04','DragDropStep06','DragDropStep08','DragDropStep08_2','DragDropStep09','DragDropStep10','DragDropStep11','DragDropStep12','ChangeDropMachine','SendDropBegin')){ReplaceBody (Method $common $name) @((I 'Ret'))}
    $dropField=$common.Fields|Where-Object Name -CEQ 'isDroping'
    if(!$dropField){throw 'Missing audited drop field'}
    ReplaceBody (Method $common 'set_IsDroping') @((I 'Ldarg_0'),(I 'Stsfld' $dropField),(I 'Ret'))

    $switch=Method $common 'PrepareToSwitchToMachine'
    $store=@($switch.Body.Instructions|Where-Object {$_.OpCode.Code -eq 'Stsfld' -and $_.Operand.Name -ceq 'newDesMachineID'})
    if($store.Count -ne 1){throw 'Missing audited switch anchor'}
    $anchor=$store[0].Previous;$resume=I 'Nop'
    InsertAt $switch $anchor @((I 'Ldarg_0'),(I 'Conv_U4'),(I 'Call' (Ref 'BeforeSwitch')),(I 'Brtrue' $resume),(I 'Ret'),$resume)

    $process=Method $common 'ProcessPackage';$pre=CallSite $process 'PreProcess'
    $anchor=$pre.Next.Operand;$resume=I 'Nop'
    InsertAt $process $anchor @((I 'Ldarg_0'),(I 'Callvirt' (Method $data 'get_Bytes')),(I 'Ldarg_1'),(I 'Call' (Ref 'Packet')),(I 'Brfalse' $resume),(I 'Ret'),$resume)
    $step=CallSite $process 'DragDropStep01';$step.OpCode=[Mono.Cecil.Cil.OpCodes]::Pop;$step.Operand=$null

    $mouse=Method $hook 'MouseHookProc';$first=$mouse.Body.Instructions[0]
    $normal=I 'Nop';$pass=I 'Nop'
    $callNext=@($mouse.Body.Instructions|Where-Object {$_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.Name -ceq 'CallNextHookEx'})[0].Operand
    $handle=$hook.Fields|Where-Object Name -CEQ 'hMouseHook'
    InsertAt $mouse $first @((I 'Ldarg_1'),(I 'Ldarg_2'),(I 'Ldarg_3'),(I 'Call' (Ref 'InspectMouse')),(I 'Dup'),(I 'Brfalse' $normal),(I 'Ldc_I4_1'),(I 'Beq' $pass),(I 'Ldc_I4_1'),(I 'Ret'),$pass,(I 'Ldarg_0'),(I 'Ldfld' $handle),(I 'Ldarg_1'),(I 'Ldarg_2'),(I 'Ldarg_3'),(I 'Call' $callNext),(I 'Ret'),$normal,(I 'Pop'))
    $real=CallSite $mouse 'get_RealData';$real.OpCode=[Mono.Cecil.Cil.OpCodes]::Ldc_I4_1;$real.Operand=$null
    # No untagged physical UP may consume a legacy injection counter.
    foreach($i in $mouse.Body.Instructions){if($i.Operand -is [Mono.Cecil.MethodReference] -and $i.Operand.Name -ceq 'get_SkipMouseUpCount'){$i.OpCode=[Mono.Cecil.Cil.OpCodes]::Ldc_I4_0;$i.Operand=$null}}

    $input=Method $sim 'SendInputEx'
    $inputType=$all|Where-Object FullName -CEQ 'MouseWithoutBorders.NativeMethods/INPUT'
    $mouseType=$all|Where-Object FullName -CEQ 'MouseWithoutBorders.NativeMethods/MOUSEINPUT'
    $kind=$inputType.Fields|Where-Object Name -CEQ 'type';$mi=$inputType.Fields|Where-Object Name -CEQ 'mi';$extra=$mouseType.Fields|Where-Object Name -CEQ 'dwExtraInfo'
    $resume=I 'Nop'
    # v9.0.0.11 (Codex keyboard hotfix): mouse injection is identified by NormalMouseTag, so
    # the hook no longer resets the shared InputHook.RealData flag that MoveMouse* clear.
    # KeyboardHookProc still reads it; restore it here or physical keys stop being forwarded.
    $realSet=Method $hook 'set_RealData'
    InsertAt $input $input.Body.Instructions[0] @((I 'Ldarg_0'),(I 'Ldfld' $kind),(I 'Brtrue' $resume),(I 'Ldarga' $input.Parameters[0]),(I 'Ldflda' $mi),(I 'Call' (Ref 'GetNormalMouseTag')),(I 'Stfld' $extra),(I 'Ldc_I4_1'),(I 'Call' $realSet),$resume)
    $availability=@($input.Body.Instructions|Where-Object {$_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.Name -ceq 'get_InjectMouseInputAvailable'})
    if($availability.Count -ne 2){throw 'Unexpected injection fallback layout'}
    foreach($i in $availability){$i.OpCode=[Mono.Cecil.Cil.OpCodes]::Ldc_I4_0;$i.Operand=$null}
    $sendClosure=@($all.Methods|Where-Object Name -CEQ '<SendMouse>b__0')[0]
    if(!$sendClosure){throw 'Missing SendMouse closure'}
    $real=CallSite $sendClosure 'set_RealData';$real.Previous.OpCode=[Mono.Cecil.Cil.OpCodes]::Ldc_I4_1;Mark $sendClosure
    $keyboard=Method $hook 'KeyboardHookProc'
    InsertAt $keyboard $keyboard.Body.Instructions[0] @((I 'Ldarg_1'),(I 'Ldarg_2'),(I 'Ldarg_3'),(I 'Call' (Ref 'Keyboard')))

    $hand=Method $common 'ShakeHand';$bytes=CallSite $hand 'get_Bytes'
    InsertAt $hand $bytes.Next @((I 'Ldarg_1'),(I 'Call' (TRef 'StampNativeHandshake')))
    $post=@($hand.Body.Instructions|Where-Object {$_.OpCode.Code -eq 'Stind_I4'})
    if($post.Count -ne 1 -or $post[0].Next.OpCode.Code -ne 'Ldc_I4_1'){throw 'Unexpected handshake acceptance anchor'}
    $accept=$post[0].Next;$accept.OpCode=[Mono.Cecil.Cil.OpCodes]::Nop;$accept.Operand=$null
    InsertAt $hand $accept @((I 'Ldarg_1'),(I 'Ldloc' $hand.Body.Variables[4]),(I 'Ldarg_0'),(I 'Ldind_Ref'),(I 'Call' (TRef 'AcceptNativeHandshake')))

    $serve=Method $sockets 'SendOrReceiveClipboardData'
    foreach($name in @('set_IsDroping','set_IsDragging','set_DragMachine')){$call=CallSite $serve $name;$call.OpCode=[Mono.Cecil.Cil.OpCodes]::Pop;$call.Operand=$null}
    $shake=CallSite $serve 'ShakeHand';$anchor=$shake.Next.Operand
    $resume=I 'Nop';$ret=@($serve.Body.Instructions|Where-Object OpCode -EQ ([Mono.Cecil.Cil.OpCodes]::Ret))[-1]
    InsertAt $serve $anchor @((I 'Ldarg_0'),(I 'Ldloc_1'),(I 'Ldloc_2'),(I 'Ldloc_0'),(I 'Call' (TRef 'ServeNativeSocket')),(I 'Brfalse' $resume),(I 'Leave' $ret),$resume)

    $destination="$out\MouseWithoutBorders.exe";$m.Write($destination)
    $check=[Mono.Cecil.ModuleDefinition]::ReadModule($destination)
    try {
        $unchanged=0
        foreach($method in (Types $check.Types).Methods){if($script:edited.ContainsKey($method.FullName)){continue};if((Body $method) -cne $before[$method.FullName]){throw "Unexpected modification $($method.FullName)"};$unchanged++}
        # v9.0.0.11 guard: the keyboard hook must never be left with RealData=false.
        # Only KeyboardHookProc may read the flag. Every 'RealData=false' must be followed,
        # with no branch in between, by a mouse SendInputEx (whose prefix restores it), or
        # (SendKey, which injects a KEY) by an explicit 'RealData=true' later in the body.
        $readers=@();$clears=0
        foreach($method in (Types $check.Types).Methods){
            if(!$method.HasBody){continue};$ins=$method.Body.Instructions;$isKey=$method.Name -ceq 'SendKey'
            for($k=1;$k -lt $ins.Count;$k++){
                $op=$ins[$k].Operand;if($op -isnot [Mono.Cecil.MethodReference]){continue}
                if($op.Name -ceq 'get_RealData'){$readers+=$method.Name}
                if($op.Name -ceq 'set_RealData' -and $ins[$k-1].OpCode.Code -eq 'Ldc_I4_0'){
                    $clears++;$ok=$false
                    for($j=$k+1;$j -lt $ins.Count;$j++){
                        $x=$ins[$j];$call=if($x.Operand -is [Mono.Cecil.MethodReference]){$x.Operand.Name}else{''}
                        if($call -ceq 'set_RealData' -and $x.Previous.OpCode.Code -eq 'Ldc_I4_1'){$ok=$true;break}
                        if($call -ceq 'SendInputEx' -and !$isKey){$ok=$true;break}
                        if(!$isKey -and $x.OpCode.FlowControl -in 'Branch','Cond_Branch','Return','Throw'){break}
                    }
                    if(!$ok){throw "RealData cleared without a guaranteed reset in $($method.DeclaringType.Name)::$($method.Name)"}
                }
            }
        }
        if(@($readers|Select-Object -Unique) -join ',' -cne 'KeyboardHookProc'){throw "Unexpected RealData readers: $($readers -join ',')"}
        $sendInput=(Types $check.Types|Where-Object FullName -CEQ 'MouseWithoutBorders.InputSimu').Methods|Where-Object Name -CEQ 'SendInputEx'
        $prefix=@($sendInput.Body.Instructions|Select-Object -First 10|ForEach-Object {("$($_.OpCode.Code) "+$(if($_.Operand -is [Mono.Cecil.MemberReference]){$_.Operand.Name})).Trim()})
        if(($prefix -join ';') -cnotmatch 'Ldfld type;Brtrue;Ldarga;Ldflda mi;Call GetNormalMouseTag;Stfld dwExtraInfo;Ldc_I4_1;Call set_RealData'){throw "SendInputEx mouse prefix does not restore RealData: $($prefix -join ';')"}
        Write-Output "RealData guard: $clears clear sites, each followed by a reset; only KeyboardHookProc reads the flag."
    } finally {$check.Dispose()}
    $names=@($script:edited.Values|ForEach-Object {"$($_.DeclaringType.FullName)|$($_.Name)"})
    $names|Set-Content -LiteralPath "$out\jit-methods.txt" -Encoding utf8
    [ordered]@{Scope='Offline integrated candidate. Not installed. Dual-PC runtime unverified.';ChangedMethods=$names;UnchangedMethods=$unchanged;SHA256=(Get-FileHash $destination).Hash}|ConvertTo-Json -Depth 4|Set-Content "$out\native-verification.json" -Encoding utf8
    Write-Output "Main integration: $($script:edited.Count) methods patched, $unchanged unchanged."
} finally {$m.Dispose();$n.Dispose();$t.Dispose()}
