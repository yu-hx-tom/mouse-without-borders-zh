using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using MwbTransfer;

// v9.0.0.9 coordinator replay. Drives the real NativeDragRuntime through a fake
// platform: no input is injected, no window is touched, no socket is opened.
// Work items and the packet outbox are pumped explicitly so races can be ordered.
// Query helpers (Count/Last/LastId) NEVER flush the outbox (Codex 9.0.0.8 note):
// tests flush explicitly, so a packet that is still queued is visible as such.
sealed class FakeConnection { internal uint Id; internal FakeConnection(uint id){Id=id;} }
class FakePlatform : NativePlatform
{
    internal DateTime Clock=new DateTime(2026,10,12,0,0,0,DateTimeKind.Utc);
    internal uint LocalId=10,PointerId=10;
    internal readonly HashSet<uint> Disconnected=new HashSet<uint>();
    internal readonly List<Action> Work=new List<Action>();
    internal readonly List<Action> Outbox=new List<Action>();
    internal readonly List<int[]> Packets=new List<int[]>(); // type, peer, argument
    internal readonly List<Guid> PacketIds=new List<Guid>();
    internal readonly List<string> Messages=new List<string>();
    internal readonly List<Guid> Drops=new List<Guid>(),Offers=new List<Guid>();
    internal readonly List<int> HelperCommands=new List<int>();
    internal Func<int,bool> HelperReply=delegate{return true;};
    internal readonly IntPtr HelperWindow=new IntPtr(0x5150);
    internal bool Folder;
    internal int PreviewOn,PreviewOff,Hides,Placements,Moves;
    // Simulated desktop: OLE drag loop, ordinary capture owner, left button, probe completeness.
    internal bool Ole,LeftDown=true,ProbeComplete=true;internal IntPtr Ordinary;
    internal int SendUnderGate,ConnectedUnderGate,ProbeUnderGate;
    internal NativeInputDisposition LastHook=NativeInputDisposition.Ordinary;internal int HookCalls;
    internal Action BeforeHook;
    internal int SlowMilliseconds;internal bool ThreadedOutbox;
    readonly object outboxGate=new object();Thread outboxThread;readonly Queue<Action> threaded=new Queue<Action>();
    internal override DateTime Now{get{lock(this)return Clock;}}
    // Lets a test run another queued worker while this one is "sleeping" (true overlap).
    internal Action DuringSleep;
    internal override void Sleep(int milliseconds){Action a=DuringSleep;DuringSleep=null;if(a!=null)a();}
    internal override void Queue(Action work){lock(Work)Work.Add(work);}
    internal override void RunLong(Action work){lock(Work)Work.Add(work);}
    internal override void StartTimer(Action tick){}
    // Outbox sequence numbers, like the real NativePlatform (v9.0.0.10).
    long postedSeq,writtenSeq;
    internal override long Post(Action send)
    {
        if(!ThreadedOutbox){lock(Outbox){Outbox.Add(send);return ++postedSeq;}}
        lock(outboxGate)
        {
            threaded.Enqueue(send);long seq=++postedSeq;
            if(outboxThread==null){outboxThread=new Thread(delegate(){while(true){Action n;lock(outboxGate){while(threaded.Count==0)Monitor.Wait(outboxGate);n=threaded.Dequeue();}n();lock(outboxGate)writtenSeq++;}});outboxThread.IsBackground=true;outboxThread.Start();}
            Monitor.Pulse(outboxGate);
            return seq;
        }
    }
    internal override long LastPosted{get{if(!ThreadedOutbox)lock(Outbox)return postedSeq;lock(outboxGate)return postedSeq;}}
    internal override bool Written(long seq){if(!ThreadedOutbox)lock(Outbox)return writtenSeq>=seq;lock(outboxGate)return writtenSeq>=seq;}
    // Manual outbox: nothing is written unless the test flushes, so no time can help.
    internal override bool WaitWritten(long seq,int milliseconds)
    {
        if(!ThreadedOutbox)return Written(seq);
        var clock=Stopwatch.StartNew();
        while(!Written(seq)){if(clock.ElapsedMilliseconds>=milliseconds)return false;Thread.Sleep(0);}
        return true;
    }
    internal bool AbsenceSupported=true;
    internal override bool AbsencePathSupported{get{return AbsenceSupported;}}
    internal override bool InDefaultDesktop(){return true;}
    internal override uint Local{get{return LocalId;}}
    internal override uint Pointer{get{return PointerId;}}
    internal override void SetDown(bool down){}
    internal override string Name(uint peer){return "PC"+peer;}
    internal override bool Connected(uint peer)
    {
        if(NativeDragRuntime.GateHeld)Interlocked.Increment(ref ConnectedUnderGate);
        if(SlowMilliseconds>0)Thread.Sleep(SlowMilliseconds);
        lock(Disconnected)return peer==LocalId||!Disconnected.Contains(peer);
    }
    internal override void Send(int type,uint peer,Guid id,uint argument)
    {
        if(NativeDragRuntime.GateHeld)Interlocked.Increment(ref SendUnderGate);
        if(SlowMilliseconds>0)Thread.Sleep(SlowMilliseconds);
        lock(Packets){Packets.Add(new[]{type,(int)peer,(int)argument});PacketIds.Add(id);}
    }
    internal override bool ConnectFile(string machine,Guid id,bool sending){return true;}
    internal override uint PeerOf(object connection){return ((FakeConnection)connection).Id;}
    internal override bool ConnectionAlive(object connection){return connection!=null;}
    internal override object CaptureDrop(){return new object();}
    internal override void PreviewBegin(){PreviewOn++;}
    internal override void PreviewEnd(){PreviewOff++;}
    internal override void BeginDrop(Guid id,string peer,object snapshot,Action<Guid,string> request){Drops.Add(id);}
    internal override bool Offer(Guid id,string peer,string path){Offers.Add(id);return true;}
    internal override void FailRequest(Guid id,string reason){}
    internal override void TickRequests(){}
    internal override void Message(string title,string text){lock(Messages)Messages.Add(title);}
    internal override bool IsDirectory(string path){return Folder;}
    internal override IntPtr LocateHelper(){return HelperWindow;}
    internal override bool HelperCommand(IntPtr window,Guid id,int command){HelperCommands.Add(command);return HelperReply(command);}
    internal override void PlaceHelper(IntPtr window,Rectangle bounds){Placements++;}
    internal override void HideHelper(IntPtr window){Hides++;}
    internal override bool CursorPos(out Point point){point=new Point(100,100);return true;}
    internal override IntPtr WindowAt(Point point){return HelperWindow;}
    internal override CaptureState ProbeCapture()
    {
        if(NativeDragRuntime.GateHeld)Interlocked.Increment(ref ProbeUnderGate);
        return new CaptureState{Complete=ProbeComplete,OleDrag=Ole,OleWindow=Ole?new IntPtr(0xC1):IntPtr.Zero,Ordinary=Ordinary,LeftDown=LeftDown};
    }
    internal IntPtr LastMoveTag;
    internal override uint InjectMove(int dx,IntPtr tag){Moves++;LastMoveTag=tag;return 1;}
    internal override uint InjectLeftUp(IntPtr token,out int error)
    {
        error=0;HookCalls++;
        Action before=BeforeHook;BeforeHook=null;if(before!=null)before();
        // Simulate the hook seeing our injected UP, with the observed low-DWORD chain.
        IntPtr data=Marshal.AllocHGlobal(32);
        try
        {
            for(int i=0;i<32;i+=4)Marshal.WriteInt32(data,i,0);
            Marshal.WriteInt32(data,12,1);Marshal.WriteIntPtr(data,IntPtr.Size==8?24:20,new IntPtr(token.ToInt64()&0xFFFFFFFFL));
            int result=NativeDragRuntime.InspectMouse(0,0x202,data);
            LastHook=result==1?NativeInputDisposition.PassToWindows:result==-1?NativeInputDisposition.SuppressStale:NativeInputDisposition.Ordinary;
            if(LastHook==NativeInputDisposition.PassToWindows){LeftDown=false;Ole=false;Ordinary=IntPtr.Zero;} // the system processed the UP
        }
        finally{Marshal.FreeHGlobal(data);}
        return 1;
    }
    internal void Flush(){while(true){Action a;lock(Outbox){if(Outbox.Count==0)return;a=Outbox[0];Outbox.RemoveAt(0);}a();lock(Outbox)writtenSeq++;}}
    internal void Pump(){Flush();while(true){Action a;lock(Work){if(Work.Count==0)break;a=Work[0];Work.RemoveAt(0);}a();Flush();}}
    internal void RunLast(){Action a;lock(Work){a=Work[Work.Count-1];Work.RemoveAt(Work.Count-1);}a();Flush();}
    // Pure queries: they never flush.
    internal int Count(int type,uint peer){int n=0;lock(Packets)foreach(int[] p in Packets)if(p[0]==type&&(uint)p[1]==peer)n++;return n;}
    internal int[] Last(int type){lock(Packets)for(int i=Packets.Count-1;i>=0;i--)if(Packets[i][0]==type)return Packets[i];return null;}
    internal Guid LastId(int type){lock(Packets)for(int i=Packets.Count-1;i>=0;i--)if(Packets[i][0]==type)return PacketIds[i];return Guid.Empty;}
    internal int Queued(){lock(Outbox)return Outbox.Count;}
}

