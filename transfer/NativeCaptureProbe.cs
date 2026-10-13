using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace MwbTransfer
{
    // Evidence about who holds the mouse on this desktop (v9.0.0.8, tightened v9.0.0.9).
    //
    // What it is: ole32's DoDragDrop keeps mouse capture on its private window of
    // class CLIPBRDWNDCLASS for the whole drag loop. That was verified on this
    // project's Windows 10 19044 machine; it is an implementation detail of ole32,
    // not a documented contract, and Windows 11 is not yet verified.
    //
    // How it is used: OleDrag==true is positive evidence that an OLE loop exists.
    // OleDrag==false is only accepted as "no OLE loop" when Complete is true, AND the
    // caller has independent evidence too (see NativeDragRuntime: no capture reply on
    // this leg and the helper sink saw no OLE callback). Any failure => Complete=false,
    // and callers must then treat the state as Unknown (fail closed).
    // Reading capture state sends no messages to any application and cannot block.
    internal sealed class CaptureState
    {
        internal bool Complete;         // every query succeeded on the input desktop
        internal bool OleDrag;          // some thread runs an OLE DoDragDrop loop
        internal IntPtr OleWindow;
        internal IntPtr Ordinary;       // a non-OLE capture (text selection, rubber band, move/size loop...)
        internal bool LeftDown;         // system-wide left button state (only meaningful if Complete)
        internal string Incomplete;     // numeric/diagnostic reason when Complete is false (no names)
        public override string ToString() { return "complete="+Complete+(Incomplete!=null?"("+Incomplete+")":"")+" ole="+OleDrag+" ordinary="+(Ordinary!=IntPtr.Zero)+" left="+LeftDown; }
    }

    internal static class NativeCaptureProbe
    {
        internal const string OleDragClass="CLIPBRDWNDCLASS";
        const int MaxOleWindows=256;

        internal static CaptureState Probe()
        {
            CaptureState state=new CaptureState();
            bool complete=OnInputDesktop();if(!complete)state.Incomplete="desktop";
            // 1) OLE drag windows may be top-level or message-only: check both.
            foreach(IntPtr parent in new[]{IntPtr.Zero,new IntPtr(-3)})
            {
                IntPtr after=IntPtr.Zero;int seen=0;
                while(true)
                {
                    after=FindWindowEx(parent,after,OleDragClass,null);
                    if(after==IntPtr.Zero)break;
                    if(++seen>MaxOleWindows){complete=false;state.Incomplete="ole-window-limit";break;}   // never silently truncate
                    uint pid;uint thread=GetWindowThreadProcessId(after,out pid);
                    if(thread==0)continue;                            // window vanished meanwhile
                    IntPtr capture;
                    if(!CaptureOf(thread,out capture)){int oleError=Marshal.GetLastWin32Error();if(ThreadAlive(thread)){complete=false;state.Incomplete="ole-thread-query:"+oleError;}continue;}
                    if(capture==after){state.OleDrag=true;state.OleWindow=after;}
                }
            }
            // 2) Any other capture held by a GUI thread on this desktop.
            HashSet<uint> threads=new HashSet<uint>();
            bool enumerated=EnumWindows(delegate(IntPtr window,IntPtr unused)
            {
                // A console window reports its attached client's (non-GUI) thread as owner;
                // GetGUIThreadInfo then fails with 87 (observed on the project machine).
                // That pseudo-owner cannot run a drag loop on this window, so it is skipped;
                // the same thread is still queried through any real window it owns.
                if(ClassOf(window)=="ConsoleWindowClass")return true;
                uint pid;uint thread=GetWindowThreadProcessId(window,out pid);
                if(thread!=0)threads.Add(thread);
                return true;
            },IntPtr.Zero);
            if(!enumerated){complete=false;state.Incomplete="enum-windows";}
            foreach(uint thread in threads)
            {
                IntPtr capture;
                if(!CaptureOf(thread,out capture))
                {
                    // A thread that exited between enumeration and query is harmless;
                    // any other failure makes the picture incomplete.
                    int error=Marshal.GetLastWin32Error();
                    if(ThreadAlive(thread)){complete=false;state.Incomplete="thread-query:"+error;}
                    continue;
                }
                if(capture==IntPtr.Zero)continue;
                if(ClassOf(capture)==OleDragClass){state.OleDrag=true;if(state.OleWindow==IntPtr.Zero)state.OleWindow=capture;}
                else if(state.Ordinary==IntPtr.Zero)state.Ordinary=capture;
            }
            state.LeftDown=(GetAsyncKeyState(1)&0x8000)!=0;
            state.Complete=complete;
            return state;
        }
        static bool CaptureOf(uint thread,out IntPtr capture)
        {
            capture=IntPtr.Zero;
            GUITHREADINFO info=new GUITHREADINFO();info.Size=Marshal.SizeOf(typeof(GUITHREADINFO));
            if(!GetGUIThreadInfo(thread,ref info))return false;
            capture=info.Capture;return true;
        }
        static bool ThreadAlive(uint thread)
        {
            IntPtr handle=OpenThread(0x0800,false,thread);   // THREAD_QUERY_LIMITED_INFORMATION
            if(handle==IntPtr.Zero)return Marshal.GetLastWin32Error()!=87; // ERROR_INVALID_PARAMETER: gone
            try{uint code;return !GetExitCodeThread(handle,out code)||code==259;}  // STILL_ACTIVE (or unknown)
            finally{CloseHandle(handle);}
        }
        // The capture state of another desktop is invisible to us: only the input
        // desktop that this thread is attached to counts as a complete picture.
        static bool OnInputDesktop()
        {
            IntPtr input=OpenInputDesktop(0,false,0x0001);    // DESKTOP_READOBJECTS
            if(input==IntPtr.Zero)return false;
            try
            {
                string mine=DesktopName(GetThreadDesktop(GetCurrentThreadId()));
                string active=DesktopName(input);
                return mine!=null&&active!=null&&String.Equals(mine,active,StringComparison.OrdinalIgnoreCase);
            }
            finally{CloseDesktop(input);}
        }
        static string DesktopName(IntPtr desktop)
        {
            if(desktop==IntPtr.Zero)return null;
            byte[] buffer=new byte[512];uint needed;
            if(!GetUserObjectInformation(desktop,2,buffer,(uint)buffer.Length,out needed))return null;   // UOI_NAME
            return Encoding.Unicode.GetString(buffer,0,(int)Math.Min(needed,(uint)buffer.Length)).TrimEnd('\0');
        }
        static string ClassOf(IntPtr window){StringBuilder text=new StringBuilder(64);GetClassName(window,text,text.Capacity);return text.ToString();}

        [StructLayout(LayoutKind.Sequential)] struct RECT{public int Left,Top,Right,Bottom;}
        [StructLayout(LayoutKind.Sequential)] struct GUITHREADINFO{public int Size,Flags;public IntPtr Active,Focus,Capture,MenuOwner,MoveSize,Caret;public RECT CaretRect;}
        delegate bool EnumProc(IntPtr window,IntPtr param);
        [DllImport("user32.dll",SetLastError=true)] static extern bool GetGUIThreadInfo(uint thread,ref GUITHREADINFO info);
        [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr parent,IntPtr after,string className,string title);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window,out uint pid);
        [DllImport("user32.dll",SetLastError=true)] static extern bool EnumWindows(EnumProc callback,IntPtr param);
        [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetClassName(IntPtr window,StringBuilder name,int count);
        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll",SetLastError=true)] static extern IntPtr OpenInputDesktop(uint flags,bool inherit,uint access);
        [DllImport("user32.dll")] static extern bool CloseDesktop(IntPtr desktop);
        [DllImport("user32.dll")] static extern IntPtr GetThreadDesktop(uint thread);
        [DllImport("user32.dll",SetLastError=true)] static extern bool GetUserObjectInformation(IntPtr handle,int index,byte[] info,uint length,out uint needed);
        [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
        [DllImport("kernel32.dll",SetLastError=true)] static extern IntPtr OpenThread(uint access,bool inherit,uint thread);
        [DllImport("kernel32.dll")] static extern bool GetExitCodeThread(IntPtr thread,out uint code);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    }
}
