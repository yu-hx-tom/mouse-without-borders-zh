using System;
using System.Runtime.InteropServices;
using System.Threading;
using MwbTransfer;

internal static class NativeDragStateTests
{
    static int checks;
    static void Check(bool value,string description)
    { if(!value)throw new Exception(description);checks++; }
    static NativeDragSession Session(uint controller)
    { return new NativeDragSession(Guid.NewGuid(),10,controller); }
    static void Roundtrip(uint controller)
    {
        NativeDragSession s=Session(controller);Guid first=Guid.NewGuid(),second=Guid.NewGuid();string path;
        Check(s.LeaveSource(s.Gesture,first,controller,20),"leave source");
        Check(s.AcceptCapture(first,1,@"D:\synthetic.txt"),"capture original native data object");
        Check(s.ReturnToSource(first,controller),"return source");
        Check(s.State==NativeDragSession.Phase.Native && s.Target==s.Source && s.SourcePath==null,"source remains Windows-owned without custom pending");
        Check(!s.EndRemote(first,controller,true,out path),"late remote release cannot end a returned native drag");
        Check(!s.AcceptCapture(first,1,@"D:\old.txt"),"late helper cannot recapture at source");
        Check(s.LeaveSource(s.Gesture,second,controller,20),"leave again with a new capture leg");
        Check(!s.AcceptCapture(first,1,@"D:\old.txt"),"previous capture cannot contaminate second crossing");
        Check(!s.EndRemote(first,controller,true,out path),"previous end cannot terminate second crossing");
        Check(!s.EndRemote(second,99,true,out path),"different controller cannot end gesture");
        Check(s.AcceptCapture(second,1,@"D:\fresh.txt"),"each crossing revalidates live OLE");
        Check(s.EndRemote(second,controller,true,out path) && path==@"D:\fresh.txt","remote release gets captured immutable path");
        Check(s.SourcePath==null,"ended gesture retains no path");
        Check(!s.EndRemote(second,controller,true,out path) && path==null,"duplicate end cannot dispatch twice");
        Check(s.ClaimCleanup(second),"one safe source cleanup requested");
        Check(!s.ClaimCleanup(second),"duplicate cleanup rejected");
        Check(!s.CleanupCompleted(second,false) && s.State==NativeDragSession.Phase.Ending,"failed/unobserved SendInput is not successful cleanup");
        Check(!s.CleanupCompleted(second,true),"OLE release alone cannot stand in for observed tagged UP");
        Check(!s.ObserveCleanupUp(first),"stale tag cannot mark new cleanup released");
        Check(s.ObserveCleanupUp(second),"persist observed UP before waiting for OLE exit");
        Check(!s.CleanupCompleted(second,false)&&s.CleanupUpObserved,"delayed OLE exit retains UP observation for confirmation-only retry");
        Check(!s.ObserveCleanupUp(second),"confirmation retry cannot inject another release");
        Check(s.CleanupCompleted(second,true) && s.State==NativeDragSession.Phase.Ended,"cleanup completes only after tagged hook observation");
        Check(!s.AcceptCapture(second,1,@"D:\late.txt"),"post-completion capture ignored");
    }
    static void ReleaseBeforeCapture()
    {
        NativeDragSession s=Session(10);Guid leg=Guid.NewGuid();string path;
        s.LeaveSource(s.Gesture,leg,10,20);
        Check(s.EndRemote(leg,10,true,out path)&&path==null,"release before capture still ends source but cannot transfer");
        Check(!s.AcceptCapture(leg,1,@"D:\late.txt"),"capture after physical release ignored");
        Check(s.ClaimCleanup(leg),"release-before-capture cleans held system state");
    }
    static void CancelAndUnsupported()
    {
        foreach(int count in new[]{1,2})
        {
            NativeDragSession s=Session(20);Guid leg=Guid.NewGuid();string path;
            s.LeaveSource(s.Gesture,leg,20,30);
            Check(s.AcceptCapture(leg,count,@"D:\example.txt")== (count==1),"multiselect is not silently truncated");
            Check(!s.AcceptCapture(leg,1,@"D:\replacement.txt"),"helper reply is single-use including unsupported captures");
            Check(s.EndRemote(leg,20,false,out path)&&path==null,"remote cancel never dispatches transfer");
        }
        NativeDragSession local=Session(10);Guid localLeg=Guid.NewGuid();
        Check(!local.LeaveSource(local.Gesture,localLeg,10,10),"source cannot become a custom destination");
        Check(local.EndNative(local.Gesture),"local release/cancel lets Windows finish");
        Check(!local.ClaimCleanup(localLeg),"no synthetic release on local drop");
    }
    static void Envelope()
    {
        Guid leg=Guid.NewGuid(),parsed;int count;string path;
        string value=NativeDragCapture.Encode(leg,1,@"D:\测试 | 空格\图纸.txt");
        Check(NativeDragCapture.TryDecode(value,out parsed,out count,out path)&&parsed==leg&&count==1&&path==@"D:\测试 | 空格\图纸.txt","Unicode paths are length-bounded and delimited safely");
        foreach(string malformed in new[]{null,"",@"D:\legacy.txt",value+"|extra",value.Replace("/1|","/2|"),value.Replace("|1|","|-1|"),value.Substring(0,value.LastIndexOf('|')+1)+"/w=="})
            Check(!NativeDragCapture.TryDecode(malformed,out parsed,out count,out path)&&parsed==Guid.Empty&&count==0&&path==null,"reject malformed, legacy or invalid UTF-8 envelope");
    }
    static void Tags()
    {
        NativeInputTag tags=new NativeInputTag();Guid leg=Guid.NewGuid();IntPtr token=tags.Register(leg);tags.Arm(token);
        Check(tags.InspectValue(0x202,0,token,leg)==NativeInputDisposition.Ordinary,"physical up cannot consume tagged cleanup");
        Check(tags.InspectValue(0x200,1,token,leg)==NativeInputDisposition.Ordinary,"move cannot consume up token");
        Check(tags.InspectValue(0x202,1,IntPtr.Zero,leg)==NativeInputDisposition.Ordinary,"unrelated injection retains original route");
        IntPtr data=Marshal.AllocHGlobal(IntPtr.Size==8?32:24);
        try
        {
            for(int i=0;i<(IntPtr.Size==8?32:24);i++)Marshal.WriteByte(data,i,0);
            Marshal.WriteInt32(data,12,1);Marshal.WriteIntPtr(data,IntPtr.Size==8?24:20,token);
            Check(tags.Inspect(-1,0x202,data,leg)==NativeInputDisposition.Ordinary,"negative hook code is untouched");
            Check(tags.Inspect(0,0x202,data,leg)==NativeInputDisposition.PassToWindows,"pointer-width tag allows just the valid cleanup through to OLE");
        }
        finally { Marshal.FreeHGlobal(data); }
        Check(tags.WasObserved(token),"matching hook receipt recorded");
        Check(tags.InspectValue(0x202,1,token,leg)==NativeInputDisposition.SuppressStale,"duplicate cleanup cannot release a later gesture");
        IntPtr failed=tags.Register(leg);tags.Arm(failed);tags.Retire(failed);
        Check(!tags.WasObserved(failed)&&tags.InspectValue(0x202,1,failed,leg)==NativeInputDisposition.SuppressStale,"failed/retired injection cannot leak into later physical input");
        IntPtr old=tags.Register(leg);tags.Arm(old);
        Check(tags.InspectValue(0x202,1,old,Guid.NewGuid())==NativeInputDisposition.SuppressStale,"late injected up is swallowed after generation changed");
        Check(tags.InspectValue(0x202,0,old,leg)==NativeInputDisposition.Ordinary,"late token never consumes a real user up");
    }
    static void CleanupRetryAndCancel()
    {
        var session=Session(10);var leg=Guid.NewGuid();string path;
        session.LeaveSource(session.Gesture,leg,10,20);session.AcceptCapture(leg,1,@"D:\synthetic.txt");
        session.EndRemote(leg,10,true,out path);session.ClaimCleanup(leg);
        Check(session.PendingTransferPath==path,"first cleanup timeout retains active transfer path for retry");
        Check(!session.CleanupExternallyCompleted(Guid.NewGuid(),true),"stale external release cannot finish current leg");
        Check(!session.CleanupExternallyCompleted(leg,false),"unconfirmed external cancellation cannot unlock input");
        session.CancelPendingTransfer();
        Check(session.PendingTransferPath==null,"Esc during Ending cancels pending transmission");
        Check(session.CleanupExternallyCompleted(leg,true)&&session.State==NativeDragSession.Phase.Ended,"confirmed external release exits Ending without waiting for a synthetic UP");
        Check(session.PendingTransferPath==null&&!session.CleanupExternallyCompleted(leg,true),"external release cannot replay a transfer or finish twice");
        var normal=Session(10);leg=Guid.NewGuid();normal.LeaveSource(normal.Gesture,leg,10,20);normal.AcceptCapture(leg,1,@"D:\synthetic.txt");
        normal.EndRemote(leg,10,true,out path);normal.ClaimCleanup(leg);
        Check(!normal.CleanupCompleted(leg,false)&&normal.PendingTransferPath==path,"retry failure cannot discard the active offer");
        normal.ObserveCleanupUp(leg);Check(normal.CleanupCompleted(leg,true)&&normal.PendingTransferPath==null,"successful cleanup clears the transient path");

        var tags=new NativeInputTag();var token=tags.Register(leg);tags.Arm(token);
        using(var start=new ManualResetEvent(false))
        {
            NativeInputDisposition result=NativeInputDisposition.Ordinary;
            var hook=new Thread(delegate(){start.WaitOne();Thread.Sleep(30);result=tags.InspectValue(0x202,1,token,leg);});
            hook.Start();Check(!tags.WasObserved(token),"queue insertion can precede hook observation");start.Set();
            for(int i=0;i<50&&!tags.WasObserved(token);i++)Thread.Sleep(10);
            bool accepted=tags.RetireAndWasObserved(token);hook.Join();
            Check(accepted&&result==NativeInputDisposition.PassToWindows,"delayed hook retains authority until observation");
            Check(tags.InspectValue(0x202,1,token,leg)==NativeInputDisposition.SuppressStale,"accepted token still rejects duplicate UP");
        }
    }
    static void DwordInputChain()
    {
        var reserved=new IntPtr(12345);var tags=new NativeInputTag(reserved);var leg=Guid.NewGuid();
        for(int i=0;i<64;i++)
        {
            IntPtr sent=tags.Register(leg);tags.Arm(sent);
            IntPtr callback=new IntPtr((long)unchecked((uint)sent.ToInt64()));
            Check(sent.ToInt64()>0&&sent.ToInt64()<=Int32.MaxValue&&sent!=reserved,"tag survives DWORD truncation and excludes normal-move marker");
            Check(tags.InspectValue(0x202,1,callback,leg)==NativeInputDisposition.PassToWindows,"low-DWORD callback recognizes its own cleanup UP");
            Check(tags.RetireAndWasObserved(sent),"low-DWORD callback updates the registered token");
        }
    }
    // v9.0.0.7 additions -----------------------------------------------------------
    static void ClaimAbandonAndReject()
    {
        string path;var s=Session(10);var leg=Guid.NewGuid();
        s.LeaveSource(s.Gesture,leg,10,20);s.AcceptCapture(leg,1,@"D:\a.txt");s.EndRemote(leg,10,true,out path);
        Check(!s.CleanupClaimed,"Ending starts unclaimed");
        Check(s.ClaimCleanup(leg)&&s.CleanupClaimed,"a timer retry may claim when it runs before the first worker");
        Check(!s.ClaimCleanup(leg),"claim is still single per leg");
        Check(s.ObserveCleanupUp(leg),"claimed retry can record its observed UP (v9.0.0.6 could not)");
        var a=Session(10);var legA=Guid.NewGuid();
        a.LeaveSource(a.Gesture,legA,10,20);a.AcceptCapture(legA,1,@"D:\b.txt");a.EndRemote(legA,10,true,out path);
        Check(!a.Abandon(Guid.NewGuid()),"stale leg cannot abandon");
        Check(a.Abandon(legA)&&a.State==NativeDragSession.Phase.Ended&&a.PendingTransferPath==null,"abandon ends Ending without authorizing a transfer, even unclaimed");
        Check(!a.Abandon(legA),"abandon is single-use");
        var r=Session(10);var legR=Guid.NewGuid();r.LeaveSource(r.Gesture,legR,10,20);
        Check(r.RejectCapture(legR)&&r.State==NativeDragSession.Phase.Capturing,"unsupported capture is consumed but never becomes Remote");
        Check(!r.AcceptCapture(legR,1,@"D:\folder"),"a rejected leg cannot later accept a capture");
        Check(!r.RejectCapture(legR),"reject is single-use");
        Check(r.EndRemote(legR,10,true,out path)&&path==null,"release after rejection still runs cleanup and transfers nothing");
    }
    static void TagRetention()
    {
        DateTime now=new DateTime(2026,10,12,0,0,0,DateTimeKind.Utc);
        var tags=new NativeInputTag(new IntPtr(7));tags.Clock=delegate{return now;};var leg=Guid.NewGuid();
        var first=tags.Register(leg);tags.Arm(first);tags.RetireAndWasObserved(first);
        for(int i=0;i<NativeInputTag.Limit+904;i++){var t=tags.Register(leg);tags.Arm(t);tags.Retire(t);now=now.AddSeconds(1);}
        Check(tags.LiveCount<=NativeInputTag.Limit,"5000 cleanups exceed the old 4096 lifetime cap without failing");
        Check(tags.InspectValue(0x202,1,first,leg)==NativeInputDisposition.SuppressStale,"an expired token's very late delivery is still swallowed, not treated as input");
        Check(tags.InspectValue(0x202,0,first,leg)==NativeInputDisposition.Ordinary,"a physical up is never affected by expired tokens");
        var live=tags.Register(leg);tags.Arm(live);
        Check(tags.InspectValue(0x202,1,live,leg)==NativeInputDisposition.PassToWindows,"new tokens keep exact matching after pruning");
    }
    // v9.0.0.8: Codex probe — 65538 retire cycles must not make the first token Ordinary.
    static void TagNamespace()
    {
        DateTime now=new DateTime(2026,10,12,0,0,0,DateTimeKind.Utc);
        var reserved=new IntPtr(99);var tags=new NativeInputTag(reserved);tags.Clock=delegate{return now;};
        IntPtr first=IntPtr.Zero;var seen=new System.Collections.Generic.HashSet<IntPtr>();bool unique=true;
        for(int i=0;i<70000;i++)
        {
            var t=tags.Register(Guid.NewGuid());if(i==0)first=t;
            if(!seen.Add(t)||t==reserved||t.ToInt64()<=0||t.ToInt64()>Int32.MaxValue)unique=false;
            tags.Retire(t);now=now.AddMinutes(3);
        }
        Check(unique,"70000 issued tokens: positive 31-bit, never the normal tag, never reissued");
        Check(tags.LiveCount<=2,"retired tokens leave the live table");
        Check(tags.InspectValue(0x202,1,first,Guid.Empty)==NativeInputDisposition.SuppressStale,"the first token is still recognised as ours and refused after 70000 cycles");
        Check(tags.InspectValue(0x202,0,first,Guid.Empty)==NativeInputDisposition.Ordinary,"a physical UP is never affected");
        var other=new NativeInputTag();
        Check(other.InspectValue(0x202,1,first,Guid.Empty)==NativeInputDisposition.Ordinary||other.Issued(first),"values outside a namespace stay ordinary input");
        Check(!tags.Issued(new IntPtr(-5))&&!tags.Issued(IntPtr.Zero),"non-positive values are never ours");
        Check(!tags.Issued(reserved),"a reserved value (normal/cleanup-move tag) is never classified as an issued token");
    }
    // v9.0.0.9: OLE evidence per leg disables the absence path.
    static void OleEvidence()
    {
        string path;
        var s=Session(10);var a=Guid.NewGuid();s.LeaveSource(s.Gesture,a,10,20);
        Check(!s.OleEvidence,"a fresh leg has no OLE evidence");
        s.AcceptCapture(a,1,@"D:\f.txt");Check(s.OleEvidence,"an accepted capture is OLE evidence");
        s.EndRemote(a,10,true,out path);Check(s.OleEvidence,"evidence survives into Ending");
        var r=Session(10);var b=Guid.NewGuid();r.LeaveSource(r.Gesture,b,10,20);r.RejectCapture(b);
        Check(r.OleEvidence,"an unsupported (rejected) capture is also OLE evidence");
        var m=Session(10);var c=Guid.NewGuid();m.LeaveSource(m.Gesture,c,10,20);m.AcceptCapture(c,3,@"D:\x");
        Check(m.OleEvidence,"a multi-item reply is OLE evidence even though it is refused");
        var n=Session(10);var d=Guid.NewGuid();n.LeaveSource(n.Gesture,d,10,20);n.EndRemote(d,10,true,out path);
        Check(!n.OleEvidence,"release before any capture reply carries no OLE evidence");
        var k=Session(10);var e1=Guid.NewGuid();k.LeaveSource(k.Gesture,e1,10,20);k.AcceptCapture(e1,1,@"D:\f.txt");k.ReturnToSource(e1,10);
        var e2=Guid.NewGuid();k.LeaveSource(k.Gesture,e2,10,20);
        Check(!k.OleEvidence,"evidence is per leg: a new leg starts without it");
    }
    // v9.0.0.10 (Codex 9.0.0.9 P2-3): cleanup moves carry a per-attempt token.
    static void MoveTokens()
    {
        var normal=new IntPtr(12345);var tags=new NativeInputTag(normal);Guid a=Guid.NewGuid(),b=Guid.NewGuid();
        var moveA=tags.RegisterMove(a);
        Check(tags.InspectMove(1,moveA)==NativeInputDisposition.PassToWindows,"a live move token passes");
        tags.Retire(moveA);
        Check(tags.InspectMove(1,moveA)==NativeInputDisposition.SuppressStale,"a retired move token is refused");
        var moveB=tags.RegisterMove(b);
        Check(moveB!=moveA&&tags.InspectMove(1,moveA)==NativeInputDisposition.SuppressStale&&tags.InspectMove(1,moveB)==NativeInputDisposition.PassToWindows,"while B's attempt runs, A's move is still refused; only B's passes");
        var upB=tags.Register(b);tags.Arm(upB);
        Check(tags.InspectMove(1,upB)==NativeInputDisposition.SuppressStale,"an UP token never authorises a move");
        Check(tags.InspectValue(0x202,1,moveB,b)==NativeInputDisposition.SuppressStale,"a move token never authorises an UP");
        Check(tags.InspectValue(0x202,1,upB,b)==NativeInputDisposition.PassToWindows,"the UP token still works for its own leg");
        tags.RetireLeg(b);
        Check(tags.InspectMove(1,moveB)==NativeInputDisposition.SuppressStale,"invalidating the leg retires its move token");
        Check(tags.InspectMove(1,normal)==NativeInputDisposition.Ordinary&&tags.InspectMove(0,moveB)==NativeInputDisposition.Ordinary&&tags.InspectMove(1,new IntPtr(-7))==NativeInputDisposition.Ordinary,"normal-tag, physical and foreign moves stay ordinary");
        // A move delivered long after its record was pruned is still recognised as ours.
        DateTime now=new DateTime(2026,10,12,0,0,0,DateTimeKind.Utc);
        var late=new NativeInputTag(normal);late.Clock=delegate{return now;};
        var old=late.RegisterMove(a);late.Retire(old);now=now.Add(NativeInputTag.Linger).AddMinutes(1);
        late.RegisterMove(b);   // registration prunes expired records
        Check(late.LiveCount==1&&late.InspectMove(1,old)==NativeInputDisposition.SuppressStale,"a pruned (long-retired) move token is still refused, never ordinary");
    }
    static int Main()
    {
        try { Roundtrip(10);Roundtrip(30);ReleaseBeforeCapture();CancelAndUnsupported();Envelope();Tags();CleanupRetryAndCancel();DwordInputChain();ClaimAbandonAndReject();TagRetention();TagNamespace();OleEvidence();MoveTokens();
            Console.WriteLine("PASS "+checks+" lifecycle/envelope/tag assertions; process="+(IntPtr.Size*8)+"bit. No input injected; no OS drag or network integration claimed.");return 0; }
        catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
}
