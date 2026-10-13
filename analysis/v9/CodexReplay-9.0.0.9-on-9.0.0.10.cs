using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using MwbTransfer;

// Codex 9.0.0.9 counterexamples (CodexRegression-9.0.0.9-original.cs), replayed step by
// step against 9.0.0.10. Same schedules, same fake platform. Two adaptations only:
//  - CleanupMovesAllowed no longer exists; the hook verdict alone is checked.
//  - Where 9.0.0.10 DECLINES a crossing, MWB keeps the cursor at the edge and retries on
//    the next mouse event; the replay does the same (flushing the outbox = the background
//    writer running in the meantime) and records how the caller was treated.
// Every line states the observed outcome; "FIXED" means the defect no longer reproduces.
static class CodexReplay
{
    static int checks;
    static void Fixed(bool ok,string text){if(!ok)throw new Exception("STILL REPRODUCES: "+text);checks++;Console.WriteLine("FIXED "+text);}
    static void Packet(FakePlatform f,int type,uint peer,Guid id,uint arg){NativeDragRuntime.Packet(NativeWire.Encode(type,peer,f.LocalId,id,arg),new FakeConnection(peer));}
    static void Confirm(FakePlatform f,uint peer){Packet(f,80,peer,NativeWire.NewId(),1);f.Flush();}
    static void Mouse(FakePlatform f,uint peer,int msg){byte[] b=new byte[64];Array.Copy(BitConverter.GetBytes(123),b,4);Array.Copy(BitConverter.GetBytes(peer),0,b,8,4);Array.Copy(BitConverter.GetBytes(f.LocalId),0,b,12,4);Array.Copy(BitConverter.GetBytes(msg),0,b,28,4);NativeDragRuntime.Packet(b,new FakeConnection(peer));}
    static int HookMove(IntPtr tag){IntPtr d=Marshal.AllocHGlobal(32);try{for(int i=0;i<32;i+=4)Marshal.WriteInt32(d,i,0);Marshal.WriteInt32(d,12,1);Marshal.WriteIntPtr(d,IntPtr.Size==8?24:20,tag);return NativeDragRuntime.InspectMouse(0,0x200,d);}finally{Marshal.FreeHGlobal(d);}}
    // MWB edge retry: declined -> background writer runs -> next mouse event retries.
    static bool Retry(FakePlatform f,uint next,out bool declinedFirst)
    {
        declinedFirst=!NativeDragRuntime.BeforeSwitch(next);
        if(!declinedFirst)return true;
        f.Flush();return NativeDragRuntime.BeforeSwitch(next);
    }
    static int Index(FakePlatform f,int type,Guid id){for(int i=0;i<f.Packets.Count;i++)if(f.Packets[i][0]==type&&f.PacketIds[i]==id)return i;return -1;}

    static void ResumeOvertakesArmSource()
    {
        var c=new FakePlatform{LocalId=10,PointerId=20};NativeDragRuntime.ResetForTests(c);Confirm(c,20);
        NativeDragRuntime.LocalMouse(0x201);
        bool declined;
        Fixed(Retry(c,10,out declined),"two-PC remote source departure still accepted");c.PointerId=10;NativeDragRuntime.LocalMouse(0x200);
        Guid a=Guid.Empty;
        Fixed(c.Queued()==1,"ArmSource A queued (unchanged precondition)");
        bool accepted=NativeDragRuntime.BeforeSwitch(20);
        Fixed(!accepted&&c.LastId(85)==Guid.Empty,"return to S is DECLINED while ArmSource A is unsent; no Resume A written ahead of it");
        c.Flush();a=c.LastId(82);
        Fixed(NativeDragRuntime.BeforeSwitch(20),"the retried return is accepted once ArmSource A is on the wire");c.PointerId=20;
        c.Flush();
        Fixed(Index(c,82,a)>=0&&Index(c,85,a)>Index(c,82,a),"S receives ArmSource A before Resume A");
        Fixed(Retry(c,10,out declined),"second departure accepted (after edge retry)");c.PointerId=10;NativeDragRuntime.LocalMouse(0x200);
        c.Flush();Guid b=c.LastId(82);
        Fixed(b!=a&&Index(c,82,b)>Index(c,85,a),"ArmSource B follows Resume A; nothing is delivered out of gesture order");
        // Source side, Codex's ORIGINAL inverted delivery (as an old 9.0.0.9 controller would send it).
        var s=new FakePlatform{LocalId=20,PointerId=20};NativeDragRuntime.ResetForTests(s);Mouse(s,10,0x201);
        Packet(s,85,10,a,0);Mouse(s,10,0x200);
        Packet(s,82,10,a,10);Packet(s,82,10,b,10);
        NativeDragRuntime.CaptureReply(NativeDragCapture.Encode(a,1,@"C:\synthetic-only.bin"));s.Flush();
        Fixed(s.LastId(84)!=a&&NativeDragRuntime.StaleArms==1&&NativeDragRuntime.SourceState=="Capturing","source refuses stale A (already resumed), arms current B, never announces Ready for A");
        Packet(s,87,10,b,10);s.Flush();
        Fixed(NativeDragRuntime.SourceState!="Remote"&&NativeDragRuntime.SourceState!="Capturing","Drop B ends B");
        Guid unknown=NativeWire.NewId();Packet(s,87,10,unknown,10);s.Flush();
        Fixed(s.Count(92,10)>=1,"an end for an unknown leg gets an Ended reply (controller not left waiting)");
    }

