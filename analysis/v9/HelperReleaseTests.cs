using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using MwbTransfer;
class HelperReleaseTests
{
    [StructLayout(LayoutKind.Sequential)] struct Packet {internal IntPtr Kind;internal int Length;internal IntPtr Bytes;}
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr window,IntPtr after,int x,int y,int width,int height,uint flags);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr window);
    static int checks;
    static void Check(bool condition,string name){if(!condition)throw new Exception(name);checks++;}
    static bool Command(Form helper,Form main,Guid leg,int command)
    {
        byte[] bytes=new byte[24];Array.Copy(BitConverter.GetBytes(1),bytes,4);Array.Copy(BitConverter.GetBytes(command),0,bytes,4,4);Array.Copy(leg.ToByteArray(),0,bytes,8,16);
        IntPtr data=Marshal.AllocHGlobal(24),packet=Marshal.AllocHGlobal(Marshal.SizeOf(typeof(Packet)));
        try
        {
            Marshal.Copy(bytes,0,data,24);Marshal.StructureToPtr(new Packet{Kind=new IntPtr(0x3944574d),Length=24,Bytes=data},packet,false);
            var msg=Message.Create(helper.Handle,0x4a,main.Handle,packet);
            Check(HelperNativeDrag.HandleMessage(helper,ref msg),"helper handles owned IPC");
            return msg.Result==new IntPtr(1);
        }
        finally{Marshal.FreeHGlobal(data);Marshal.FreeHGlobal(packet);}
    }
    [STAThread] static int Main()
    {
        try
        {
            using(var main=new Form())using(var helper=new Form{ShowInTaskbar=false,FormBorderStyle=FormBorderStyle.None,Opacity=0.11,AllowDrop=true})
            {
                Guid leg=Guid.NewGuid();Check(Command(helper,main,leg,1),"capture arm");helper.Hide();
                Check(Command(helper,main,leg,3),"reject arm");
                SetWindowPos(helper.Handle,new IntPtr(-1),-30000,-30000,32,32,0x50);Application.DoEvents();
                Check(!helper.Visible&&IsWindowVisible(helper.Handle),"reproduce native visible but WinForms cached invisible");
                Check(!Command(helper,main,leg,4),"visibility alone cannot authorize UP without OLE evidence");
                var drag=new DragEventArgs(new DataObject(),1,-29990,-29990,DragDropEffects.Copy,DragDropEffects.Copy);
                HelperNativeDrag.OnDragEnter(helper,drag,delegate(string unused){throw new Exception("Reject must never capture/send data");});
                Check(drag.Effect==DragDropEffects.None,"OLE reject target never accepts a drop");
                Check(Command(helper,main,leg,4),"native-visible reject target permits cleanup after fresh OLE/None");
                Check(Command(helper,main,leg,3)&&Command(helper,main,leg,4),"same-leg retry preserves OLE observation");
                helper.Hide();Check(!Command(helper,main,leg,4),"hidden HWND cannot authorize release");
                Check(Command(helper,main,leg,2)&&!HelperNativeDrag.OwnsWindow(helper),"invalidate cleans helper ownership");

                // v9.0.0.8: the time-based command 8 is gone; the helper never infers "no OLE".
                Guid quiet=Guid.NewGuid();
                Check(Command(helper,main,quiet,1),"capture arm for a non-OLE hold");helper.Hide();
                Check(Command(helper,main,quiet,3),"reject arm");
                SetWindowPos(helper.Handle,new IntPtr(-1),-30000,-30000,32,32,0x50);Application.DoEvents();
                System.Threading.Thread.Sleep(600);
                Check(!Command(helper,main,quiet,8),"removed quiet-period command 8 never grants anything");
                Check(!Command(helper,main,quiet,4),"no OLE event: the strict query still refuses, however long it waits");
                // v9.0.0.9 (Codex 9.0.0.8 #3): plain WM_MOUSEMOVE never opens the strict query.
                var move=Message.Create(helper.Handle,0x200,IntPtr.Zero,IntPtr.Zero);HelperNativeDrag.HandleMessage(helper,ref move);
                Check(!Command(helper,main,quiet,4),"strict QueryReject stays FALSE after only WM_MOUSEMOVE (Codex replay inverted)");
                Check(Command(helper,main,quiet,9),"QueryNoOleSeen: armed, visible sink with no OLE callback on this leg");
                helper.Hide();Check(!Command(helper,main,quiet,9),"QueryNoOleSeen is refused while the sink is hidden");
                SetWindowPos(helper.Handle,new IntPtr(-1),-30000,-30000,32,32,0x50);Application.DoEvents();
                // A release that reaches the armed sink (no OLE drag, proof path) is swallowed.
                var up=Message.Create(helper.Handle,0x202,IntPtr.Zero,IntPtr.Zero);
                Check(HelperNativeDrag.HandleMessage(helper,ref up),"armed sink swallows WM_LBUTTONUP (never reaches the helper's own handlers)");
                var press=Message.Create(helper.Handle,0x201,IntPtr.Zero,IntPtr.Zero);
                Check(HelperNativeDrag.HandleMessage(helper,ref press),"armed sink swallows WM_LBUTTONDOWN");
                var late=new DragEventArgs(new DataObject(),1,-29990,-29990,DragDropEffects.Copy,DragDropEffects.Copy);
                HelperNativeDrag.OnDragEnter(helper,late,delegate(string unused){throw new Exception("reject never captures");});
                Check(late.Effect==DragDropEffects.None&&Command(helper,main,quiet,4),"OLE on the sink is told None and the strict barrier opens");
                Check(!Command(helper,main,quiet,9),"after any OLE callback, QueryNoOleSeen is permanently false for this leg");
                Check(Command(helper,main,quiet,2),"invalidate the quiet leg");
                var after=Message.Create(helper.Handle,0x202,IntPtr.Zero,IntPtr.Zero);
                Check(!HelperNativeDrag.HandleMessage(helper,ref after),"an unarmed helper passes mouse messages through unchanged");
                Guid captured=Guid.NewGuid();
                Check(Command(helper,main,captured,1),"arm a leg before simulating a main-process restart");
                // A restarted main process: commands from a new trusted PID clear the stale leg.
                HelperNativeDrag.AdoptOwner(helper,0x7FFFFFF0u);
                Check(HelperNativeDrag.ActiveLeg==Guid.Empty&&!HelperNativeDrag.OwnsWindow(helper),"stale leg from a previous main process is cleared");
                Guid fresh=Guid.NewGuid();
                HelperNativeDrag.AdoptOwner(helper,(uint)System.Diagnostics.Process.GetCurrentProcess().Id);
                Check(Command(helper,main,fresh,1)&&HelperNativeDrag.ActiveLeg==fresh,"the new main can arm immediately");
                Check(Command(helper,main,fresh,2),"cleanup");
            }
            Console.WriteLine("PASS "+checks+" helper visibility/reject checks; own offscreen windows only, no input injection.");return 0;
        }
        catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
}