static class NativeRuntimeTests
{
    const int Hello=80,ArmSource=82,ArmTarget=83,Ready=84,Leave=86,Drop=87,Commit=89,Abort=90,Ended=92,CleanupDelayed=93,Unsupported=94,Recover=95;
    static int checks;
    static void Check(bool value,string name){if(!value)throw new Exception("FAILED: "+name);checks++;}
    static FakePlatform Reset(uint local){var f=new FakePlatform{LocalId=local,PointerId=local};NativeDragRuntime.ResetForTests(f);return f;}
    static void Packet(FakePlatform f,int type,uint from,Guid id,uint argument)
    { NativeDragRuntime.Packet(NativeWire.Encode(type,from,f.LocalId,id,argument),new FakeConnection(from)); }
    static void MousePacket(FakePlatform f,uint from,int message)
    {
        byte[] b=new byte[64];Array.Copy(BitConverter.GetBytes(123),0,b,0,4);Array.Copy(BitConverter.GetBytes(from),0,b,8,4);
        Array.Copy(BitConverter.GetBytes(f.LocalId),0,b,12,4);Array.Copy(BitConverter.GetBytes(message),0,b,28,4);
        NativeDragRuntime.Packet(b,new FakeConnection(from));
    }
    static void Confirm(FakePlatform f,uint peer){Packet(f,Hello,peer,NativeWire.NewId(),1);}
    static int Hook(int message,int flags,IntPtr token)
    {
        IntPtr d=Marshal.AllocHGlobal(32);for(int i=0;i<32;i+=4)Marshal.WriteInt32(d,i,0);
        Marshal.WriteInt32(d,12,flags);Marshal.WriteIntPtr(d,IntPtr.Size==8?24:20,token);
        try{return NativeDragRuntime.InspectMouse(0,message,d);}finally{Marshal.FreeHGlobal(d);}
    }
    // A held crossing with the fake (manual) outbox: the first attempt is declined
    // until the queued packets are written, exactly like MWB retrying at the edge.
    static bool Cross(FakePlatform f,uint next)
    {
        if(NativeDragRuntime.BeforeSwitch(next))return true;
        f.Flush();
        return NativeDragRuntime.BeforeSwitch(next);
    }
    static void NoNetworkUnderGate(FakePlatform f)
    {
        Check(f.SendUnderGate==0&&f.ConnectedUnderGate==0&&f.ProbeUnderGate==0&&NativeDragRuntime.IpcUnderGate==0,"no socket write, connectivity probe, capture probe or helper IPC while holding the coordinator gate");
    }

