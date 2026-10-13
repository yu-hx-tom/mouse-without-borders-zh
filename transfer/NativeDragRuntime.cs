using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;

namespace MwbTransfer
{
    // v9.0.0.8 rules
    //  * Gate protects coordinator state. While holding Gate nothing waits for another
    //    process or the network: helper IPC runs under HelperIpc only (order
    //    HelperIpc -> Gate), packets go through Platform.Post (an ordered outbox
    //    thread), and connectivity probes run in Tick outside Gate into a cache.
    //  * A synthetic release needs positive evidence, never elapsed time:
    //      strict: an OLE drag loop exists and OLE itself reported None on our sink;
    //      proof:  the capture probe shows NO OLE drag loop on this desktop, so the
    //              UP can only reach our sink or an ordinary capture owner.
    //  * The controller is unlocked only by Ended, a down link, or an explicit Esc.
    //    Unlocking invalidates the old cleanup first (session ended, tokens refused).
    public static class NativeDragRuntime
    {
        const int Hello=80,Ack=81,ArmSource=82,ArmTarget=83,Ready=84,Resume=85,Leave=86,Drop=87,Cancel=88,Commit=89,Abort=90,PushRequest=91,Ended=92,CleanupDelayed=93,Unsupported=94,Recover=95;
        internal const uint EndedReleased=1,EndedAbandoned=2;
        internal const uint UnsupportedMultiple=1,UnsupportedFolder=2;
        internal static readonly TimeSpan TargetGiveUp=TimeSpan.FromSeconds(60);
        // A Commit that precedes the controller's release may wait this long for it.
        internal static readonly TimeSpan ReleaseGrace=TimeSpan.FromSeconds(2);
        // Esc unlocks a waiting controller only after this, so the Esc that cancels a
        // drag in progress is not mistaken for a recovery request.
        internal static readonly TimeSpan EscapeArm=TimeSpan.FromSeconds(1);
        static readonly TimeSpan CapabilityValid=TimeSpan.FromSeconds(30),CapabilityRefresh=TimeSpan.FromSeconds(10);
        static readonly object Gate=new object(),HelperIpc=new object();
        internal static NativePlatform Platform=new NativePlatform();
        sealed class Capability { internal Guid Challenge;internal object Connection;internal DateTime Sent=DateTime.MinValue,Confirmed=DateTime.MinValue; }
        sealed class Target { internal Guid Id;internal uint Source,Controller,Unsupported;internal bool Ready,Released,Committed;internal object Snapshot;internal DateTime ReleasedAt,CommittedAt; }
        // Ready/Unsupported from the source can overtake ArmTarget from the controller
        // (different connections); keep them briefly by id instead of dropping them.
        sealed class Early { internal uint Source,Controller,Unsupported;internal bool Ready;internal DateTime At; }
        static readonly Dictionary<uint,Capability> Peers=new Dictionary<uint,Capability>();
        static readonly Dictionary<Guid,Target> ReleasedTargets=new Dictionary<Guid,Target>();
        static readonly Dictionary<Guid,Early> EarlyNotices=new Dictionary<Guid,Early>();
        // Filled by Tick outside Gate; read without network I/O anywhere else.
        static readonly Dictionary<uint,bool> LinkDown=new Dictionary<uint,bool>();
        // v9.0.0.9: a probe result is committed only if the peer's epoch is unchanged
        // since the probe started; a new wait/target bumps the epoch, so an in-flight
        // verdict from before it is discarded instead of written back.
        static readonly Dictionary<uint,long> LinkEpoch=new Dictionary<uint,long>();
        static long linkEpochCounter;
        static int tickRunning;
        static readonly IntPtr NormalTag=new IntPtr((Guid.NewGuid().GetHashCode()&Int32.MaxValue)|1);
        // v9.0.0.10: cleanup jiggle moves carry a token issued for ONE cleanup attempt
        // (same never-reused namespace as release tokens). The hook passes a move only
        // while that exact token is live; it is retired when the attempt ends or the
        // leg is invalidated, so an old move can never be re-authorised by a later
        // attempt (v9.0.0.9 used one global tag plus a global switch).
        static readonly NativeInputTag Tags=new NativeInputTag(NormalTag);
        // v9.0.0.10 held-crossing barrier (see BeforeSwitchCore): no network on the hook.
        static bool crossPending;static uint crossNext;static Guid crossLeg;static long crossSeq;
        // Source side: legs that already returned/ended or were announced dead before
        // their ArmSource arrived. A late ArmSource for them is refused.
        static readonly HashSet<Guid> RetiredLegs=new HashSet<Guid>();
        static readonly Queue<Guid> RetiredOrder=new Queue<Guid>();
        static NativeDragSession source;
        static Target target;
        static bool down;
        static volatile bool armPending;
        static uint origin,pointer,pendingSource;
        static Guid leg,pendingEnd,notifiedFailure,delayNotified;
        static DateTime pendingEndSince;
        static IntPtr helper;
        // The leg the helper may still hold. Its invalidation is retried until confirmed.
        static Guid helperLeg;
        static bool safeTargetObserved,cleanupRunning,started;
        // Numeric process-local diagnostics only; no filenames or transfer history.
        static int releaseAttempts,releaseInserted,releaseError,releaseHooks,releaseUnknown,releaseRejected,releaseAccepted;
        static int hookTokenLow,hookTokenHigh,sendTokenLow,sendTokenHigh;
        static bool hookEnding,hookOnSink;
        static int ipcUnderGate,proofReleases,proofAttempts,strictReleases,invalidations,recoveries,targetsExpired,staleLinkVerdicts,deferredCrossings,staleArms;

        static void Start(){if(!started){started=true;Platform.StartTimer(Tick);}}
        public static IntPtr GetNormalMouseTag(){return NormalTag;}
        internal static bool GateHeld{get{return Monitor.IsEntered(Gate);}}