    static void ActualCrossingLatency()
    {
        var f=new FakePlatform{LocalId=10,PointerId=30};NativeDragRuntime.ResetForTests(f);
        Confirm(f,20);Confirm(f,30);Confirm(f,40);NativeDragRuntime.LocalMouse(0x201);
        bool declined;Retry(f,20,out declined);f.PointerId=20;NativeDragRuntime.LocalMouse(0x200);f.Flush();
        f.SlowMilliseconds=400;
        var sw=Stopwatch.StartNew();bool first=NativeDragRuntime.BeforeSwitch(40);sw.Stop();long firstMs=sw.ElapsedMilliseconds;
        f.Flush(); // the background writer pays the 400 ms writes, not the hook
        sw.Restart();bool second=NativeDragRuntime.BeforeSwitch(40);sw.Stop();long secondMs=sw.ElapsedMilliseconds;
        Fixed(!first&&second&&f.Count(83,40)==1,"the 20->40 crossing really happens (declined once, accepted after ArmTarget(40) was written)");
        Fixed(firstMs<50&&secondMs<50,"hook-side calls took "+firstMs+" ms and "+secondMs+" ms with 400 ms writes (was 1216 ms)");
        Fixed(f.SendUnderGate==0&&f.Queued()==2,"Leave(20)+Resume(30) are only queued by the accepted crossing; no write on the caller");
    }

    static Guid EndLocal(FakePlatform f)
    {
        Confirm(f,20);NativeDragRuntime.LocalMouse(0x201);bool declined;Retry(f,20,out declined);
        Guid id=f.LastId(83);f.PointerId=20;NativeDragRuntime.LocalMouse(0x200);f.Pump();
        NativeDragRuntime.LocalMouse(0x202);return id;
    }
    static void MoveTagReopensForNextLeg()
    {
        var f=new FakePlatform();NativeDragRuntime.ResetForTests(f);EndLocal(f);IntPtr oldMove=IntPtr.Zero;
        f.DuringSleep=delegate{oldMove=f.LastMoveTag;};f.Pump();
        Fixed(oldMove!=IntPtr.Zero&&HookMove(oldMove)==-1,"old cleanup move is blocked after A finishes (unchanged)");
        f.PointerId=10;f.LeftDown=true;EndLocal(f);int during=0,own=0;IntPtr newMove=IntPtr.Zero;
        f.DuringSleep=delegate{newMove=f.LastMoveTag;during=HookMove(oldMove);own=HookMove(newMove);};f.Pump();
        Fixed(during==-1&&own==1&&newMove!=oldMove,"the stale move from A stays blocked during B's cleanup; B's own token passes");
    }
    static int Main(){try{ResumeOvertakesArmSource();ActualCrossingLatency();MoveTagReopensForNextLeg();Console.WriteLine("PASS "+checks+" Codex 9.0.0.9 counterexamples no longer reproduce; fake platform only, no input/window/socket operations.");return 0;}catch(Exception ex){Console.WriteLine(ex);return 1;}}
}