    // S=C=10, release on P=20. Returns the leg.
    static Guid ReleaseOnTarget(FakePlatform f,bool withFile)
    {
        Confirm(f,20);f.Flush();int armedBefore=f.Count(ArmTarget,20);
        NativeDragRuntime.LocalMouse(0x201);
        bool first=NativeDragRuntime.BeforeSwitch(20);int armed=f.Count(ArmTarget,20)-armedBefore;
        Check(!first&&armed==0&&f.Queued()==1,"v9.0.0.10: the held crossing is DECLINED while its ArmTarget is only queued (nothing written on the hook) [first="+first+" armed="+armed+" queued="+f.Queued()+"]");
        Check(!NativeDragRuntime.BeforeSwitch(20)&&f.Queued()==1,"repeated attempts do not post a second ArmTarget");
        f.Flush();
        Check(NativeDragRuntime.BeforeSwitch(20),"once ArmTarget is on the wire, the next attempt crosses");
        Guid leg=f.LastId(ArmTarget);
        f.PointerId=20;NativeDragRuntime.LocalMouse(0x200);
        Check(NativeDragRuntime.SourceState=="Capturing","ArmSource reached the local source");
        f.Pump();
        if(withFile)NativeDragRuntime.CaptureReply(NativeDragCapture.Encode(leg,1,@"C:\Users\u\test.bin"));
        NativeDragRuntime.LocalMouse(0x202);
        Check(NativeDragRuntime.SourceState=="Ending"&&NativeDragRuntime.ControllerWaiting,"release on P puts S in Ending and holds the controller");
        Check(!NativeDragRuntime.BeforeSwitch(10),"source return is blocked while cleanup is pending");
        return leg;
    }

    // Original P0-1 (text selection, rubber band...): the absence path needs three facts.
    static void AbsencePathNeedsThreeFacts()
    {
        for(int variant=0;variant<2;variant++)
        {
            var f=Reset(10);f.Ole=false;f.LeftDown=true;f.Ordinary=variant==0?new IntPtr(0x77):IntPtr.Zero;
            f.HelperReply=delegate(int c){return c!=4;};   // no OLE ever reaches the sink
            ReleaseOnTarget(f,false);f.Pump();
            string label=variant==0?"ordinary capture owner":"no capture at all";
            Check(f.HookCalls==1&&f.LastHook==NativeInputDisposition.PassToWindows,label+": one tagged UP on complete, independent absence evidence");
            Check(f.HelperCommands.Contains(9),label+": the sink's 'no OLE seen on this leg' fact was required");
            Check(NativeDragRuntime.ProofReleases==1&&NativeDragRuntime.SourceState=="Ended"&&!NativeDragRuntime.ControllerWaiting,label+": released, controller unlocked");
            Check(f.Count(Abort,20)==1&&f.Offers.Count==0&&f.Messages.Count==0,label+": nothing transferred, no warning");
            Check(f.LastMoveTag!=IntPtr.Zero&&f.LastMoveTag!=NativeDragRuntime.GetNormalMouseTag()&&Hook(0x200,1,f.LastMoveTag)==-1,label+": cleanup moves carried an attempt token, refused once the attempt ended");
            NoNetworkUnderGate(f);
        }
        // Each missing fact disables the absence path (fail closed).
        for(int missing=0;missing<3;missing++)
        {
            var f=Reset(10);f.Ole=false;f.LeftDown=true;f.Ordinary=new IntPtr(0x77);
            if(missing==0)f.ProbeComplete=false;
            if(missing==1)f.HelperReply=delegate(int c){return c!=9&&c!=4;};   // the sink saw an OLE callback
            ReleaseOnTarget(f,missing==2);                                       // missing==2: a file capture happened on this leg
            f.HelperReply=missing==1?f.HelperReply:delegate(int c){return c!=4;};// OLE None never confirmed
            f.Pump();
            string label=missing==0?"probe incomplete (Unknown)":missing==1?"sink saw OLE":"leg has OLE evidence (captured file)";
            Check(f.HookCalls==0&&NativeDragRuntime.SourceState=="Ending",label+": no UP; the leg waits for strict evidence or Esc");
            NoNetworkUnderGate(f);
        }
        var g=Reset(10);g.Ole=false;g.LeftDown=false;
        ReleaseOnTarget(g,false);g.Pump();
        Check(g.HookCalls==0&&NativeDragRuntime.SourceState=="Ended","complete probe: no OLE and the button already up => finished without any synthetic input");
        var h=Reset(10);h.Ole=false;h.LeftDown=false;h.ProbeComplete=false;h.HelperReply=delegate(int c){return c!=4&&c!=9;};
        ReleaseOnTarget(h,false);h.Pump();
        Check(h.HookCalls==0&&NativeDragRuntime.SourceState=="Ending","an incomplete probe is never read as 'button released'");
    }

    // Codex 9.0.0.8 #3: the strict path accepts only OLE's own None confirmation.
    static void StrictPathIsStrict()
    {
        var f=Reset(10);f.Ole=true;f.HelperReply=delegate(int c){return c!=4;};
        ReleaseOnTarget(f,true);f.Pump();
        for(int i=0;i<120;i++){f.Clock=f.Clock.AddSeconds(30);NativeDragRuntime.Tick();f.Pump();}
        Check(f.HookCalls==0,"one hour of a stalled OLE source: no synthetic UP");
        Check(NativeDragRuntime.SourceState=="Ending"&&NativeDragRuntime.ControllerWaiting,"nothing is released by elapsed time");
        Check(f.Messages.Count==1,"the user is told once how to recover (Esc)");
        var s=Reset(10);s.Ole=true;s.HelperReply=delegate(int c){return c!=7;};
        ReleaseOnTarget(s,true);s.Pump();
        Check(s.HookCalls==1&&NativeDragRuntime.StrictReleases==1,"OLE None on the sink authorises exactly one UP");
        Check(s.Offers.Count==1&&s.Count(Commit,20)==1&&!NativeDragRuntime.ControllerWaiting,"a strict release carries the file and unlocks the controller");
        var w=Reset(10);w.Ole=true;w.ProbeComplete=false;w.HelperReply=delegate(int c){return c!=7;};
        ReleaseOnTarget(w,true);w.Pump();
        Check(w.HookCalls==1&&NativeDragRuntime.StrictReleases==1,"the strict path does not depend on the capture probe (helper confirms the drop)");
        NoNetworkUnderGate(f);NoNetworkUnderGate(s);
    }

