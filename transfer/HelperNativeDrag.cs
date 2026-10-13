using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace MwbTransfer
{
    // Runs inside the interactive user's existing helper, not the SYSTEM host.
    // WM_COPYDATA only sets/queries context. Never call IPC from its WndProc.
    public static class HelperNativeDrag
    {
        const int CopyData=0x4a, Magic=0x3944574d, Version=1;
        internal const int ArmCapture=1, Invalidate=2, ArmReject=3, QueryReject=4, FinishReject=5, QueryReleased=6, QueryExternalRelease=7, QueryNoOleSeen=9;
        // v9.0.0.8: the v9.0.0.7 "quiet period" command (8) was removed and is never reused.
        // v9.0.0.9: QueryReject (4) is STRICT: only a fresh OLE DragEnter/DragOver on this
        // armed sink (answered None) opens it; plain WM_MOUSEMOVE never does.
        // QueryNoOleSeen (9) is a separate, read-only fact for the absence path: since
        // this leg was armed, no OLE callback of any kind reached this window.
        static Form owner;
        static Guid active;
        static int mode;
        static bool consumed,rejectObserved,anyOleObserved,normalMoveObserved;
        static long armedAt;
        static uint ownerProcess;
        public static bool OwnsWindow(Form form) { return Object.ReferenceEquals(owner,form)&&active!=Guid.Empty; }
        [StructLayout(LayoutKind.Sequential)] struct CopyDataStruct { internal IntPtr Kind; internal int Length; internal IntPtr Bytes; }
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window,out uint pid);
        [DllImport("kernel32.dll",SetLastError=true)] static extern IntPtr OpenProcess(uint access,bool inherit,uint pid);
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool QueryFullProcessImageName(IntPtr process,uint flags,StringBuilder text,ref int length);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr window);

        public static bool HandleMessage(Form form, ref Message message)
        {
            if(OwnsWindow(form)&&mode==ArmReject)
            {
                if(message.Msg==0x200)normalMoveObserved=true;
                // A release injected while no OLE drag exists lands on this sink. The
                // form itself has no mouse handlers, but never let a click through.
                if(message.Msg==0x201||message.Msg==0x202){message.Result=IntPtr.Zero;return true;}
            }
            if(message.Msg!=CopyData || message.LParam==IntPtr.Zero) return false;
            CopyDataStruct data=(CopyDataStruct)Marshal.PtrToStructure(message.LParam,typeof(CopyDataStruct));
            if(data.Kind.ToInt64()!=Magic) return false;
            message.Result=IntPtr.Zero;
            uint sender;
            if(data.Length!=24 || data.Bytes==IntPtr.Zero || !TrustedMain(message.WParam,out sender)) return true;
            byte[] bytes=new byte[24];Marshal.Copy(data.Bytes,bytes,0,bytes.Length);
            if(BitConverter.ToInt32(bytes,0)!=Version) return true;
            byte[] id=new byte[16];Array.Copy(bytes,8,id,0,16);Guid leg=new Guid(id);
            if(leg==Guid.Empty) return true;
            EnsureOwner(form);
            AdoptOwner(form,sender);
            int command=BitConverter.ToInt32(bytes,4);
            if(command==ArmCapture)
            {
                // Main must invalidate an earlier leg before installing another.
                if(active!=Guid.Empty) return true;
                active=leg;mode=ArmCapture;consumed=rejectObserved=anyOleObserved=normalMoveObserved=false;armedAt=Stopwatch.GetTimestamp();
                message.Result=new IntPtr(Version);return true;
            }
            // Release may beat the queued capture worker.
            if(command==ArmReject&&active==Guid.Empty){Clear();active=leg;}
            if(command==Invalidate&&active==Guid.Empty){form.Hide();message.Result=new IntPtr(Version);return true;}
            if(active!=leg) return true;
            switch(command)
            {
                case Invalidate:
                    form.Hide();Clear();message.Result=new IntPtr(Version);break;
                case ArmReject:
                    // Retrying the same leg must not erase evidence from its OLE callback.
                    if(mode!=ArmReject){rejectObserved=normalMoveObserved=false;}
                    mode=ArmReject;consumed=true;form.Opacity=0.11;
                    message.Result=new IntPtr(Version);break;
                case QueryReject:
                    // OLE must actually visit this reject target and be told None.
                    // Main shows this HWND via SetWindowPos(SWP_SHOWWINDOW).
                    // That does not set WinForms' cached Control.Visible flag after Hide().
                    if(mode==ArmReject&&IsWindowVisible(form.Handle)&&form.AllowDrop&&rejectObserved)message.Result=new IntPtr(Version);
                    break;
                case QueryNoOleSeen:
                    if(mode==ArmReject&&IsWindowVisible(form.Handle)&&!anyOleObserved&&!rejectObserved)message.Result=new IntPtr(Version);
                    break;
                case FinishReject:
                    if(mode!=ArmReject)break;
                    form.Hide();Clear();message.Result=new IntPtr(Version);break;
                case QueryReleased:
                    if(mode==ArmReject&&!rejectObserved&&(GetAsyncKeyState(1)&0x8000)==0)message.Result=new IntPtr(Version);
                    break;
                case QueryExternalRelease: // Ordinary input reached this sink and left is up.
                    if(mode==ArmReject&&normalMoveObserved&&!rejectObserved&&(GetAsyncKeyState(1)&0x8000)==0)message.Result=new IntPtr(Version);
                    break;
            }
            return true;
        }

        // v9.0.0.7: a restarted main process cannot know the previous process's
        // leg, so commands from a different trusted main PID reset this context.
        internal static void AdoptOwner(Form form,uint sender)
        {
            if(sender==0||sender==ownerProcess)return;
            if(ownerProcess!=0&&active!=Guid.Empty){form.Hide();Clear();}
            ownerProcess=sender;
        }
        internal static Guid ActiveLeg { get { return active; } }

        public static void OnDragEnter(Form form, DragEventArgs e, Action<string> sendReply)
        {
            e.Effect=DragDropEffects.None;
            if(!Object.ReferenceEquals(owner,form) || active==Guid.Empty) return;
            anyOleObserved=true;
            if(mode==ArmReject) { rejectObserved=true;return; }
            if(mode!=ArmCapture || consumed || ElapsedMilliseconds(armedAt)>750) return;
            Guid capturedLeg=active;consumed=true;
            string envelope=null;
            try
            {
                if(e.Data!=null && e.Data.GetDataPresent(DataFormats.FileDrop))
                {
                    string[] paths=e.Data.GetData(DataFormats.FileDrop) as string[];
                    if(paths!=null && paths.Length>0)envelope=NativeDragCapture.Encode(capturedLeg,paths.Length,paths[0]);
                }
            }
            catch(ArgumentException) { }
            catch(System.Runtime.InteropServices.COMException) { }
            // Hiding preserves the existing OLE object. There is deliberately no
            // MouseUp, local copy, clipboard mutation or fabricated IDataObject.
            form.Hide();
            if(envelope!=null)
            {
                string reply=envelope;
                ThreadPool.QueueUserWorkItem(delegate { try { sendReply(reply); } catch(Exception) { /* Main times out this leg; never crash the helper's input thread. */ } });
            }
        }

        static void EnsureOwner(Form form)
        {
            if(Object.ReferenceEquals(owner,form))return;
            if(owner!=null)throw new InvalidOperationException("Multiple native drag helper windows");
            owner=form;form.AllowDrop=true;
            form.DragOver+=delegate(object sender,DragEventArgs e) { e.Effect=DragDropEffects.None;anyOleObserved=true;if(mode==ArmReject&&active!=Guid.Empty)rejectObserved=true; };
            form.DragLeave+=delegate { rejectObserved=false; };
            form.DragDrop+=delegate(object sender,DragEventArgs e) { e.Effect=DragDropEffects.None;rejectObserved=false; };
        }
        static void Clear() { active=Guid.Empty;mode=0;consumed=true;rejectObserved=anyOleObserved=normalMoveObserved=false; }
        static double ElapsedMilliseconds(long start) { return (Stopwatch.GetTimestamp()-start)*1000.0/Stopwatch.Frequency; }

        static bool TrustedMain(IntPtr window,out uint pid)
        {
            pid=0;
            if(window==IntPtr.Zero)return false;
            GetWindowThreadProcessId(window,out pid);
            if(pid==0)return false;
            IntPtr process=OpenProcess(0x1000,false,pid);
            if(process==IntPtr.Zero)return false;
            try
            {
                StringBuilder path=new StringBuilder(32768);int length=path.Capacity;
                if(!QueryFullProcessImageName(process,0,path,ref length))return false;
                string expected=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"MouseWithoutBorders.exe");
                if(!String.Equals(Path.GetFullPath(path.ToString()),Path.GetFullPath(expected),StringComparison.OrdinalIgnoreCase))return false;
                using(Process sender=Process.GetProcessById((int)pid))
                using(Process self=Process.GetCurrentProcess())return sender.SessionId==self.SessionId;
            }
            catch(ArgumentException) { return false; }
            catch(InvalidOperationException) { return false; }
            catch(System.ComponentModel.Win32Exception) { return false; }
            finally { CloseHandle(process); }
        }
    }
}