        // 0 = existing hook logic; 1 = CallNextHookEx; -1 = suppress our stale up.
        public static int InspectMouse(int code,int message,IntPtr data)
        {
            if(code<0||data==IntPtr.Zero)return 0;
            int flags=Marshal.ReadInt32(data,12);
            // Physical input never carries our tags: no lock here. (Physical button
            // events still reach LocalMouse below, whose Gate sections are short.)
            if((flags&1)==0)return 0;
            IntPtr token=Marshal.ReadIntPtr(data,IntPtr.Size==8?24:20);
            if(token==NormalTag)
            {
                if(message==0x202)lock(Gate)ReleaseTarget();
                return 1;
            }
            if(message==0x200)
            {
                // Our cleanup moves: decided by the token table's own short lock, never Gate.
                NativeInputDisposition move=Tags.InspectMove(flags,token);
                return move==NativeInputDisposition.Ordinary?0:move==NativeInputDisposition.PassToWindows?1:-1;
            }
            if(message!=0x202)return 0;
            lock(Gate)
            {
                Guid ending=Guid.Empty;
                Point p;
                if(source!=null&&source.State==NativeDragSession.Phase.Ending&&safeTargetObserved&&Platform.CursorPos(out p)&&Platform.WindowAt(p)==helper)ending=source.Leg;
                NativeInputDisposition result=Tags.InspectValue(message,flags,token,ending);
                releaseHooks++;hookTokenLow=unchecked((int)token.ToInt64());hookTokenHigh=unchecked((int)(token.ToInt64()>>32));
                hookEnding=source!=null&&source.State==NativeDragSession.Phase.Ending;hookOnSink=ending!=Guid.Empty;
                if(result==NativeInputDisposition.Ordinary)releaseUnknown++;
                else if(result==NativeInputDisposition.SuppressStale)releaseRejected++;
                else releaseAccepted++;
                return result==NativeInputDisposition.Ordinary?0:result==NativeInputDisposition.PassToWindows?1:-1;
            }
        }

        // Called after the existing physical mouse packet is forwarded. Remote
        // injected input has our exact NormalTag and never re-enters this path.
        public static void LocalMouse(int message)
        {
            // Plain moves only matter while an ArmSource is pending: no lock for them.
            if(message!=0x201&&message!=0x202&&message!=0x205&&!armPending)return;
            if(!Platform.InDefaultDesktop())return;
            lock(Gate)
            {
                Start();uint local=Platform.Local;
                if(local<=1||local==255)return;
                if(armPending&&Platform.Pointer==pointer&&leg!=Guid.Empty)
                {armPending=false;Dispatch(ArmSource,origin,leg,pointer);}
                if(message==0x201)
                {
                    // A new physical press on this machine's own screen supersedes any
                    // old cleanup here before Windows sees the press.
                    if(Platform.Pointer==local)InvalidateSource();
                    down=true;armPending=false;origin=pointer=Platform.Pointer;leg=Guid.Empty;Platform.SetDown(true);
                    if(origin==local && (source==null||source.State==NativeDragSession.Phase.Ended))source=new NativeDragSession(Guid.NewGuid(),local,local);
                }
                else if(message==0x202||message==0x205)
                {
                    if(message==0x202&&target!=null&&target.Controller==local)ReleaseTarget();
                    EndController(message==0x202);
                }
            }
        }
        public static void Escape()
        {
            lock(Gate)
            {
                if(source!=null&&source.State==NativeDragSession.Phase.Ending)source.CancelPendingTransfer();
                // Explicit user recovery of a waiting controller (never automatic).
                if(pendingEnd!=Guid.Empty&&Platform.Now-pendingEndSince>=EscapeArm)RecoverController();
                if(down)EndController(false);
            }
        }
        public static void Keyboard(int code,int message,IntPtr data)
        {
            if(code>=0&&data!=IntPtr.Zero&&(message==0x100||message==0x104)&&Marshal.ReadInt32(data)==27&&(Marshal.ReadInt32(data,8)&0x10)==0)Escape();
        }
        static void EndController(bool dropped)
        {
            if(!down)return;down=false;armPending=false;Platform.SetDown(false);
            CancelPendingCross();
            if(pointer==origin)
            {
                if(source!=null&&source.Controller==Platform.Local)source.EndNative(source.Gesture);
                leg=Guid.Empty;return;
            }
            if(leg==Guid.Empty)return;
            pendingEnd=leg;pendingSource=origin;pendingEndSince=Platform.Now;
            // A new wait starts with no link verdict; only a probe started after now counts.
            ResetLink(origin);
            // Original input UP was sent before this terminal notification.
            Dispatch(dropped?Drop:Cancel,origin,leg,pointer);
        }