    // Codex 9.0.0.8 #2 (and 9.0.0.7 P1-2): nothing of the old cleanup reaches new input.
    static void RecoveryRetiresAllSideEffects()
    {
        var f=Reset(10);f.Ole=true;
        ReleaseOnTarget(f,true);
        bool returned=false;string afterEsc=null;int moveBeforeEsc=0,moveAfterEsc=0;
        f.BeforeHook=delegate{
            f.Clock=f.Clock.AddSeconds(2);
            moveBeforeEsc=Hook(0x200,1,f.LastMoveTag);
            NativeDragRuntime.Escape();
            // Still inside the old attempt (its finally has not run): the move token must
            // already be dead, not only once the attempt unwinds.
            moveAfterEsc=Hook(0x200,1,f.LastMoveTag);
            afterEsc=NativeDragRuntime.SourceState;
            returned=NativeDragRuntime.BeforeSwitch(10);f.PointerId=10;
            NativeDragRuntime.LocalMouse(0x201);
            f.Ordinary=new IntPtr(0xBEEF);f.LeftDown=true;   // the user's NEW selection capture
        };
        f.Pump();
        Check(afterEsc=="Ended"&&returned,"Esc retires the local old cleanup before the pointer may return");
        Check(moveBeforeEsc==1&&moveAfterEsc==-1,"the running attempt's move token passes until Esc and is refused from the moment of recovery");
        Check(f.HookCalls==1&&f.LastHook==NativeInputDisposition.SuppressStale,"the old cleanup's UP is refused after recovery");
        Check(f.Ordinary==new IntPtr(0xBEEF),"no cancel/release reaches the user's NEW capture (v9.0.0.8 sent WM_CANCELMODE to it)");
        Check(Hook(0x200,1,f.LastMoveTag)==-1,"a cleanup move delivered after recovery is dropped by the hook");
        Check(NativeDragRuntime.Invalidations==1&&NativeDragRuntime.SourceState=="Native","old leg invalidated; the new press owns a fresh native session");
        // During an active cleanup the same tag passes.
        // Codex 9.0.0.9 P2-3: a move from attempt/leg A must not pass during leg B's cleanup.
        var g=Reset(10);g.Ole=true;g.HelperReply=delegate(int c){return c!=7;};
        ReleaseOnTarget(g,true);bool allowedDuring=false;IntPtr tokenA=IntPtr.Zero;
        g.DuringSleep=delegate{tokenA=g.LastMoveTag;allowedDuring=Hook(0x200,1,tokenA)==1;};
        g.Pump();
        Check(allowedDuring&&NativeDragRuntime.SourceState=="Ended","leg A: its own move token passes while its attempt runs; A completes");
        Check(Hook(0x200,1,tokenA)==-1,"...and is dropped once that attempt ended");
        g.PointerId=10;g.LeftDown=true;g.Ole=true;g.HelperReply=delegate(int c){return c!=4;};
        ReleaseOnTarget(g,true);int oldDuringB=0,newDuringB=0;IntPtr tokenB=IntPtr.Zero;
        g.DuringSleep=delegate{tokenB=g.LastMoveTag;oldDuringB=Hook(0x200,1,tokenA);newDuringB=Hook(0x200,1,tokenB);};
        g.Pump();
        Check(tokenB!=tokenA&&oldDuringB==-1&&newDuringB==1,"Codex replay inverted: during leg B's cleanup the stale A move stays refused; only B's token passes");
        NativeDragRuntime.Escape();
        Check(NativeDragRuntime.ControllerWaiting,"Esc within the first second is not a recovery request");
        NoNetworkUnderGate(f);
    }

    static void TimerRetryBeatsFirstWorker()
    {
        var f=Reset(10);f.Ole=true;f.HelperReply=delegate(int c){return c!=7;};
        ReleaseOnTarget(f,true);
        NativeDragRuntime.Tick();
        Check(f.Work.Count==2,"first worker and timer retry are both queued");
        Action first=f.Work[0];f.Work.RemoveAt(0);f.DuringSleep=first;
        f.RunLast();f.Pump();
        Check(NativeDragRuntime.SourceState=="Ended"&&!NativeDragRuntime.ControllerWaiting,"the retry claims the leg and completes");
        Check(f.HookCalls==1,"exactly one release injected");
    }

    static void ControllerUnlocksOnlyByEvidence()
    {
        for(int variant=0;variant<3;variant++)
        {
            var f=Reset(10);Confirm(f,20);Confirm(f,30);f.Flush();
            f.PointerId=30;NativeDragRuntime.LocalMouse(0x201);
            Check(Cross(f,20),"S=30 -> P=20 held crossing");
            f.PointerId=20;NativeDragRuntime.LocalMouse(0x200);NativeDragRuntime.LocalMouse(0x202);f.Flush();
            Guid leg=f.LastId(Drop);
            Check(f.Count(Drop,30)==1&&NativeDragRuntime.ControllerWaiting,"Drop sent; controller waits");
            for(int i=0;i<10;i++){f.Clock=f.Clock.AddMinutes(1);NativeDragRuntime.Tick();}
            Check(NativeDragRuntime.ControllerWaiting&&!NativeDragRuntime.BeforeSwitch(30),"ten minutes pass: still waiting (no time-based unlock)");
            if(variant==0){lock(f.Disconnected)f.Disconnected.Add(30);NativeDragRuntime.Tick();Check(f.Messages.Count==1,"link loss unlocks with a warning");}
            else if(variant==1){NativeDragRuntime.Escape();Check(f.Count(Recover,30)==0&&f.Queued()>=1,"v9.0.0.10: Esc only QUEUES Recover (nothing written on the hook)");f.Flush();Check(f.Count(Recover,30)==1,"Recover is written by the outbox");}
            else {Packet(f,Ended,30,leg,1);Check(f.Messages.Count==0,"Ended(released) unlocks silently");}
            Check(!NativeDragRuntime.ControllerWaiting&&NativeDragRuntime.BeforeSwitch(30),"return allowed");
            NoNetworkUnderGate(f);
        }
    }

