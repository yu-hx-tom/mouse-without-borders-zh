using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace MwbTransfer
{
    // v9.0.0.7: every OS, host, UI and network effect used by NativeDragRuntime
    // goes through this class. Production uses these implementations unchanged;
    // the coordinator replay test overrides them so no real input is injected.
    internal class NativePlatform
    {
        // ---- time and scheduling -------------------------------------------------
        internal virtual DateTime Now { get { return DateTime.UtcNow; } }
        internal virtual void Sleep(int milliseconds) { Thread.Sleep(milliseconds); }
        internal virtual void Queue(Action work) { ThreadPool.QueueUserWorkItem(delegate { try { work(); } catch { } }); }
        // File transfers can run for minutes; keep them off the shared thread pool
        // so timer retries and cleanup are not starved.
        internal virtual void RunLong(Action work)
        {
            Thread thread=new Thread(delegate() { try { work(); } catch { } });
            thread.IsBackground=true; thread.Name="MWB native file connection"; thread.Start();
        }
        Timer timer;
        internal virtual void StartTimer(Action tick)
        { if(timer==null) timer=new Timer(delegate { try { tick(); } catch { } },null,500,500); }

        // v9.0.0.8: ordered outbox. Every native-drag packet is written by this one
        // background thread, so no caller (input hook, packet thread, timer) ever
        // waits on the host's socket lock or a slow connection. FIFO keeps order.
        // v9.0.0.10: every post gets a sequence number; Written(seq) reports that this
        // and every earlier post has been handed to the host's socket writer. The
        // coordinator uses it as a NON-BLOCKING ordering barrier for held crossings.
        readonly object outboxGate=new object();
        readonly System.Collections.Generic.Queue<Action> outbox=new System.Collections.Generic.Queue<Action>();
        Thread outboxThread;
        long posted,written;
        internal virtual long Post(Action send)
        {
            lock(outboxGate)
            {
                outbox.Enqueue(send);long seq=++posted;
                if(outboxThread==null)
                {
                    outboxThread=new Thread(OutboxLoop);
                    outboxThread.IsBackground=true;outboxThread.Name="MWB native drag outbox";outboxThread.Start();
                }
                // PulseAll (defensive): the writer and WaitWritten callers share this monitor.
                Monitor.PulseAll(outboxGate);
                return seq;
            }
        }
        internal virtual long LastPosted { get { lock(outboxGate) return posted; } }
        internal virtual bool Written(long seq) { lock(outboxGate) return written>=seq; }
        // Bounded wait for Written(seq). Waits only on the outbox's own monitor (never on
        // the host's socket lock, which only the outbox thread touches), at most
        // `milliseconds` in total.
        internal virtual bool WaitWritten(long seq,int milliseconds)
        {
            long deadline=DateTime.UtcNow.Ticks+milliseconds*TimeSpan.TicksPerMillisecond;
            lock(outboxGate)
            {
                while(written<seq)
                {
                    long left=(deadline-DateTime.UtcNow.Ticks)/TimeSpan.TicksPerMillisecond;
                    if(left<=0)return false;
                    Monitor.Wait(outboxGate,(int)left);
                }
                return true;
            }
        }
        void OutboxLoop()
        {
            while(true)
            {
                Action next;
                lock(outboxGate){while(outbox.Count==0)Monitor.Wait(outboxGate);next=outbox.Dequeue();}
                try{next();}catch{}
                // Counted even on failure: a failed write must not freeze crossings.
                lock(outboxGate){written++;Monitor.PulseAll(outboxGate);}
            }
        }

        // v9.0.0.8: evidence of OLE/ordinary capture (sends no messages). v9.0.0.9: it
        // reports Complete=false on any failure; callers treat that as Unknown.
        // (v9.0.0.9 removed CancelMode: a cross-process cancel could not be bound to the
        // old gesture and might reach a NEW capture after a recovery.)
        internal virtual CaptureState ProbeCapture() { return NativeCaptureProbe.Probe(); }

        // v9.0.0.10 explicit support boundary for the ABSENCE path (Codex 9.0.0.9 Q3).
        // It relies on an ole32 implementation detail (CLIPBRDWNDCLASS capture) verified
        // only on Windows 10 build 19044 on the project machine; it is enabled for the
        // 19041-19045 servicing family of that same Windows 10 release line and nowhere
        // else. On any other build the absence path is OFF (a non-file hold then waits
        // for the user's Esc); the strict OLE/None path is unaffected. Add a build here
        // only after CaptureProbeExperiment passes on it.
        static readonly int[] VerifiedBuilds={19041,19042,19043,19044,19045};
        bool? absenceSupported;
        internal virtual bool AbsencePathSupported
        {
            get
            {
                if(absenceSupported==null)
                {
                    bool ok=false;
                    try
                    {
                        OSVERSIONINFOEX v=new OSVERSIONINFOEX();v.Size=Marshal.SizeOf(typeof(OSVERSIONINFOEX));
                        ok=RtlGetVersion(ref v)==0&&VerifiedAbsenceBuild(v.Major,v.Minor,v.Build);
                    }
                    catch{ok=false;}
                    absenceSupported=ok;
                }
                return absenceSupported.Value;
            }
        }
        // Windows 11 also reports 10.0 (builds 22000+); only the listed builds qualify.
        internal static bool VerifiedAbsenceBuild(int major,int minor,int build)
        { return major==10&&minor==0&&Array.IndexOf(VerifiedBuilds,build)>=0; }
        [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]
        struct OSVERSIONINFOEX
        {
            public int Size,Major,Minor,Build,Platform;
            [MarshalAs(UnmanagedType.ByValTStr,SizeConst=128)] public string ServicePack;
            public ushort ServicePackMajor,ServicePackMinor,SuiteMask;public byte ProductType,Reserved;
        }
        [DllImport("ntdll.dll")] static extern int RtlGetVersion(ref OSVERSIONINFOEX info);

        // ---- host (original Mouse without Borders) --------------------------------
        PropertyInfo logon,saver;
        internal virtual bool InDefaultDesktop()
        {
            if(logon==null)
            {
                Type common=Assembly.GetEntryAssembly().GetType("MouseWithoutBorders.Common");
                const BindingFlags flags=BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
                saver=common.GetProperty("RunOnScrSaverDesktop",flags);
                logon=common.GetProperty("RunOnLogonDesktop",flags);
            }
            return !(bool)logon.GetValue(null,null)&&!(bool)saver.GetValue(null,null);
        }
        internal virtual uint Local { get { return NativeHost.Local; } }
        internal virtual uint Pointer { get { return NativeHost.Pointer; } }
        internal virtual void SetDown(bool down) { NativeHost.SetDown(down); }
        internal virtual string Name(uint peer) { return NativeHost.Name(peer); }
        internal virtual bool Connected(uint peer) { return NativeHost.Connected(peer); }
        internal virtual void Send(int type,uint peer,Guid id,uint argument) { NativeHost.Send(type,peer,id,argument); }
        internal virtual bool ConnectFile(string machine,Guid id,bool sending) { return NativeHost.ConnectFile(machine,id,sending); }
        internal virtual uint PeerOf(object connection)
        { return Convert.ToUInt32(connection.GetType().GetProperty("MachineId").GetValue(connection,null)); }
        internal virtual bool ConnectionAlive(object connection)
        {
            if(connection==null)return false;
            try{object socket=connection.GetType().GetProperty("BackingSocket").GetValue(connection,null);return socket!=null&&((System.Net.Sockets.Socket)socket).Connected;}catch{return false;}
        }

        // ---- transfer UI (MwbTransfer.dll) ---------------------------------------
        internal virtual object CaptureDrop() { return Transfer.CaptureNativeDrop(NativeHost.Form.Handle); }
        internal virtual void PreviewBegin() { Transfer.BeginDragPreview(NativeHost.Form.Handle); }
        internal virtual void PreviewEnd() { Transfer.EndDragPreview(); }
        internal virtual void BeginDrop(Guid id,string peer,object snapshot,Action<Guid,string> request) { Transfer.BeginNativeDrop(id,peer,snapshot,request); }
        internal virtual bool Offer(Guid id,string peer,string path) { return Transfer.OfferNativeFile(id,peer,path); }
        internal virtual void FailRequest(Guid id,string reason) { Transfer.FailNativeRequest(id,reason); }
        internal virtual void TickRequests() { Transfer.TickNativeRequests(); }
        internal virtual void Message(string title,string text) { Transfer.ShowNativeMessage(title,text); }
        internal virtual bool IsDirectory(string path) { return Transfer.IsNativeDirectory(path); }

        // ---- helper window ---------------------------------------------------------
        const string HelperTitle="Mouse without Borders Helper";
        // Only a window owned by this installation's helper, in this session, is
        // trusted as the reject sink (v9.0.0.7; earlier builds trusted any title match).
        internal virtual IntPtr LocateHelper()
        {
            string expected=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"MouseWithoutBordersHelper.exe");
            uint session;
            if(!ProcessIdToSessionId((uint)System.Diagnostics.Process.GetCurrentProcess().Id,out session))return IntPtr.Zero;
            IntPtr after=IntPtr.Zero;
            for(int i=0;i<32;i++)
            {
                after=FindWindowEx(IntPtr.Zero,after,null,HelperTitle);
                if(after==IntPtr.Zero)return IntPtr.Zero;
                if(OwnedBy(after,expected,session))return after;
            }
            return IntPtr.Zero;
        }
        static bool OwnedBy(IntPtr window,string expected,uint session)
        {
            uint pid,windowSession;GetWindowThreadProcessId(window,out pid);
            if(pid==0||!ProcessIdToSessionId(pid,out windowSession)||windowSession!=session)return false;
            IntPtr process=OpenProcess(0x1000,false,pid);
            if(process==IntPtr.Zero)return false;
            try
            {
                StringBuilder path=new StringBuilder(32768);int length=path.Capacity;
                if(!QueryFullProcessImageName(process,0,path,ref length))return false;
                return String.Equals(Path.GetFullPath(path.ToString()),Path.GetFullPath(expected),StringComparison.OrdinalIgnoreCase);
            }
            catch(ArgumentException){return false;}
            finally{CloseHandle(process);}
        }
        internal virtual bool HelperCommand(IntPtr window,Guid id,int command)
        {
            if(window==IntPtr.Zero)return false;
            byte[] data=new byte[24];Array.Copy(BitConverter.GetBytes(1),data,4);Array.Copy(BitConverter.GetBytes(command),0,data,4,4);Array.Copy(id.ToByteArray(),0,data,8,16);
            IntPtr memory=Marshal.AllocHGlobal(24);
            try
            {
                Marshal.Copy(data,0,memory,24);COPYDATA packet=new COPYDATA{Magic=new IntPtr(0x3944574d),Size=24,Data=memory};IntPtr reply;
                return SendMessageTimeout(window,0x4a,NativeHost.Form.Handle,ref packet,2,100,out reply)!=IntPtr.Zero&&reply==new IntPtr(1);
            }
            finally{Marshal.FreeHGlobal(memory);}
        }
        // SWP_ASYNCWINDOWPOS: never block the caller on the helper's UI thread.
        internal virtual void PlaceHelper(IntPtr window,Rectangle bounds)
        { SetWindowPos(window,new IntPtr(-1),bounds.X,bounds.Y,bounds.Width,bounds.Height,0x4000|0x40|0x10); }
        internal virtual void HideHelper(IntPtr window) { if(window!=IntPtr.Zero)ShowWindowAsync(window,0); }

        // ---- pointer and input -----------------------------------------------------
        internal virtual bool CursorPos(out Point point) { return GetCursorPos(out point); }
        internal virtual IntPtr WindowAt(Point point) { return WindowFromPoint(point); }
        internal virtual uint InjectMove(int dx,IntPtr tag)
        {
            INPUT move=new INPUT{Type=0,Mouse=new MOUSEINPUT{X=dx,Flags=0x2001,Extra=tag}};
            return SendInput(1,new[]{move},Marshal.SizeOf(typeof(INPUT)));
        }
        internal virtual uint InjectLeftUp(IntPtr token,out int error)
        {
            INPUT input=new INPUT{Type=0,Mouse=new MOUSEINPUT{Flags=4,Extra=token}};
            uint sent=SendInput(1,new[]{input},Marshal.SizeOf(typeof(INPUT)));
            error=Marshal.GetLastWin32Error();return sent;
        }

        [StructLayout(LayoutKind.Sequential)] struct COPYDATA {internal IntPtr Magic;internal int Size;internal IntPtr Data;}
        [StructLayout(LayoutKind.Sequential)] struct MOUSEINPUT {internal int X,Y,Data,Flags,Time;internal IntPtr Extra;}
        [StructLayout(LayoutKind.Sequential)] struct INPUT {internal int Type;internal MOUSEINPUT Mouse;}
        [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr parent,IntPtr after,string className,string title);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window,out uint pid);
        [DllImport("kernel32.dll")] static extern bool ProcessIdToSessionId(uint pid,out uint session);
        [DllImport("kernel32.dll",SetLastError=true)] static extern IntPtr OpenProcess(uint access,bool inherit,uint pid);
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool QueryFullProcessImageName(IntPtr process,uint flags,StringBuilder text,ref int length);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
        [DllImport("user32.dll")] static extern bool GetCursorPos(out Point point);
        [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(Point point);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr window,IntPtr after,int x,int y,int width,int height,uint flags);
        [DllImport("user32.dll")] static extern bool ShowWindowAsync(IntPtr window,int command);
        [DllImport("user32.dll")] static extern IntPtr SendMessageTimeout(IntPtr window,int message,IntPtr from,ref COPYDATA data,uint flags,uint timeout,out IntPtr result);
        [DllImport("user32.dll",SetLastError=true)] static extern uint SendInput(uint count,INPUT[] inputs,int size);
    }
}