        // Runs at the audited switch point, before evSwitch and cursor migration.
        // v9.0.0.10: never writes to the network (only enqueues), on any thread.
        // On a LAN the outbox writes the new ArmTarget within a millisecond, so the hook
        // waits for it at most CrossWaitMs (outside Gate, on the outbox monitor only) and
        // the crossing normally succeeds at the first attempt. On a slow link the attempt
        // is declined after that bound and MWB retries (>=100 ms later, see Core).
        internal const int CrossWaitMs=20;
        public static bool BeforeSwitch(uint next)
        {
            if(!Platform.InDefaultDesktop())return true;
            long wait;
            lock(Gate)
            {
                if(BeforeSwitchCore(next))return true;
                wait=crossPending&&crossNext==next?crossSeq:0;
            }
            if(wait<=0||!Platform.WaitWritten(wait,CrossWaitMs))return false;
            lock(Gate)return BeforeSwitchCore(next);
        }
        // Ordering without blocking the hook (v9.0.0.10). All protocol packets share one
        // FIFO outbox, so they keep their mutual order. Relative to the ORIGINAL program's
        // mouse packets (written synchronously by its hook), a held crossing is DECLINED
        // (PrepareToSwitchToMachine has already stamped lastJump, and a remote pointer's
        // SendNextMachine is throttled the same way, so MWB retries on the next mouse
        // movement at the edge at least 100 ms later; audited IL) until every packet that
        // must precede that input is already on the wire:
        //   - into a target: the ArmTarget posted at the first attempt (and, FIFO, all
        //     earlier posts);
        //   - back to the source: everything posted so far (e.g. a still-queued ArmSource),
        //     so the source can never receive a departure after the return.
        // Packets posted once the crossing is accepted (Leave/Resume/ArmSource) need no
        // precedence over input: the source handles a return on the first mouse packet,
        // and no input is sent to a target that was left.
        static bool BeforeSwitchCore(uint next)
        {
            Start();
            if(down&&(next<=1||next==255||origin<=1||origin==255))return false;
            if(pendingEnd!=Guid.Empty&&IsLinkDown(pendingSource))ReleaseController("来源电脑已断开连接，已允许鼠标切回，本次文件未传输。\n重新连接后若那台电脑上文件图标仍跟随光标，请在那里按 Esc 取消，不要直接单击。");
            if(pendingEnd!=Guid.Empty && (next==pendingSource||down))return false;
            if(!down){CancelPendingCross();return true;}
            if(next==pointer)return true;
            if(!Capable(origin)||!Capable(next))return false;
            if(!crossPending||crossNext!=next)
            {
                CancelPendingCross();
                crossPending=true;crossNext=next;crossLeg=Guid.Empty;
                if(next!=origin){crossLeg=NativeWire.NewId();crossSeq=Dispatch(ArmTarget,next,crossLeg,origin);}
                else crossSeq=Platform.LastPosted;
            }
            if(crossSeq>0&&!Platform.Written(crossSeq)){deferredCrossings++;return false;}
            // Barrier passed: the crossing happens now.
            if(leg!=Guid.Empty)
            {
                Dispatch(Leave,pointer,leg,0);
                Dispatch(Resume,origin,leg,0);
            }
            pointer=next;leg=crossLeg;armPending=leg!=Guid.Empty;
            crossPending=false;crossLeg=Guid.Empty;crossSeq=0;
            return true;
        }
        // A target was armed for a crossing that did not happen (direction changed or the
        // button was released at the edge): un-arm it. No input ever reached it.
        static void CancelPendingCross()
        {
            if(!crossPending)return;
            if(crossLeg!=Guid.Empty)Dispatch(Leave,crossNext,crossLeg,0);
            crossPending=false;crossLeg=Guid.Empty;crossSeq=0;
        }
        static void RetireLeg(Guid id)
        {
            if(id==Guid.Empty||!RetiredLegs.Add(id))return;
            RetiredOrder.Enqueue(id);
            while(RetiredOrder.Count>256)RetiredLegs.Remove(RetiredOrder.Dequeue());
        }
        static bool IsLinkDown(uint peer){bool value;return peer!=Platform.Local&&LinkDown.TryGetValue(peer,out value)&&value;}
        static void ResetLink(uint peer){LinkDown.Remove(peer);LinkEpoch[peer]=++linkEpochCounter;}
        static long EpochOf(uint peer){long value;return LinkEpoch.TryGetValue(peer,out value)?value:0;}
        static void ReleaseController(string warning)
        {
            pendingEnd=Guid.Empty;pendingSource=0;
            if(warning!=null)Platform.Message("拖放未完成",warning);
        }
        // User pressed Esc while waiting. The old cleanup is invalidated BEFORE the
        // pointer may return, so none of its input can act on new input.
        static void RecoverController()
        {
            uint s=pendingSource;Guid id=pendingEnd;
            ReleaseController(null);recoveries++;
            // Local source: invalidated now, under Gate. Remote source: queued Recover, and
            // the source also retires the leg on the first returning mouse packet, before
            // that input is injected (does not depend on Recover's arrival order).
            Dispatch(Recover,s,id,0);
            Platform.Message("已解除等待","已按 Esc 解除等待，可以切回来源电脑，本次文件未传输。\n若那台电脑上文件图标仍跟随光标，请在那台电脑上再按一次 Esc 取消，不要直接单击。");
        }
        static bool Capable(uint peer)
        {
            if(peer==Platform.Local)return true;
            Capability c;DateTime now=Platform.Now;
            if(Peers.TryGetValue(peer,out c)&&now-c.Confirmed<CapabilityValid&&Platform.ConnectionAlive(c.Connection))return true;
            if(c==null){c=new Capability();Peers[peer]=c;}
            if(now-c.Sent>TimeSpan.FromSeconds(1)){c.Challenge=NativeWire.NewId();c.Sent=now;Send(Hello,peer,c.Challenge,1);}
            return false;
        }
        // Keep confirmed peers fresh so a held crossing is not refused once per idle period.
        static void RefreshCapabilities(DateTime now)
        {
            foreach(KeyValuePair<uint,Capability> pair in Peers)
            {
                Capability c=pair.Value;
                if(c.Confirmed==DateTime.MinValue||now-c.Confirmed<CapabilityRefresh||now-c.Sent<TimeSpan.FromSeconds(2)||!Platform.ConnectionAlive(c.Connection))continue;
                c.Challenge=NativeWire.NewId();c.Sent=now;Send(Hello,pair.Key,c.Challenge,1);
            }
        }
        public static bool Packet(byte[] bytes,object connection)
        {
            if(bytes==null||bytes.Length<32||connection==null||!Platform.InDefaultDesktop())return false;
            uint peer=Platform.PeerOf(connection);
            uint local=Platform.Local;
            if(BitConverter.ToUInt32(bytes,8)!=peer||BitConverter.ToUInt32(bytes,12)!=local)return false;
            int kind=BitConverter.ToInt32(bytes,0);
            lock(Gate)
            {
                Start();
                if(kind==123) { RemoteMouse(peer,BitConverter.ToInt32(bytes,28));return false; }
                int type;Guid id;uint argument;
                if(!NativeWire.Decode(bytes,peer,local,out type,out id,out argument))return false;
                if(type==Hello)
                {
                    if(argument==1)
                    {
                        Capability c;if(!Peers.TryGetValue(peer,out c)){c=new Capability();Peers[peer]=c;}
                        c.Connection=connection;c.Confirmed=Platform.Now;Send(Ack,peer,id,1);
                    }
                    return true;
                }
                if(type==Ack)
                {
                    Capability c;if(argument==1&&Peers.TryGetValue(peer,out c)&&c.Challenge==id){c.Connection=connection;c.Confirmed=Platform.Now;}
                    return true;
                }
                Handle(type,peer,id,argument);return true;
            }
        }
        static void RemoteMouse(uint controller,int message)
        {
            // The controller only sends input here after it stopped waiting for this
            // source: retire the old cleanup before this input is injected.
            if(source!=null&&source.Controller==controller)InvalidateSource();
            if(message==0x201)
            {
                if(source==null||source.State==NativeDragSession.Phase.Ended)source=new NativeDragSession(Guid.NewGuid(),Platform.Local,controller);
            }
            else if(source!=null&&source.Controller==controller&&(message==0x200||message==0x202))
            {
                // First actual input returning to S invalidates its helper BEFORE
                // the queued SendInput can deliver that movement to Windows.
                if(source.State==NativeDragSession.Phase.Capturing||source.State==NativeDragSession.Phase.Remote)ReturnSource(source.Leg,controller);
                if(message==0x202)source.EndNative(source.Gesture);
            }
        }