    static void SourceRetiresOldCleanup()
    {
        for(int variant=0;variant<3;variant++)
        {
            var f=Reset(30);f.Ole=true;f.HelperReply=delegate(int c){return c!=4;};
            MousePacket(f,10,0x201);
            Guid leg=NativeWire.NewId();
            Packet(f,ArmSource,10,leg,20);f.Pump();
            Packet(f,Drop,10,leg,20);f.Pump();
            Check(NativeDragRuntime.SourceState=="Ending"&&f.HookCalls==0,"S waits for OLE None and injects nothing");
            Check(f.Count(CleanupDelayed,10)==1,"controller told once");
            if(variant==0)Packet(f,Recover,10,leg,0);
            else if(variant==1)MousePacket(f,10,0x200);
            else {f.PointerId=30;NativeDragRuntime.LocalMouse(0x201);}
            f.Flush();
            string label=variant==0?"Recover":variant==1?"controller input returning":"S's own physical press";
            Check(NativeDragRuntime.Invalidations==1,label+": old leg invalidated");
            Check(f.Count(Abort,20)==1&&f.Count(Ended,10)==1&&f.Last(Ended)[2]==(int)NativeDragRuntime.EndedAbandoned,label+": target aborted, controller told");
            int moves=f.Moves;f.Pump();NativeDragRuntime.Tick();f.Pump();
            Check(f.HookCalls==0&&f.Moves==moves,label+": no synthetic input of any kind after invalidation");
            NoNetworkUnderGate(f);
        }
    }

    static void TargetLifecycle()
    {
        var f=Reset(20);
        Guid t1=NativeWire.NewId();
        Packet(f,ArmTarget,10,t1,30);Packet(f,Ready,30,t1,10);
        Check(NativeDragRuntime.HasActiveTarget&&f.PreviewOn==1,"remote preview shown");
        lock(f.Disconnected)f.Disconnected.Add(10);NativeDragRuntime.Tick();
        Check(!NativeDragRuntime.HasActiveTarget&&f.PreviewOff>=1,"controller link loss removes icon/border residue");
        lock(f.Disconnected)f.Disconnected.Remove(10);NativeDragRuntime.Tick();
        Guid t2=NativeWire.NewId();Packet(f,ArmTarget,10,t2,30);Packet(f,Ready,30,t2,10);
        Check(Hook(0x202,1,NativeDragRuntime.GetNormalMouseTag())==1,"controller's injected UP passes to Windows");
        Check(NativeDragRuntime.ReleasedTargetCount==1&&!NativeDragRuntime.HasActiveTarget,"released target waits for Commit by id");
        Guid t3=NativeWire.NewId();Packet(f,ArmTarget,10,t3,30);Packet(f,Ready,30,t3,10);
        Check(NativeDragRuntime.HasActiveTarget&&f.PreviewOn==3,"a new crossing previews while the earlier release waits");
        int offBefore=f.PreviewOff;
        Packet(f,Commit,30,t2,10);
        Check(f.Drops.Count==1&&f.Drops[0]==t2,"late Commit completes the earlier release");
        Check(f.PreviewOff==offBefore&&NativeDragRuntime.HasActiveTarget,"the late Commit does not end the new leg's preview");
        Hook(0x202,1,NativeDragRuntime.GetNormalMouseTag());
        f.Clock=f.Clock.AddSeconds(61);NativeDragRuntime.Tick();
        Check(NativeDragRuntime.ReleasedTargetCount==0,"a release whose Commit never arrives expires");
        Check(Hook(0x202,0,IntPtr.Zero)==0,"physical input is untouched by the hook fast path");
        NoNetworkUnderGate(f);

        var g=Reset(20);Guid a=NativeWire.NewId();
        Packet(g,ArmTarget,10,a,30);Hook(0x202,1,NativeDragRuntime.GetNormalMouseTag());Packet(g,Ready,30,a,10);Packet(g,Commit,30,a,10);
        Check(g.Drops.Count==1&&g.Drops[0]==a,"Ready after the release still completes the transfer");
        Check(g.PreviewOn==0,"a released target never reopens a preview");
        var k=Reset(20);Guid c=NativeWire.NewId();
        Packet(k,ArmTarget,10,c,30);Hook(0x202,1,NativeDragRuntime.GetNormalMouseTag());Packet(k,Commit,30,c,10);
        Check(k.Drops.Count==0&&NativeDragRuntime.ReleasedTargetCount==1,"Commit without Ready keeps the record");
        Packet(k,Ready,30,c,10);
        Check(k.Drops.Count==1&&k.Drops[0]==c,"the late Ready completes the transfer");
        var h=Reset(20);Guid b=NativeWire.NewId();
        Packet(h,Ready,30,b,10);Packet(h,ArmTarget,10,b,30);
        Check(h.PreviewOn==1,"an early Ready is applied when ArmTarget arrives");
        Hook(0x202,1,NativeDragRuntime.GetNormalMouseTag());Packet(h,Commit,30,b,10);
        Check(h.Drops.Count==1,"...and the transfer completes");
    }

    // Codex 9.0.0.8 #1: a Commit whose release never arrived can never be completed by a later click.
    static void CommitWithoutReleaseNeverUsesALaterClick()
    {
        // (a) Commit first, the controller's UP arrives within the grace: normal completion.
        var f=Reset(20);Guid id=NativeWire.NewId();
        Packet(f,ArmTarget,10,id,30);Packet(f,Ready,30,id,10);Packet(f,Commit,30,id,10);
        Check(f.Drops.Count==0&&NativeDragRuntime.HasActiveTarget,"Commit before the release waits");
        f.Clock=f.Clock.AddSeconds(1);Hook(0x202,1,NativeDragRuntime.GetNormalMouseTag());
        Check(f.Drops.Count==1&&f.Drops[0]==id,"the release inside the grace completes the transfer");
        // (b) the release never comes: the record closes; a much later click does nothing.
        var g=Reset(20);Guid lost=NativeWire.NewId();
        Packet(g,ArmTarget,10,lost,30);Packet(g,Ready,30,lost,10);Packet(g,Commit,30,lost,10);
        g.Clock=g.Clock.AddSeconds(61);NativeDragRuntime.Tick();
        Check(!NativeDragRuntime.HasActiveTarget&&g.Messages.Count==1,"the committed but unreleased target closes with a notice");
        Hook(0x202,1,NativeDragRuntime.GetNormalMouseTag());
        Check(g.Drops.Count==0,"Codex replay inverted: a later unrelated UP never starts the old transfer");
        // (c) a stray click after the grace but before the next Tick is also refused.
        var h=Reset(20);Guid late=NativeWire.NewId();
        Packet(h,ArmTarget,10,late,30);Packet(h,Ready,30,late,10);Packet(h,Commit,30,late,10);
        h.Clock=h.Clock.AddSeconds(3);Hook(0x202,1,NativeDragRuntime.GetNormalMouseTag());
        Check(h.Drops.Count==0&&!NativeDragRuntime.HasActiveTarget,"an UP after the grace is not taken as this gesture's release");
    }

