// Own-window synthetic checks only. Never begins a real drag or queries Explorer.
using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using MwbTransfer;

internal static class DragPreviewTests
{
    static int passed,failed;
    static string output;
    const BindingFlags PrivateStatic=BindingFlags.NonPublic|BindingFlags.Static;
    const BindingFlags PrivateInstance=BindingFlags.NonPublic|BindingFlags.Instance;
    [STAThread] static int Main(string[] args)
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
        Application.EnableVisualStyles();Transfer.TestMode=true;
        output=Path.GetFullPath(args.Length>0?args[0]:Path.Combine(Path.GetTempPath(),"mwb-drag-preview-tests"));
        Directory.CreateDirectory(output);
        IntPtr previous=ProgressWindow.EnterDpiContext();
        try
        {
            Run("hover cache follows one icon but invalidates moved background and changed windows",CacheIdentity);
            Run("end clears transient state and invalidates its generation",EndClearsState);
            Run("late lookup cannot resurrect ended drag or replace a new drag",LateResult);
            Run("lookup completion releases the singleton query gate",LookupGateReleased);
            Run("overlay has no-activate, tool-window and click-through native behavior",NativeWindowFlags);
            Run("only a crisp border is rendered, with transparent center and no text",FeedbackRendering);
            Run("file badge follows negative coordinates, scales and does not activate or capture input",CursorFeedback);
            Run("inactive refresh hides visible preview on its 80 ms timer",InactiveRefresh);
        }
        finally { DragPreview.End();ProgressWindow.RestoreDpiContext(previous); }
        Console.WriteLine("RESULT passed="+passed+" failed="+failed);
        Console.WriteLine("Synthetic own-window test only: no Explorer lookup, user files, drag, real peer, or desktop cursor movement.");
        return failed==0?0:1;
    }
    static void Run(string name,Action action)
    { try { action();passed++;Console.WriteLine("PASS "+name); } catch(Exception ex) { failed++;Console.WriteLine("FAIL "+name+" :: "+ex); } }
    static void Assert(bool ok,string message) { if(!ok)throw new InvalidOperationException(message); }
    static DropTarget.Snapshot Snapshot(int x,int y)
    { return new DropTarget.Snapshot{Window=new IntPtr(1234),ProcessId=2345,Desktop=true,Point=new DropTarget.POINT{X=x,Y=y},WindowBounds=new DropTarget.RECT{Left=0,Top=0,Right=1000,Bottom=800}}; }
    static DropTarget.ResolvedTarget Target()
    { return new DropTarget.ResolvedTarget{Directory=@"D:\synthetic-only\destination",DisplayName="目标文件夹 & 图纸",Bounds=new Rectangle(80,80,140,95)}; }
    static void CacheIdentity()
    {
        DropTarget.Snapshot old=Snapshot(100,100),current=Snapshot(100,100);DropTarget.ResolvedTarget target=Target();
        Assert(DragPreview.SameTarget(old,current,target),"Stationary matching target was rejected");
        current.Point.X++;Assert(DragPreview.SameTarget(old,current,target),"Motion within the same folder icon lost confirmation");
        target.Background=true;Assert(!DragPreview.SameTarget(old,current,target),"Motion onto another icon reused full-folder background confirmation");
        current=Snapshot(100,100);Assert(DragPreview.SameTarget(old,current,target),"Stationary background lost confirmation");target.Background=false;
        current.Window=new IntPtr(1235);Assert(!DragPreview.SameTarget(old,current,target),"New window reused stale confirmation");current=Snapshot(100,100);
        current.ProcessId++;Assert(!DragPreview.SameTarget(old,current,target),"New process reused stale confirmation");current=Snapshot(100,100);
        current.Desktop=false;Assert(!DragPreview.SameTarget(old,current,target),"Window mode reused stale confirmation");current=Snapshot(100,100);
        current.WindowBounds.Right++;Assert(!DragPreview.SameTarget(old,current,target),"Moved or resized window reused stale confirmation");
        Assert(!DragPreview.SameTarget(Snapshot(220,100),Snapshot(220,100),target),"Right edge outside target accepted");
        Assert(!DragPreview.SameTarget(Snapshot(100,175),Snapshot(100,175),target),"Bottom edge outside target accepted");
        Assert(!DragPreview.SameTarget(null,old,target)&&!DragPreview.SameTarget(old,null,target)&&!DragPreview.SameTarget(old,old,null),"Null target evidence accepted");
    }
    static FieldInfo StaticField(string name) { return typeof(DragPreview).GetField(name,PrivateStatic); }
    static object Get(string name) { return StaticField(name).GetValue(null); }
    static void Set(string name,object value) { StaticField(name).SetValue(null,value); }
    static void Seed(int version)
    {
        Set("active",true);Set("generation",version);Set("overlay",new IntPtr(1234));Set("latest",null);Set("lookupBusy",1);
    }
    static void Publish(int version)
    {
        MethodInfo method=typeof(DragPreview).GetMethod("CompleteLookup",PrivateStatic);
        Assert(method!=null,"CompleteLookup test seam missing");
        method.Invoke(null,new object[]{version,Snapshot(100,100),Target()});
    }
    static void EndClearsState()
    {
        Seed(50);Publish(50);Assert(Get("latest")!=null,"Current result was not accepted");
        DragPreview.End();
        Assert(!(bool)Get("active")&&Get("latest")==null&&(IntPtr)Get("overlay")==IntPtr.Zero,"Drag end retained active target evidence");
        Assert((int)Get("generation")==51,"Drag end failed to invalidate pending lookup");
    }
    static void LateResult()
    {
        Seed(60);DragPreview.End();Publish(60);Assert(Get("latest")==null,"Ended drag was resurrected by late result");
        Seed(62);Publish(60);Assert(Get("latest")==null,"Old drag result replaced new drag");
        Publish(62);Assert(Get("latest")!=null,"Current drag result rejected");DragPreview.End();
        // Alternate end and publication under real contention; after both finish no target may remain.
        for(int n=0;n<32;n++)
        {
            int version=100+n*2;Seed(version);
            using(ManualResetEvent start=new ManualResetEvent(false))
            {
                Exception failure=null;Thread completion=new Thread(delegate(){try{start.WaitOne();Publish(version);}catch(Exception ex){failure=ex;}});
                completion.Start();start.Set();DragPreview.End();Assert(completion.Join(2000),"Synthetic completion did not stop");
                if(failure!=null)throw failure;
                Assert(Get("latest")==null&&!(bool)Get("active"),"End/publication race retained a target");
            }
        }
    }
    static void LookupGateReleased()
    {
        Seed(200);Publish(199);Assert((int)Get("lookupBusy")==0,"Stale completion leaked query gate");
        Seed(202);Publish(202);Assert((int)Get("lookupBusy")==0,"Current completion leaked query gate");DragPreview.End();
    }
    static void NativeWindowFlags()
    {
        using(DragPreview.PreviewWindow window=new DragPreview.PreviewWindow())
        {
            window.Opacity=0;IntPtr handle=window.Handle;long flags=GetWindowLong(handle,-20).ToInt64();
            Assert((flags&0x08000000)!=0&&(flags&0x80)!=0&&(flags&0x20)!=0,"Native no-activate/tool-window/transparent flags missing");
            Assert(!window.ShowInTaskbar&&window.FormBorderStyle==FormBorderStyle.None,"Overlay leaked normal app chrome");
            Assert(SendMessage(handle,0x84,IntPtr.Zero,IntPtr.Zero)==new IntPtr(-1),"Hit test is not transparent");
            Assert(SendMessage(handle,0x21,IntPtr.Zero,IntPtr.Zero)==new IntPtr(3),"Mouse interaction activates overlay");
            IntPtr foreground=GetForegroundWindow();window.Display(Target());
            Assert(GetForegroundWindow()==foreground,"Displaying own preview stole foreground");
            window.ClearAndHide();Assert(!window.Visible,"Clear did not hide preview");
        }
    }
    static object WindowField(DragPreview.PreviewWindow window,string name)
    { return typeof(DragPreview.PreviewWindow).GetField(name,PrivateInstance).GetValue(window); }
    static void FeedbackRendering()
    {
        using(DragPreview.PreviewWindow window=new DragPreview.PreviewWindow())
        {
            window.Opacity=0;
            window.Display(Target());
            Assert((Rectangle)WindowField(window,"outline")!=Rectangle.Empty,"Confirmed folder has no outline");
            Assert(window.Text==String.Empty&&window.Controls.Count==0,"Preview contains UI text or child controls");
            foreach(FieldInfo field in typeof(DragPreview.PreviewWindow).GetFields(PrivateInstance|BindingFlags.DeclaredOnly))
                Assert(field.FieldType!=typeof(string)&&field.FieldType!=typeof(Font),"Preview retains text or font state: "+field.Name);
            Save(window,"drag-preview-confirmed.png");
            for(int n=0;n<40;n++)window.Display(Target());
            Save(window,"drag-preview-repeated.png");
            window.Display(null);
            Assert(!window.Visible&&(Rectangle)WindowField(window,"outline")==Rectangle.Empty,"Unresolved target retained a visible border");
            window.Display(Target());
            DropTarget.ResolvedTarget invalid=Target();invalid.Bounds=Rectangle.Empty;window.Display(invalid);
            Assert(!window.Visible&&(Rectangle)WindowField(window,"outline")==Rectangle.Empty,"Empty target bounds retained a visible border");
            window.Display(Target());
            window.ClearAndHide();
            Assert(!window.Visible&&(Rectangle)WindowField(window,"outline")==Rectangle.Empty,"Closed preview retains per-drag display state");
        }
    }
    static void Save(Form window,string name)
    {
        using(Bitmap bitmap=new Bitmap(window.Width,window.Height))
        {
            window.DrawToBitmap(bitmap,new Rectangle(Point.Empty,bitmap.Size));
            int colored=0,transparent=0;int green=Color.FromArgb(36,132,79).ToArgb(),key=window.TransparencyKey.ToArgb();
            for(int y=0;y<bitmap.Height;y++)for(int x=0;x<bitmap.Width;x++)
            {
                Color c=bitmap.GetPixel(x,y);
                if(c.ToArgb()==green)colored++;
                else if(c.ToArgb()==key)transparent++;
                else throw new InvalidOperationException("Border contains blended transparency-key pixels or additional UI: "+c);
            }
            Assert(colored>50&&transparent>colored,"Border or transparent inner area missing");
            Assert(bitmap.GetPixel(bitmap.Width/2,bitmap.Height/2).ToArgb()==key,"Folder center is not transparent");
            Assert(bitmap.GetPixel(0,0).ToArgb()==green&&bitmap.GetPixel(bitmap.Width-1,bitmap.Height-1).ToArgb()==green,"Border does not reach crisp outer corners");
            bitmap.Save(Path.Combine(output,name),ImageFormat.Png);
        }
    }
    static void InactiveRefresh()
    {
        DragPreview.End();
        Type type=typeof(DragPreview).GetNestedType("PreviewContext",BindingFlags.NonPublic);
        using(ApplicationContext context=(ApplicationContext)Activator.CreateInstance(type,true))
        {
            System.Windows.Forms.Timer timer=(System.Windows.Forms.Timer)type.GetField("timer",PrivateInstance).GetValue(context);
            timer.Stop();Assert(timer.Interval<=80,"Release hide timer exceeds 80 ms");
            DragPreview.PreviewWindow window=(DragPreview.PreviewWindow)type.GetField("window",PrivateInstance).GetValue(context);
            window.Opacity=0;window.Display(Target());Assert(window.Visible,"Synthetic preview was not shown");
            DragPreview.CursorWindow cursor=(DragPreview.CursorWindow)type.GetField("cursor",PrivateInstance).GetValue(context);
            cursor.Opacity=0;cursor.DisplayAt(new Point(100,100),96);
            Assert(cursor.Visible,"File badge was not shown");
            type.GetMethod("Tick",PrivateInstance).Invoke(context,null);
            Assert(!window.Visible&&(Rectangle)WindowField(window,"outline")==Rectangle.Empty,"Inactive tick failed to clear transient preview");
            Assert(!cursor.Visible,"Inactive drag retained file badge");
            Assert((int)Get("lookupBusy")==0,"Inactive tick started a folder lookup");
        }
    }
    static void CursorFeedback()
    {
        using(DragPreview.CursorWindow cursor=new DragPreview.CursorWindow())
        {
            cursor.Opacity=0;
            IntPtr foreground=GetForegroundWindow();
            long flags=GetWindowLong(cursor.Handle,-20).ToInt64();
            Assert((flags&0x080000A0)==0x080000A0,"File badge lacks non-interactive window flags");
            Assert(SendMessage(cursor.Handle,0x84,IntPtr.Zero,IntPtr.Zero)==new IntPtr(-1),"Badge captures hit testing");
            Assert(SendMessage(cursor.Handle,0x21,IntPtr.Zero,IntPtr.Zero)==new IntPtr(3),"Badge activates on mouse input");
            foreach(int dpi in new[]{96,120,144,192})
            {
                cursor.DisplayAt(new Point(-700,200),dpi);
                Assert(cursor.Location==new Point(-700+16*dpi/96,200+20*dpi/96),"Badge lost negative-screen pointer offset");
                Assert(cursor.Size==new Size(26*dpi/96,32*dpi/96),"Badge DPI dimensions incorrect");
                Assert(cursor.Visible&&cursor.Text==String.Empty&&cursor.Controls.Count==0,"Badge hidden or has unwanted text");
                using(Bitmap bitmap=new Bitmap(cursor.Width,cursor.Height))
                {
                    cursor.DrawToBitmap(bitmap,new Rectangle(Point.Empty,bitmap.Size));
                    Assert(bitmap.GetPixel(0,0).ToArgb()==Color.Magenta.ToArgb(),"Badge background not transparent");
                    Assert(bitmap.GetPixel(10*dpi/96,11*dpi/96).ToArgb()==Color.White.ToArgb(),"Badge has no white paper face");
                    bitmap.Save(Path.Combine(output,"drag-file-icon-"+dpi+"dpi.png"),ImageFormat.Png);
                }
            }
            Assert(GetForegroundWindow()==foreground,"File badge stole foreground");
            cursor.ClearAndHide();Assert(!cursor.Visible,"Badge did not hide");
        }
    }
    [DllImport("user32.dll",EntryPoint="GetWindowLongPtrW")] static extern IntPtr GetWindowLong(IntPtr window,int index);
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr window,int message,IntPtr wparam,IntPtr lparam);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
}