        // ---- target side -------------------------------------------------------------
        static Target FindTarget(Guid id)
        {
            if(target!=null&&target.Id==id)return target;
            Target t;return ReleasedTargets.TryGetValue(id,out t)?t:null;
        }
        static void ReleaseTarget()
        {
            if(target==null||target.Released)return;
            Target t=target;target=null;
            // An UP arriving after the grace is not this gesture's release (see Commit).
            if(t.Committed&&Platform.Now-t.CommittedAt>ReleaseGrace)
            {
                Platform.PreviewEnd();targetsExpired++;
                Platform.Message("本次拖放未传输","没有收到这台电脑上的松开位置，本次文件未传输，请重新拖放。");
                return;
            }
            t.Snapshot=Platform.CaptureDrop();t.Released=true;t.ReleasedAt=Platform.Now;Platform.PreviewEnd();
            ReleasedTargets[t.Id]=t;
            if(t.Unsupported!=0)ExplainUnsupported(t.Unsupported);
            CompleteTarget(t);
        }
        static void ExplainUnsupported(uint reason)
        {
            Platform.Message("本次拖放未传输",reason==UnsupportedFolder?"暂不支持直接拖放文件夹，请先压缩为单个文件后再拖放。":"暂不支持一次拖放多个项目，请先压缩为单个文件，或逐个拖放。");
        }
        // Completes only when Released, Ready and Committed are all known; a missing
        // piece keeps the record until it arrives or the record expires.
        static void CompleteTarget(Target t)
        {
            if(t==null||!t.Committed||!t.Released||!t.Ready)return;
            ReleasedTargets.Remove(t.Id);if(Object.ReferenceEquals(target,t))target=null;
            uint peer=t.Source;
            // BeginDrop never touches the preview: another leg may be showing one.
            Platform.BeginDrop(t.Id,Platform.Name(peer),t.Snapshot,delegate(Guid request,string machine){
                Platform.RunLong(delegate{if(!Platform.ConnectFile(machine,request,false))lock(Gate)Dispatch(PushRequest,peer,request,0);});
            });
        }
        static void ExpireTargets(DateTime now)
        {
            if(target!=null&&(IsLinkDown(target.Controller)||IsLinkDown(target.Source)))
            {Platform.PreviewEnd();target=null;targetsExpired++;}
            // Committed but its release never arrived within the grace: close it, so no
            // later unrelated click can complete it at the wrong place (v9.0.0.9).
            else if(target!=null&&target.Committed&&!target.Released&&now-target.CommittedAt>ReleaseGrace)
            {
                Platform.PreviewEnd();target=null;targetsExpired++;
                Platform.Message("本次拖放未传输","没有收到这台电脑上的松开位置，本次文件未传输，请重新拖放。");
            }
            List<Guid> stale=null;
            foreach(KeyValuePair<Guid,Target> pair in ReleasedTargets)
                if(now-pair.Value.ReleasedAt>TargetGiveUp||IsLinkDown(pair.Value.Source)){if(stale==null)stale=new List<Guid>();stale.Add(pair.Key);}
            if(stale!=null)foreach(Guid id in stale)
            {
                Target t=ReleasedTargets[id];ReleasedTargets.Remove(id);targetsExpired++;
                if(t.Committed)Platform.Message("本次拖放未传输","来源电脑的确认信息没有完整到达，本次文件未传输，请重新拖放。");
            }
            List<Guid> old=null;
            foreach(KeyValuePair<Guid,Early> pair in EarlyNotices)if(now-pair.Value.At>TargetGiveUp){if(old==null)old=new List<Guid>();old.Add(pair.Key);}
            if(old!=null)foreach(Guid id in old)EarlyNotices.Remove(id);
        }
        static Early RememberEarly(Guid id,uint sourcePeer)
        {
            Early e;
            if(!EarlyNotices.TryGetValue(id,out e)){if(EarlyNotices.Count>=64)return null;e=new Early{Source=sourcePeer,At=Platform.Now};EarlyNotices[id]=e;}
            return e.Source==sourcePeer?e:null;
        }