    // Codex 9.0.0.8 #5: an in-flight link probe from before a new target is discarded.
    sealed class VerdictRace : FakePlatform
    {
        internal Action DuringProbe;
        internal override bool Connected(uint peer)
        {
            if(NativeDragRuntime.GateHeld)Interlocked.Increment(ref ConnectedUnderGate);
            if(peer==10&&DuringProbe!=null){Action a=DuringProbe;DuringProbe=null;a();return false;}
            return true;
        }
    }
    static void InFlightLinkVerdictIsDiscarded()
    {
        var f=new VerdictRace{LocalId=20,PointerId=20};NativeDragRuntime.ResetForTests(f);
        Guid oldId=NativeWire.NewId(),newId=NativeWire.NewId();Packet(f,ArmTarget,10,oldId,30);
        f.DuringProbe=delegate{Packet(f,ArmTarget,10,newId,30);Packet(f,Ready,30,newId,10);};
        NativeDragRuntime.Tick();
        Check(NativeDragRuntime.HasActiveTarget&&NativeDragRuntime.StaleLinkVerdicts>=1,"Codex replay inverted: the old 'down' verdict is discarded; the new target survives");
        NativeDragRuntime.Tick();
        Check(NativeDragRuntime.HasActiveTarget,"the next fresh probe (link up) keeps it");
    }

    static void UnsupportedCapture()
    {
        for(int variant=0;variant<2;variant++)
        {
            var f=Reset(10);Confirm(f,20);f.Flush();
            NativeDragRuntime.LocalMouse(0x201);Cross(f,20);
            Guid leg=f.LastId(ArmTarget);f.PointerId=20;NativeDragRuntime.LocalMouse(0x200);f.Pump();
            f.Folder=variant==1;
            NativeDragRuntime.CaptureReply(NativeDragCapture.Encode(leg,variant==0?3:1,@"C:\Users\u\a"));f.Flush();
            int[] u=f.Last(Unsupported);
            Check(u!=null&&u[1]==20&&(uint)u[2]==(variant==0?1u:2u),variant==0?"multi-selection reported to the target":"folder reported to the target");
            Check(f.Count(Ready,20)==0,"no Ready for unsupported data");
        }
        var t=Reset(20);Guid id=NativeWire.NewId();
        Packet(t,ArmTarget,10,id,30);Packet(t,Unsupported,30,id,2);
        Hook(0x202,1,NativeDragRuntime.GetNormalMouseTag());
        Check(t.Messages.Count==1,"the target explains why nothing was transferred on release");
    }

    static void ReturnDuringCapture()
    {
        var f=Reset(10);Confirm(f,20);f.Flush();
        NativeDragRuntime.LocalMouse(0x201);Cross(f,20);f.PointerId=20;NativeDragRuntime.LocalMouse(0x200);
        f.Pump();
        Guid leg=f.LastId(ArmTarget);
        Check(NativeDragRuntime.HelperLeg==leg,"helper armed for the leg");
        int before=f.HelperCommands.Count;
        Check(Cross(f,10),"return to S");
        Check(f.HelperCommands.Count==before&&f.Hides>=1,"no synchronous helper IPC on the switch path; helper hidden asynchronously");
        Check(NativeDragRuntime.SourceState=="Native","Windows owns the drag again");
        f.Pump();
        Check(f.HelperCommands[f.HelperCommands.Count-1]==2&&NativeDragRuntime.HelperLeg==Guid.Empty,"invalidation completes off the hook");
        NoNetworkUnderGate(f);
    }

