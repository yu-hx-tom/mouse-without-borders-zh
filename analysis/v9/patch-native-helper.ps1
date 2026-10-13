$ErrorActionPreference='Stop'
$root=Split-Path (Split-Path $PSScriptRoot)
$out=Join-Path $PSScriptRoot 'candidate'
$dll=Join-Path $out 'MwbNativeDrag.dll'
Add-Type -LiteralPath "$root\tools\cecil\lib\net40\Mono.Cecil.dll"
$source=Join-Path $root 'original\MouseWithoutBordersHelper.exe'
if((Get-FileHash -LiteralPath $source).Hash -cne 'D7E8F30B3F87373E89E8DEC1273F161C478E621E2450279A01A3D0914D754B4F'){throw 'Unaudited original helper'}
$module=[Mono.Cecil.ModuleDefinition]::ReadModule($source)
$lib=[Mono.Cecil.ModuleDefinition]::ReadModule($dll)
function AllTypes($types) { foreach($type in $types) { $type;AllTypes $type.NestedTypes } }
function Body($method) {
    if(!$method.HasBody){return 'NO_BODY'}
    # String forms preserve local/member signatures and branch offsets; unmodified
    # method bodies have no added instructions or branch widening in this patch.
    return (@($method.Body.Variables | ForEach-Object {"local:$($_.VariableType)"})+@($method.Body.Instructions | ForEach-Object {"$_"})+@($method.Body.ExceptionHandlers | ForEach-Object {"$($_.HandlerType):$($_.TryStart):$($_.TryEnd):$($_.HandlerStart):$($_.HandlerEnd):$($_.CatchType)"})) -join "`n"
}
function I([string]$name,$operand=$null) {
    $op=[Mono.Cecil.Cil.OpCodes].GetField($name).GetValue($null)
    if($null -eq $operand){return [Mono.Cecil.Cil.Instruction]::Create($op)}
    return [Mono.Cecil.Cil.Instruction]::Create($op,$operand)
}
try {
    $types=@(AllTypes $module.Types)
    $form=@($types | Where-Object Name -CEQ 'FormHelper')
    if($form.Count -ne 1){throw 'Unexpected helper form'}
    $form=$form[0]
    $enter=@($form.Methods | Where-Object Name -CEQ 'FormHelper_DragEnter')[0]
    $wnd=@($form.Methods | Where-Object Name -CEQ 'WndProc')[0]
    $remote=@($form.Fields | Where-Object Name -CEQ 'remoteClipboardHelper')[0]
    $send=@($module.GetMemberReferences() | Where-Object { $_.Name -ceq 'SendDragFile' })
    if($send.Count -eq 0){$send=@($types.Methods | Where-Object { $_.Name -ceq 'SendDragFile' })}
    if($enter.Parameters.Count -ne 2 -or $wnd.Parameters.Count -ne 1 -or !$remote -or $send.Count -ne 1){throw 'Unexpected helper IPC shape'}
    $ext=@($lib.Types | Where-Object FullName -CEQ 'MwbTransfer.HelperNativeDrag')[0]
    $onEnter=$module.ImportReference(@($ext.Methods | Where-Object Name -CEQ 'OnDragEnter')[0])
    $onMessage=$module.ImportReference(@($ext.Methods | Where-Object Name -CEQ 'HandleMessage')[0])
    $ownsWindow=$module.ImportReference(@($ext.Methods | Where-Object Name -CEQ 'OwnsWindow')[0])
    # Import framework delegate from the helper's existing v4 mscorlib scope.
    $action=[Mono.Cecil.TypeReference]::new('System','Action`1',$module,$module.TypeSystem.CoreLibrary)
    $closed=[Mono.Cecil.GenericInstanceType]::new($action)
    $closed.GenericArguments.Add($module.TypeSystem.String)
    $ctor=[Mono.Cecil.MethodReference]::new('.ctor',$module.TypeSystem.Void,$closed)
    $ctor.HasThis=$true
    $ctor.Parameters.Add([Mono.Cecil.ParameterDefinition]::new($module.TypeSystem.Object))
    $ctor.Parameters.Add([Mono.Cecil.ParameterDefinition]::new($module.TypeSystem.IntPtr))
    $before=@{}
    foreach($method in $types.Methods){$before[$method.FullName]=Body $method}
    $enter.Body=[Mono.Cecil.Cil.MethodBody]::new($enter)
    foreach($instruction in @((I 'Ldarg_0'),(I 'Ldarg_2'),(I 'Ldarg_0'),(I 'Ldfld' $remote),(I 'Dup'),(I 'Ldvirtftn' $send[0]),(I 'Newobj' $ctor),(I 'Call' $onEnter),(I 'Ret'))){$enter.Body.Instructions.Add($instruction)}
    $first=$wnd.Body.Instructions[0];$il=$wnd.Body.GetILProcessor()
    foreach($instruction in @((I 'Ldarg_0'),(I 'Ldarg_1'),(I 'Call' $onMessage),(I 'Brfalse' $first),(I 'Ret'))){$il.InsertBefore($first,$instruction)}
    $edited=@($enter.FullName,$wnd.FullName)
    foreach($name in @('timerHelper_Tick','FormHelper_Shown','FormHelper_LocationChanged')) {
        $method=@($form.Methods | Where-Object Name -CEQ $name)[0]
        if(!$method){throw "Missing helper lifecycle $name"}
        $first=$method.Body.Instructions[0];$il=$method.Body.GetILProcessor()
        foreach($instruction in @((I 'Ldarg_0'),(I 'Call' $ownsWindow),(I 'Brfalse' $first),(I 'Ret'))){$il.InsertBefore($first,$instruction)}
        $edited+=$method.FullName
    }
    $destination=Join-Path $out 'MouseWithoutBordersHelper.exe'
    $module.Write($destination)
    $check=[Mono.Cecil.ModuleDefinition]::ReadModule($destination)
    try {
        $unchanged=0
        foreach($method in (AllTypes $check.Types).Methods){
            if($edited -ccontains $method.FullName){continue}
            if((Body $method) -cne $before[$method.FullName]){throw "Unexpected helper method modification: $($method.FullName)"}
            $unchanged++
        }
        if(@($check.AssemblyReferences | Where-Object Name -CEQ 'MwbNativeDrag').Count -ne 1){throw 'Missing helper runtime reference'}
    } finally {$check.Dispose()}
    [ordered]@{Scope='Offline helper candidate, not installed; real OLE cleanup unverified';ChangedMethods=$edited;UnchangedMethods=$unchanged;OriginalSHA256=(Get-FileHash $source).Hash;CandidateSHA256=(Get-FileHash $destination).Hash;RuntimeSHA256=(Get-FileHash $dll).Hash} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $out 'helper-verification.json') -Encoding UTF8
    Write-Output "Offline helper built: 5 patched methods; $unchanged unchanged methods verified."
} finally {$module.Dispose();$lib.Dispose()}