        // Never write to a socket here: the outbox thread does, in order. Returns the
        // outbox sequence number (0 = handled locally, nothing to wait for).
        static long Send(int type,uint peer,Guid id,uint argument)
        {
            return Platform.Post(delegate{Platform.Send(type,peer,id,argument);});
        }
        static long Dispatch(int type,uint peer,Guid id,uint argument)
        {
            if(peer==Platform.Local){Handle(type,peer,id,argument);return 0;}
            return Send(type,peer,id,argument);
        }
        static void Handle(int type,uint peer,Guid id,uint argument)
        {
            uint local=Platform.Local;
            switch(type)
            {
                case ArmSource:
                    // A leg that already returned/ended (or was announced dead first) is
                    // never armed late (v9.0.0.10, Codex 9.0.0.9 P1-1).
                    if(RetiredLegs.Contains(id)){staleArms++;return;}
                    if(source==null||source.Controller!=peer||source.State!=NativeDragSession.Phase.Native||argument==local)return;
                    if(!source.LeaveSource(source.Gesture,id,peer,argument))return;
                    Platform.Queue(delegate {Capture(id);});break;
                case ArmTarget:
                {
                    if(argument==local)return;
                    if(target!=null)
                    {
                        // One controller has one pointer: its new leg supersedes an old,
                        // un-released one (a lost Leave). A disconnected owner is stale.
                        if(target.Controller==peer||IsLinkDown(target.Controller)){Platform.PreviewEnd();target=null;}
                        else return;
                    }
                    target=new Target{Id=id,Controller=peer,Source=argument};
                    // Fresh target: stale or in-flight link verdicts must not expire it.
                    ResetLink(peer);ResetLink(argument);
                    Early e;
                    if(EarlyNotices.TryGetValue(id,out e))
                    {
                        EarlyNotices.Remove(id);
                        if(e.Source==argument)
                        {
                            if(e.Unsupported!=0)target.Unsupported=e.Unsupported;
                            if(e.Ready&&e.Controller==peer){target.Ready=true;Platform.PreviewBegin();}
                        }
                    }
                    break;
                }
                case Ready:
                {
                    Target t=FindTarget(id);
                    if(t==null){Early e=RememberEarly(id,peer);if(e!=null){e.Ready=true;e.Controller=argument;}return;}
                    if(t.Source!=peer||t.Controller!=argument||t.Ready)return;
                    t.Ready=true;
                    // A released target never reopens a preview; it may now complete.
                    if(!t.Released)Platform.PreviewBegin();else CompleteTarget(t);
                    break;
                }
                case Unsupported:
                {
                    Target t=FindTarget(id);
                    if(t==null){Early e=RememberEarly(id,peer);if(e!=null)e.Unsupported=argument;return;}
                    if(t.Source!=peer||t.Unsupported!=0)return;
                    t.Unsupported=argument;
                    if(t.Released)ExplainUnsupported(argument);
                    break;
                }
                case Resume: ReturnSource(id,peer);RetireLeg(id);break;
                case Leave:
                    if(target!=null&&target.Id==id&&target.Controller==peer){Platform.PreviewEnd();target=null;}break;
                case Drop:case Cancel:
                {
                    // An end for a leg this source never armed (or already returned): it can
                    // never be armed later, and the controller must not wait for it. Always
                    // answered, whatever this source is doing: the controller matches Ended by
                    // leg id, so the reply cannot release any other wait.
                    if(source==null||source.Leg!=id)
                    {
                        RetireLeg(id);
                        Dispatch(Ended,peer,id,EndedAbandoned);
                        return;
                    }
                    if(source.Target!=argument)return;
                    string path;
                    if(!source.EndRemote(id,peer,type==Drop,out path))return;
                    uint controller=source.Controller,destination=source.Target;
                    Platform.Queue(delegate{FinishSourceAttempt(id,controller,destination,false);});break;
                }
                case Commit:
                {
                    Target t=FindTarget(id);
                    if(t==null||t.Source!=peer||t.Controller!=argument||t.Committed)return;
                    t.Committed=true;t.CommittedAt=Platform.Now;
                    // Commit is sent only after the controller released. If this target
                    // has not seen that release yet, the controller's UP may still be in
                    // flight (S!=C: different connections), so allow a short grace; after
                    // it, ExpireTargets closes the record so a LATER unrelated click can
                    // never complete this transfer (v9.0.0.9).
                    CompleteTarget(t);break;
                }
                case Abort:
                {
                    Target t=FindTarget(id);
                    if(t==null||t.Source!=peer)return;
                    if(Object.ReferenceEquals(target,t)){Platform.PreviewEnd();target=null;}
                    ReleasedTargets.Remove(id);break;
                }
                case PushRequest:
                    Platform.RunLong(delegate{if(!Platform.ConnectFile(Platform.Name(peer),id,true))Platform.FailRequest(id,"无法建立文件连接。");});break;
                case Ended:
                    if(peer==pendingSource&&pendingEnd==id)
                        ReleaseController(argument==EndedReleased?null:"来源电脑上的这次拖拽已被放弃，本次文件未传输。\n若那台电脑上文件图标仍跟随光标，请在那里按 Esc 取消，不要直接单击。");
                    break;
                case CleanupDelayed:
                    if(peer==pendingSource&&pendingEnd==id&&notifiedFailure!=id)
                    {
                        notifiedFailure=id;
                        Platform.Message("等待来源电脑","来源电脑上的原生拖拽还没有安全结束，正在重试，期间暂不能切回那台电脑。\n如需立即切回，请按 Esc（之后若那台电脑上文件图标仍跟随光标，请在那里再按一次 Esc，不要直接单击）。");
                    }
                    break;
                case Recover:
                    RetireLeg(id);
                    if(source!=null&&source.Controller==peer&&source.Leg==id)InvalidateSource();break;
            }
        }