    // Codex 9.0.0.9 P1-2: measure REAL accepted crossings (not a blocked fast path).
    // Every socket write and connectivity probe takes 400 ms; MWB's retry at the edge is
    // simulated by calling BeforeSwitch every 5 ms until it accepts.
    static double worstCall;
    static long CrossTimed(FakePlatform f,uint next,out bool accepted)
    {
        var wall=Stopwatch.StartNew();var call=new Stopwatch();accepted=false;
        while(wall.ElapsedMilliseconds<10000)
        {
            call.Restart();bool ok=NativeDragRuntime.BeforeSwitch(next);worstCall=Math.Max(worstCall,call.Elapsed.TotalMilliseconds);
            if(ok){accepted=true;break;}
            Thread.Sleep(5);
        }
        return wall.ElapsedMilliseconds;
    }
    static void Timed(Action hookCall){var c=Stopwatch.StartNew();hookCall();worstCall=Math.Max(worstCall,c.Elapsed.TotalMilliseconds);}
    // On a normal link the bounded wait (CrossWaitMs) lets the FIRST attempt cross, so a
    // file drag does not hesitate ~100 ms at every edge (MWB's retry throttle).
    static void FastNetworkCrossesFirstTime()
    {
        var f=Reset(10);f.ThreadedOutbox=true;Confirm(f,20);Confirm(f,30);Confirm(f,40);Thread.Sleep(50);
        f.PointerId=30;NativeDragRuntime.LocalMouse(0x201);
        var c=Stopwatch.StartNew();bool first=NativeDragRuntime.BeforeSwitch(20);long ms=c.ElapsedMilliseconds;
        Check(first&&f.Count(ArmTarget,20)==1&&ms<=NativeDragRuntime.CrossWaitMs+30,"fast link: the first held attempt crosses, ArmTarget(20) already written ("+ms+" ms)");
        f.PointerId=20;NativeDragRuntime.LocalMouse(0x200);
        Check(NativeDragRuntime.BeforeSwitch(40)&&f.Count(ArmTarget,40)==1,"fast link: target-to-target crossing at the first attempt");
        f.PointerId=40;NativeDragRuntime.LocalMouse(0x200);
        Check(NativeDragRuntime.BeforeSwitch(30),"fast link: return to the source at the first attempt");
        Check(NativeDragRuntime.DeferredCrossings>=0&&f.SendUnderGate==0,"no write under the gate");
        f.PointerId=30;NativeDragRuntime.LocalMouse(0x202);
    }
    static void HookLatencyWithSlowNetwork()
    {
        worstCall=0;
        // Four peers (Codex layout): controller 10, source 30, old target 20, new target 40.
        var f=Reset(10);f.ThreadedOutbox=true;Confirm(f,20);Confirm(f,30);Confirm(f,40);
        Thread.Sleep(50);f.SlowMilliseconds=400;
        int stop=0;
        var ticker=new Thread(delegate(){while(Thread.VolatileRead(ref stop)==0){NativeDragRuntime.Tick();Thread.Sleep(1);}});
        ticker.IsBackground=true;ticker.Start();
        f.PointerId=30;Timed(delegate{NativeDragRuntime.LocalMouse(0x201);});
        bool ok;long waited=CrossTimed(f,20,out ok);
        Check(ok&&f.Count(ArmTarget,20)==1,"4 PCs: crossing 30->20 accepted only after its ArmTarget was written ("+waited+" ms of edge retries)");
        f.PointerId=20;Timed(delegate{NativeDragRuntime.LocalMouse(0x200);});
        waited=CrossTimed(f,40,out ok);
        Check(ok&&f.Count(ArmTarget,40)==1,"4 PCs: crossing 20->40 (Leave+Resume+ArmTarget) accepted after ArmTarget(40) was written ("+waited+" ms)");
        f.PointerId=40;Timed(delegate{NativeDragRuntime.LocalMouse(0x200);});
        waited=CrossTimed(f,30,out ok);
        Check(ok,"4 PCs: return to the source accepted once everything queued before it was written ("+waited+" ms)");
        f.PointerId=30;
        // Two PCs: controller/source 10, target 20.
        Thread.VolatileWrite(ref stop,1);ticker.Join(5000);
        var g=Reset(10);g.ThreadedOutbox=true;Confirm(g,20);Thread.Sleep(50);g.SlowMilliseconds=400;
        Timed(delegate{NativeDragRuntime.LocalMouse(0x201);});
        waited=CrossTimed(g,20,out ok);
        Check(ok&&g.Count(ArmTarget,20)==1,"2 PCs: crossing accepted after ArmTarget written ("+waited+" ms)");
        g.PointerId=20;Timed(delegate{NativeDragRuntime.LocalMouse(0x200);});Timed(delegate{NativeDragRuntime.LocalMouse(0x202);});
        g.Clock=g.Clock.AddSeconds(2);Timed(delegate{NativeDragRuntime.Escape();});
        Timed(delegate{Hook(0x200,0,IntPtr.Zero);});
        Check(worstCall<50,"every hook-path call (press, move, release, Esc, each crossing attempt) stays under 50 ms while each write/probe takes 400 ms (worst "+worstCall.ToString("0.0")+" ms)");
        Check(f.SendUnderGate==0&&f.ConnectedUnderGate==0&&g.SendUnderGate==0,"slow operations never ran under the gate");
        Check(NativeDragRuntime.DeferredCrossings>0,"crossings were deferred (declined and retried), never waited for on the caller");
        Console.WriteLine("real crossings with 400 ms writes: worst hook-path call "+worstCall.ToString("0.0")+" ms");
    }

    // Codex 9.0.0.9 P1-1 (two PCs: controller 10, file source 20, destination = 10).
    static void ReturnNeverOvertakesArmSource()
    {
        var c=Reset(10);c.PointerId=20;Confirm(c,20);c.Flush();
        NativeDragRuntime.LocalMouse(0x201);
        Check(Cross(c,10),"remote source departure to the controller itself");
        c.PointerId=10;NativeDragRuntime.LocalMouse(0x200);
        Check(c.Queued()==1&&c.Count(ArmSource,20)==0,"ArmSource(A) is queued");
        Check(!NativeDragRuntime.BeforeSwitch(20),"the return to S is DECLINED while ArmSource(A) is still queued");
        Check(c.Count(ArmSource,20)==0&&c.Count(85,20)==0,"nothing reached S out of order");
        c.Flush();
        Check(NativeDragRuntime.BeforeSwitch(20),"after ArmSource(A) is written, the return is accepted");
        c.Flush();
        int arm=-1,resume=-1;for(int i=0;i<c.Packets.Count;i++){if(c.Packets[i][0]==ArmSource&&arm<0)arm=i;if(c.Packets[i][0]==85)resume=i;}
        Check(arm>=0&&resume>arm,"S receives ArmSource(A) strictly before Resume(A) (and before the returning mouse input)");
        // Source side: even if an old peer delivered them reversed, a retired leg is never armed.
        var s=Reset(20);MousePacket(s,10,0x201);
        Guid a=NativeWire.NewId(),b=NativeWire.NewId();
        Packet(s,85,10,a,0);Packet(s,ArmSource,10,a,10);
        Check(NativeDragRuntime.SourceState=="Native"&&NativeDragRuntime.StaleArms==1,"Codex replay inverted: a late ArmSource for an already-returned leg is refused");
        Packet(s,ArmSource,10,b,10);
        Check(NativeDragRuntime.SourceState=="Capturing","the current leg B is armed normally");
        Guid unknown=NativeWire.NewId();Packet(s,Drop,10,unknown,10);s.Flush();
        Check(s.Count(Ended,10)==1&&s.Last(Ended)[2]==(int)NativeDragRuntime.EndedAbandoned&&NativeDragRuntime.SourceState=="Capturing","an end for a leg this source never armed is answered (controller is not left waiting) and does not disturb B");
        Packet(s,Drop,10,b,10);
        Check(NativeDragRuntime.SourceState=="Ending","B ends normally");
        Packet(s,Drop,10,NativeWire.NewId(),10);s.Flush();
        Check(s.Count(Ended,10)==2&&NativeDragRuntime.SourceState=="Ending","an unknown-leg end is answered even while another leg is Ending, without touching that leg");
    }