        // ---- source side -------------------------------------------------------------
        // Runs under Gate, possibly on the input hook: never waits for the helper.
        static void ReturnSource(Guid id,uint controller)
        {
            if(source==null||!source.ReturnToSource(id,controller))return;
            RetireLeg(id);
            if(helper!=IntPtr.Zero)Platform.HideHelper(helper);
            // helperLeg names the one leg the helper may hold; never overwrite another.
            if(helperLeg==Guid.Empty)helperLeg=id;
            safeTargetObserved=false;
            Platform.Queue(InvalidateHelper);
        }
        // Ends an Ending leg without any further synthetic input: the session becomes
        // Ended under Gate, so the cleanup worker stops at its next check and the hook
        // refuses every token of this leg (no current Ending leg => SuppressStale).
        static void InvalidateSource()
        {
            if(source==null||source.State!=NativeDragSession.Phase.Ending)return;
            Guid id=source.Leg;uint controller=source.Controller,destination=source.Target;
            if(!source.Abandon(id))return;
            invalidations++;safeTargetObserved=false;
            RetireLeg(id);Tags.RetireLeg(id);   // every live UP/move token of this leg is refused from now on
            Dispatch(Abort,destination,id,controller);
            Dispatch(Ended,controller,id,EndedAbandoned);
            if(helper!=IntPtr.Zero)Platform.HideHelper(helper);
            if(helperLeg==Guid.Empty)helperLeg=id;
            Platform.Queue(InvalidateHelper);
        }
        static bool HelperCall(IntPtr window,Guid id,int command)
        {
            if(Monitor.IsEntered(Gate)){Interlocked.Increment(ref ipcUnderGate);return false;}
            return Platform.HelperCommand(window,id,command);
        }
        static void InvalidateHelper()
        {
            lock(HelperIpc)
            {
                Guid stale;IntPtr window;
                lock(Gate)
                {
                    stale=helperLeg;
                    if(stale==Guid.Empty||(source!=null&&source.Leg==stale&&source.State!=NativeDragSession.Phase.Ended))return;
                    window=helper;
                }
                IntPtr fresh=Platform.LocateHelper();
                // A restarted (or absent) helper cannot hold the old leg any more.
                if(fresh!=window||fresh==IntPtr.Zero)
                { lock(Gate){helper=fresh;if(helperLeg==stale)helperLeg=Guid.Empty;} return; }
                bool done=HelperCall(window,stale,HelperNativeDrag.Invalidate);
                if(!done)Platform.HideHelper(window);
                lock(Gate)if(done&&helperLeg==stale)helperLeg=Guid.Empty;
            }
        }
        public static void CaptureReply(string message)
        {
            Guid id;int count;string path;
            if(!NativeDragCapture.TryDecode(message,out id,out count,out path))return;
            uint reason=0;
            if(count!=1)reason=UnsupportedMultiple;
            else { bool folder=false;try{folder=Platform.IsDirectory(path);}catch{} if(folder)reason=UnsupportedFolder; }
            lock(Gate)
            {
                if(source==null)return;
                if(reason!=0){if(source.RejectCapture(id))Dispatch(Unsupported,source.Target,id,reason);return;}
                if(!source.AcceptCapture(id,count,path))return;
                Dispatch(Ready,source.Target,id,source.Controller);
            }
        }
        static void Capture(Guid id)
        {
            try
            {
                IntPtr window;
                lock(HelperIpc)
                {
                    Guid previous;
                    lock(Gate){if(!IsSource(id,NativeDragSession.Phase.Capturing))return;previous=helperLeg;}
                    window=Platform.LocateHelper();
                    if(window==IntPtr.Zero)return;
                    if(previous!=Guid.Empty&&previous!=id)
                    {
                        if(!HelperCall(window,previous,HelperNativeDrag.Invalidate)){lock(Gate)helper=window;return;}
                        lock(Gate)if(helperLeg==previous)helperLeg=Guid.Empty;
                    }
                    lock(Gate){if(!IsSource(id,NativeDragSession.Phase.Capturing))return;helper=window;helperLeg=id;}
                    if(!HelperCall(window,id,HelperNativeDrag.ArmCapture))return;
                }
                for(int i=0;i<25;i++)
                {
                    lock(Gate)
                    {
                        if(!IsSource(id,NativeDragSession.Phase.Capturing))return;
                        Point p;if(!Platform.CursorPos(out p))return;
                        // Asynchronous placement under Gate: a return to S, which also
                        // takes Gate, can never be followed by a stale placement.
                        Platform.PlaceHelper(window,new Rectangle(p.X-10+(i&1),p.Y-10,20,20));
                    }
                    Platform.Sleep(20);
                }
                lock(Gate)if(IsSource(id,NativeDragSession.Phase.Capturing))Platform.HideHelper(window);
            }
            catch { }
        }
        static bool IsSource(Guid id,NativeDragSession.Phase phase){return source!=null&&source.Leg==id&&source.State==phase;}

        const int Pending=0,ReleasedByOle=1,ReleasedByProof=2,ExternallyReleased=3;
        static Guid strictUpLeg;
        static void FinishSourceAttempt(Guid id,uint controller,uint destination,bool retry)
        {
            lock(Gate)
            {
                if(!IsSource(id,NativeDragSession.Phase.Ending)||cleanupRunning)return;
                // The first worker or a timer retry may get here first; whichever
                // does claims the leg (v9.0.0.6 lost the claim to an early retry).
                if(!source.CleanupClaimed){if(!source.ClaimCleanup(id))return;}
                else if(!retry)return;
                cleanupRunning=true;
            }
            int outcome=Pending;IntPtr window=IntPtr.Zero;
            try{outcome=RunCleanup(id,out window);}
            catch{}
            finally{CompleteCleanup(id,controller,destination,outcome,window);}
        }
        static int RunCleanup(Guid id,out IntPtr window)
        {
            window=IntPtr.Zero;
            lock(HelperIpc)
            {
                bool upObserved;Guid previous;
                lock(Gate)
                {
                    if(!IsSource(id,NativeDragSession.Phase.Ending))return Pending;
                    upObserved=source.CleanupUpObserved;previous=helperLeg;
                }
                window=Platform.LocateHelper();
                if(upObserved)return ConfirmRelease(id,strictUpLeg==id,window);
                CaptureState first=Platform.ProbeCapture();
                // Nothing holds the button any more (complete picture): finish without input.
                if(first.Complete&&!first.OleDrag&&!first.LeftDown)return ExternallyReleased;
                if(window==IntPtr.Zero)return Pending;
                if(previous!=Guid.Empty&&previous!=id)
                {
                    if(!HelperCall(window,previous,HelperNativeDrag.Invalidate))return Pending;
                    lock(Gate)if(helperLeg==previous)helperLeg=Guid.Empty;
                }
                IntPtr moveToken;
                lock(Gate){if(!IsSource(id,NativeDragSession.Phase.Ending))return Pending;helper=window;helperLeg=id;moveToken=Tags.RegisterMove(id);}
                try
                {
                if(!HelperCall(window,id,HelperNativeDrag.ArmReject))return Pending;
                for(int i=0;i<40;i++)
                {
                    lock(Gate)
                    {
                        if(!IsSource(id,NativeDragSession.Phase.Ending))return Pending;
                        Point p;if(!Platform.CursorPos(out p))return Pending;
                        Platform.PlaceHelper(window,new Rectangle(p.X-16,p.Y-16,32,32));
                    }
                    // A zero-distance move can leave OLE's cached target unchanged. These
                    // moves carry this attempt's own token: the hook drops them at delivery
                    // once the attempt ended or the leg was invalidated, and a later attempt
                    // can never re-authorise them (fresh token each attempt).
                    Platform.InjectMove((i&1)==0?1:-1,moveToken);
                    Platform.Sleep(20);
                    // Strict: OLE itself visited our armed sink and was told None. This
                    // does not depend on the capture probe at all.
                    bool viaOle=HelperCall(window,id,HelperNativeDrag.QueryReject),viaProof=false;
                    if(!viaOle)
                    {
                        CaptureState state=Platform.ProbeCapture();
                        if(state.Complete&&!state.OleDrag)
                        {
                            if(!state.LeftDown)return ExternallyReleased;
                            // Absence path, three independent facts: a complete probe shows
                            // no OLE loop on the input desktop; this leg never received a
                            // capture reply (no FileDrop drag known); and the armed, visible
                            // sink never saw any OLE callback. A leg with OLE evidence is
                            // released only through the strict path above.
                            bool oleKnown;lock(Gate)oleKnown=source!=null&&source.Leg==id&&source.OleEvidence;
                            // Supported-platform gate (v9.0.0.10): the CLIPBRDWNDCLASS evidence
                            // is only used where it was verified; elsewhere this path is off.
                            if(!oleKnown&&Platform.AbsencePathSupported)viaProof=HelperCall(window,id,HelperNativeDrag.QueryNoOleSeen);
                        }
                    }
                    lock(Gate)
                    {
                        if(!IsSource(id,NativeDragSession.Phase.Ending))return Pending;
                        Point p;if(!Platform.CursorPos(out p))return Pending;
                        safeTargetObserved=(viaOle||viaProof)&&Platform.WindowAt(p)==window;
                    }
                    IntPtr token=IntPtr.Zero;
                    // Re-check the absence evidence immediately before injecting.
                    if(safeTargetObserved&&viaProof){CaptureState again=Platform.ProbeCapture();if(!again.Complete||again.OleDrag){lock(Gate)safeTargetObserved=false;Platform.Sleep(20);continue;}}
                    lock(Gate)
                    {
                        if(!IsSource(id,NativeDragSession.Phase.Ending)){safeTargetObserved=false;return Pending;}
                        if(safeTargetObserved){token=Tags.Register(id);Tags.Arm(token);if(viaProof)proofAttempts++;}
                    }
                    if(token!=IntPtr.Zero)
                    {
                        int error;uint sent=Platform.InjectLeftUp(token,out error);
                        releaseAttempts++;releaseInserted=(int)sent;releaseError=error;sendTokenLow=unchecked((int)token.ToInt64());sendTokenHigh=unchecked((int)(token.ToInt64()>>32));
                        // SendInput acknowledges queue insertion, not hook completion. Keep
                        // the token live briefly without holding Gate so the hook can accept it.
                        for(int wait=0;sent==1&&wait<25&&!Tags.WasObserved(token);wait++)Platform.Sleep(10);
                        bool observed;
                        lock(Gate)
                        {
                            observed=Tags.RetireAndWasObserved(token);safeTargetObserved=false;
                            if(observed&&IsSource(id,NativeDragSession.Phase.Ending)){source.ObserveCleanupUp(id);if(viaOle)strictUpLeg=id;}
                            else observed=false;
                        }
                        if(observed)return ConfirmRelease(id,viaOle,window);
                    }
                    Platform.Sleep(20);
                }
                return Pending;
                }
                finally{Tags.Retire(moveToken);}
            }
        }
        // Released means, for a strict release: the sink saw OLE's Drop/Leave and the
        // button is up (helper), OR a complete probe shows no OLE loop and the button up
        // (works after a helper restart). For an absence release only the latter.
        static int ConfirmRelease(Guid id,bool strict,IntPtr window)
        {
            for(int wait=0;wait<25;wait++)
            {
                Platform.Sleep(20);
                lock(Gate)if(!IsSource(id,NativeDragSession.Phase.Ending))return Pending;
                bool done=strict&&window!=IntPtr.Zero&&HelperCall(window,id,HelperNativeDrag.QueryReleased);
                if(!done){CaptureState state=Platform.ProbeCapture();done=state.Complete&&!state.OleDrag&&!state.LeftDown;}
                if(done){if(strict)strictReleases++;else proofReleases++;return strict?ReleasedByOle:ReleasedByProof;}
            }
            return Pending;
        }
        static void CompleteCleanup(Guid id,uint controller,uint destination,int outcome,IntPtr window)
        {
            string path=null;bool finished=false,delayed=false;
            lock(Gate)
            {
                safeTargetObserved=false;
                if(IsSource(id,NativeDragSession.Phase.Ending))
                {
                    // Only a release that OLE itself dropped on None may carry the file.
                    if(outcome==ReleasedByOle){path=source.PendingTransferPath;finished=source.CleanupCompleted(id,true);}
                    else if(outcome==ReleasedByProof)finished=source.CleanupCompleted(id,true);
                    else if(outcome==ExternallyReleased)finished=source.CleanupExternallyCompleted(id,true);
                    if(!finished){path=null;delayed=delayNotified!=id;if(delayed)delayNotified=id;}
                }
            }
            bool offered=false;
            if(finished&&path!=null){try{offered=Platform.Offer(id,Platform.Name(destination),path);}catch{}}
            path=null;
            lock(Gate)
            {
                if(finished)
                {
                    Dispatch(offered?Commit:Abort,destination,id,controller);
                    Dispatch(Ended,controller,id,EndedReleased);
                }
                // Not released: keep the sink, keep retrying, never time out. The
                // controller stays blocked until Ended, a down link, or the user's Esc.
                else if(delayed)Dispatch(CleanupDelayed,controller,id,0);
                cleanupRunning=false;
                // The finished leg can never be armed again on this source.
                if(finished)RetireLeg(id);
            }
            if(!finished||window==IntPtr.Zero)return;
            lock(HelperIpc)
            {
                bool done=HelperCall(window,id,HelperNativeDrag.Invalidate);
                if(!done)Platform.HideHelper(window);
                lock(Gate)if(done&&helperLeg==id)helperLeg=Guid.Empty;
            }
        }