    static void PendingCrossIsCancelled()
    {
        var f=Reset(10);Confirm(f,20);Confirm(f,40);f.Flush();
        NativeDragRuntime.LocalMouse(0x201);
        Check(!NativeDragRuntime.BeforeSwitch(20),"crossing to 20 deferred");
        Guid armed20=Guid.Empty;
        Check(!NativeDragRuntime.BeforeSwitch(40)&&NativeDragRuntime.CrossPending,"direction changed to 40: still deferred");
        f.Flush();armed20=Guid.Empty;
        for(int i=0;i<f.Packets.Count;i++)if(f.Packets[i][0]==ArmTarget&&f.Packets[i][1]==20)armed20=f.PacketIds[i];
        bool left20=false;for(int i=0;i<f.Packets.Count;i++)if(f.Packets[i][0]==Leave&&f.Packets[i][1]==20&&f.PacketIds[i]==armed20)left20=true;
        Check(armed20!=Guid.Empty&&left20,"the target armed for the abandoned direction is un-armed (Leave with the same leg)");
        NativeDragRuntime.LocalMouse(0x202);f.Flush();
        Check(f.Count(Leave,40)==1&&!NativeDragRuntime.CrossPending,"releasing at the edge un-arms the pending target too");
        // Target side: Leave for an armed, never-entered target removes it.
        var t=Reset(20);Guid id=NativeWire.NewId();Packet(t,ArmTarget,10,id,30);Packet(t,Leave,10,id,0);
        Check(!NativeDragRuntime.HasActiveTarget,"an un-armed target leaves no record or preview");
    }

    // v9.0.0.10 platform boundary (Codex 9.0.0.9 Q3): outside the verified builds the
    // absence path is off even when all three facts hold; the strict path is unaffected.
    static void AbsencePathPlatformGate()
    {
        var f=Reset(10);f.AbsenceSupported=false;f.Ole=false;f.LeftDown=true;f.Ordinary=new IntPtr(0x77);
        f.HelperReply=delegate(int c){return c!=4;};
        ReleaseOnTarget(f,false);f.Pump();
        Check(f.HookCalls==0&&!f.HelperCommands.Contains(9)&&NativeDragRuntime.SourceState=="Ending","unverified platform: no absence-path UP; the leg waits for strict evidence or Esc");
        var s=Reset(10);s.AbsenceSupported=false;s.Ole=true;s.HelperReply=delegate(int c){return c!=7;};
        ReleaseOnTarget(s,true);s.Pump();
        Check(s.HookCalls==1&&NativeDragRuntime.StrictReleases==1,"unverified platform: the strict OLE/None path still releases");
    }

    // The production outbox (real NativePlatform, no socket): sequence numbers and the
    // non-blocking barrier the crossing relies on.
    static void ProductionOutboxBarrier()
    {
        var p=new NativePlatform();
        var gate=new ManualResetEvent(false);
        long a=p.Post(delegate{gate.WaitOne();});
        long b=p.Post(delegate{throw new InvalidOperationException("simulated write failure");});
        long c=p.Post(delegate{});
        Check(a==1&&b==2&&c==3&&p.LastPosted==3,"posts are numbered in FIFO order");
        Thread.Sleep(50);
        Check(!p.Written(a)&&!p.Written(c),"nothing counts as written while the first write is still blocked");
        var clock=Stopwatch.StartNew();bool w=p.Written(c);
        Check(!w&&clock.ElapsedMilliseconds<20,"Written() never waits for the writer");
        clock.Restart();bool bounded=p.WaitWritten(c,NativeDragRuntime.CrossWaitMs);long waitedMs=clock.ElapsedMilliseconds;
        Check(!bounded&&waitedMs>=NativeDragRuntime.CrossWaitMs-2&&waitedMs<NativeDragRuntime.CrossWaitMs+30,"WaitWritten on a blocked write gives up at its bound ("+waitedMs+" ms)");
        bool woke=false;long wokeMs=-1;
        var waiter=new Thread(delegate(){var s=Stopwatch.StartNew();woke=p.WaitWritten(c,3000);wokeMs=s.ElapsedMilliseconds;});
        waiter.Start();Thread.Sleep(30);
        long d=p.Post(delegate{});   // a Post while someone waits must still wake the writer (PulseAll)
        gate.Set();waiter.Join(5000);
        Check(woke&&wokeMs<1000,"a waiter is woken as soon as the write completes ("+wokeMs+" ms), and a concurrent Post did not steal the writer's wake-up");
        var wait=Stopwatch.StartNew();while(!p.Written(d)&&wait.ElapsedMilliseconds<5000)Thread.Sleep(1);
        Check(p.Written(a)&&p.Written(b)&&p.Written(c)&&p.Written(d),"a failed write is still counted, so crossings never freeze on it");
        Check(NativePlatform.VerifiedAbsenceBuild(10,0,19044)&&NativePlatform.VerifiedAbsenceBuild(10,0,19045),"Windows 10 21H2/22H2 builds are inside the verified boundary");
        Check(!NativePlatform.VerifiedAbsenceBuild(10,0,22000)&&!NativePlatform.VerifiedAbsenceBuild(10,0,22631)&&!NativePlatform.VerifiedAbsenceBuild(10,0,26100),"Windows 11 (also reported as 10.0) is outside: absence path off");
        Check(!NativePlatform.VerifiedAbsenceBuild(10,0,18363)&&!NativePlatform.VerifiedAbsenceBuild(6,3,9600)&&!NativePlatform.VerifiedAbsenceBuild(6,2,9200),"older Windows 10 and Windows 8.x are outside");
        bool supported=p.AbsencePathSupported;
        Console.WriteLine("this machine: absence path "+(supported?"ENABLED":"disabled")+" (Windows "+Environment.OSVersion.Version+")");
    }

    static int Main()
    {
        try
        {
            AbsencePathNeedsThreeFacts();StrictPathIsStrict();RecoveryRetiresAllSideEffects();TimerRetryBeatsFirstWorker();
            ControllerUnlocksOnlyByEvidence();SourceRetiresOldCleanup();TargetLifecycle();CommitWithoutReleaseNeverUsesALaterClick();
            InFlightLinkVerdictIsDiscarded();UnsupportedCapture();ReturnDuringCapture();ReturnNeverOvertakesArmSource();PendingCrossIsCancelled();
            AbsencePathPlatformGate();ProductionOutboxBarrier();FastNetworkCrossesFirstTime();HookLatencyWithSlowNetwork();
            Console.WriteLine("PASS "+checks+" coordinator replay checks; process="+(IntPtr.Size*8)+"bit. Fake platform only: no input injected, no window or socket used.");
            return 0;
        }
        catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
}