        internal static void Tick()
        {
            // Overlapping timer callbacks must not interleave probe results.
            if(Interlocked.Exchange(ref tickRunning,1)==1)return;
            try{TickCore();}
            finally{Interlocked.Exchange(ref tickRunning,0);}
        }
        static void TickCore()
        {
            Platform.TickRequests();
            // Connectivity is probed OUTSIDE Gate (the host's IsConnectedTo writes to
            // the socket); everything else reads the cached result.
            List<uint> watch;Dictionary<uint,long> started=new Dictionary<uint,long>();
            lock(Gate){watch=WatchedPeers();foreach(uint peer in watch)started[peer]=EpochOf(peer);}
            Dictionary<uint,bool> links=new Dictionary<uint,bool>();
            foreach(uint peer in watch){bool up=false;try{up=Platform.Connected(peer);}catch{}links[peer]=!up;}
            lock(Gate)
            {
                // Peers no longer watched lose any verdict; a verdict for a peer whose
                // epoch changed while the probe was in flight is stale and discarded.
                List<uint> unwatched=null;
                foreach(uint peer in LinkDown.Keys)if(!links.ContainsKey(peer)){if(unwatched==null)unwatched=new List<uint>();unwatched.Add(peer);}
                if(unwatched!=null)foreach(uint peer in unwatched)LinkDown.Remove(peer);
                foreach(KeyValuePair<uint,bool> pair in links)
                {
                    if(EpochOf(pair.Key)!=started[pair.Key]){staleLinkVerdicts++;continue;}
                    LinkDown[pair.Key]=pair.Value;
                }
                DateTime now=Platform.Now;
                if(helperLeg!=Guid.Empty&&(source==null||source.Leg!=helperLeg||source.State==NativeDragSession.Phase.Ended))
                    Platform.Queue(InvalidateHelper);
                if(source!=null&&source.State==NativeDragSession.Phase.Ending&&!cleanupRunning)
                {
                    Guid retryId=source.Leg;uint retryController=source.Controller,retryTarget=source.Target;
                    Platform.Queue(delegate{FinishSourceAttempt(retryId,retryController,retryTarget,true);});
                }
                if(source!=null&&(source.State==NativeDragSession.Phase.Capturing||source.State==NativeDragSession.Phase.Remote)&&IsLinkDown(source.Controller))
                {
                    Guid id=source.Leg;uint c=source.Controller,p=source.Target;string ignored;
                    if(source.EndRemote(id,c,false,out ignored))Platform.Queue(delegate{FinishSourceAttempt(id,c,p,false);});
                }
                if(pendingEnd!=Guid.Empty&&IsLinkDown(pendingSource))ReleaseController("来源电脑已断开连接，已允许鼠标切回，本次文件未传输。\n重新连接后若那台电脑上文件图标仍跟随光标，请在那里按 Esc 取消，不要直接单击。");
                ExpireTargets(now);
                RefreshCapabilities(now);
            }
        }
        static List<uint> WatchedPeers()
        {
            uint local=Platform.Local;HashSet<uint> set=new HashSet<uint>();
            if(pendingEnd!=Guid.Empty)set.Add(pendingSource);
            if(source!=null&&source.State!=NativeDragSession.Phase.Ended&&source.State!=NativeDragSession.Phase.Native)set.Add(source.Controller);
            if(target!=null){set.Add(target.Controller);set.Add(target.Source);}
            foreach(Target t in ReleasedTargets.Values)set.Add(t.Source);
            set.Remove(local);set.Remove(0);
            return new List<uint>(set);
        }

        // ---- test/diagnostic accessors (process-local, numeric only) --------------------
        internal static bool ControllerWaiting{get{lock(Gate)return pendingEnd!=Guid.Empty;}}
        internal static string SourceState{get{lock(Gate)return source==null?"none":source.State.ToString();}}
        internal static bool HasActiveTarget{get{lock(Gate)return target!=null;}}
        internal static int ReleasedTargetCount{get{lock(Gate)return ReleasedTargets.Count;}}
        internal static int IpcUnderGate{get{return ipcUnderGate;}}
        internal static int StrictReleases{get{return strictReleases;}}
        internal static int ProofReleases{get{return proofReleases;}}
        internal static int ProofAttempts{get{return proofAttempts;}}
        internal static int StaleLinkVerdicts{get{return staleLinkVerdicts;}}
        internal static int DeferredCrossings{get{return deferredCrossings;}}
        internal static int StaleArms{get{return staleArms;}}
        internal static bool CrossPending{get{lock(Gate)return crossPending;}}
        internal static int Invalidations{get{return invalidations;}}
        internal static Guid HelperLeg{get{lock(Gate)return helperLeg;}}
        internal static void ResetForTests(NativePlatform platform)
        {
            lock(Gate)
            {
                Platform=platform;Peers.Clear();ReleasedTargets.Clear();EarlyNotices.Clear();LinkDown.Clear();LinkEpoch.Clear();RetiredLegs.Clear();RetiredOrder.Clear();tickRunning=0;crossPending=false;crossLeg=Guid.Empty;crossSeq=0;crossNext=0;proofAttempts=staleLinkVerdicts=deferredCrossings=staleArms=0;source=null;target=null;down=false;armPending=false;
                origin=pointer=pendingSource=0;leg=pendingEnd=notifiedFailure=delayNotified=strictUpLeg=helperLeg=Guid.Empty;helper=IntPtr.Zero;
                safeTargetObserved=cleanupRunning=false;started=true;ipcUnderGate=proofReleases=strictReleases=invalidations=recoveries=targetsExpired=0;
            }
        }
    }
}
