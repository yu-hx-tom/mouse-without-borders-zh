using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace MwbTransfer
{
    // Transient, non-interactive drag feedback. No history, shell selection, or file writes.
    internal static class DragPreview
    {
        static readonly object Gate=new object();
        static bool active,started;
        static int generation,lookupBusy;
        static IntPtr overlay;
        static Result latest;
        sealed class Result
        {
            internal DropTarget.Snapshot Snapshot;
            internal DropTarget.ResolvedTarget Target;
            internal DateTime Completed;
        }

        internal static void Begin(IntPtr owner)
        {
            lock(Gate)
            {
                if(active && overlay==owner)return;
                active=true;overlay=owner;generation++;latest=null;
                if(started)return;
                started=true;
                Thread thread=new Thread(delegate() {
                    IntPtr dpi=ProgressWindow.EnterDpiContext();
                    try { using(PreviewContext context=new PreviewContext())Application.Run(context); }
                    catch(Exception) { /* Preview failure must never interrupt shared input. */ }
                    finally { ProgressWindow.RestoreDpiContext(dpi);lock(Gate){started=false;active=false;latest=null;} }
                });
                thread.IsBackground=true;thread.Name="MWB drop target preview";thread.SetApartmentState(ApartmentState.STA);
                try { thread.Start(); } catch { started=false;active=false;throw; }
            }
        }
        internal static void End()
        { lock(Gate){active=false;generation++;latest=null;overlay=IntPtr.Zero;} }

        internal static void CompleteLookup(int version,DropTarget.Snapshot snapshot,DropTarget.ResolvedTarget target)
        {
            lock(Gate) if(active && generation==version)
                latest=new Result{Snapshot=snapshot,Target=target,Completed=DateTime.UtcNow};
            Interlocked.Exchange(ref lookupBusy,0);
        }

        internal static bool SameTarget(DropTarget.Snapshot old,DropTarget.Snapshot current,DropTarget.ResolvedTarget target)
        {
            return old!=null && current!=null && target!=null && old.Window==current.Window && old.ProcessId==current.ProcessId &&
                old.Desktop==current.Desktop && old.WindowBounds.Equals(current.WindowBounds) &&
                (!target.Background || (old.Point.X==current.Point.X && old.Point.Y==current.Point.Y)) &&
                target.Bounds.Contains(current.Point.X,current.Point.Y);
        }

        sealed class PreviewContext : ApplicationContext
        {
            readonly PreviewWindow window=new PreviewWindow();
            readonly CursorWindow cursor=new CursorWindow();
            readonly System.Windows.Forms.Timer timer=new System.Windows.Forms.Timer();
            DateTime lastQuery=DateTime.MinValue;
            DateTime lastSnapshot=DateTime.MinValue;
            int lastGeneration;
            internal PreviewContext()
            {
                DropTarget.IgnorePreviewWindow(window.Handle);
                DropTarget.IgnoreDragIconWindow(cursor.Handle);
                timer.Interval=16;
                timer.Tick+=delegate { try { Tick(); } catch(Exception) { window.ClearAndHide();cursor.ClearAndHide(); } };
                timer.Start();
            }
            void Tick()
            {
                int version;IntPtr owner;Result result;bool enabled;
                lock(Gate)
                {
                    enabled=active;version=generation;owner=overlay;result=latest;
                }
                // v9.0.0.7: idle at 5 Hz instead of 60 Hz when no remote drag is shown.
                if(!enabled){window.ClearAndHide();cursor.ClearAndHide();if(timer.Interval!=200)timer.Interval=200;return;}
                if(timer.Interval!=16)timer.Interval=16;
                cursor.FollowPointer();
                if(version!=lastGeneration){lastGeneration=version;lastQuery=lastSnapshot=DateTime.MinValue;}
                // Keep shell hit testing at its original cadence; only the small icon follows every frame.
                if((DateTime.UtcNow-lastSnapshot).TotalMilliseconds<80)return;
                lastSnapshot=DateTime.UtcNow;
                DropTarget.Snapshot current=DropTarget.CaptureSnapshot(owner);
                bool fresh=result!=null && (DateTime.UtcNow-result.Completed).TotalMilliseconds<700 && SameTarget(result.Snapshot,current,result.Target);
                window.Display(fresh?result.Target:null);
                if(current==null || (DateTime.UtcNow-lastQuery).TotalMilliseconds<180 ||
                    (fresh && (DateTime.UtcNow-result.Completed).TotalMilliseconds<400) ||
                    Interlocked.CompareExchange(ref lookupBusy,1,0)!=0)return;
                lastQuery=DateTime.UtcNow;
                Thread query=new Thread(delegate() {
                    DropTarget.ResolvedTarget target=null;
                    try { UserContext.Run(delegate { target=DropTarget.ResolveTarget(current); }); }
                    catch(Exception) { }
                    finally
                    {
                        CompleteLookup(version,current,target);
                    }
                });
                query.IsBackground=true;query.Name="MWB hover folder lookup";query.SetApartmentState(ApartmentState.STA);
                try { query.Start(); } catch { Interlocked.Exchange(ref lookupBusy,0); }
            }
            protected override void Dispose(bool disposing)
            {
                if(disposing){timer.Stop();timer.Dispose();DropTarget.IgnorePreviewWindow(IntPtr.Zero);DropTarget.IgnoreDragIconWindow(IntPtr.Zero);cursor.Dispose();window.Dispose();}
                base.Dispose(disposing);
            }
        }

        internal class PreviewWindow : Form
        {
            Rectangle outline;
            int dpi=96;
            internal PreviewWindow()
            {
                FormBorderStyle=FormBorderStyle.None;ShowInTaskbar=false;TopMost=true;
                AutoScaleMode=AutoScaleMode.None;StartPosition=FormStartPosition.Manual;
                BackColor=Color.Magenta;TransparencyKey=BackColor;DoubleBuffered=true;
            }
            protected override bool ShowWithoutActivation { get { return true; } }
            protected override CreateParams CreateParams
            { get { CreateParams p=base.CreateParams;p.ExStyle|=0x08000000|0x00000080|0x00000020;return p; } }
            protected override void WndProc(ref Message m)
            {
                if(m.Msg==0x0084){m.Result=new IntPtr(-1);return;} // HTTRANSPARENT
                if(m.Msg==0x0021){m.Result=new IntPtr(3);return;} // MA_NOACTIVATE
                base.WndProc(ref m);
            }
            internal void ClearAndHide()
            { if(Visible)Hide();outline=Rectangle.Empty; }
            internal void Display(DropTarget.ResolvedTarget target)
            {
                if(target==null || target.Bounds.Width<=0 || target.Bounds.Height<=0){ClearAndHide();return;}
                int next=96;
                try { next=(int)GetDpiForWindow(Handle); } catch(EntryPointNotFoundException) { }
                dpi=Math.Max(96,next);
                int pad=Math.Max(4,6*dpi/96);
                Bounds=Rectangle.Inflate(target.Bounds,pad,pad);
                outline=ClientRectangle;
                if(!Visible)Show();
                Invalidate();
            }
            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                if(outline.IsEmpty)return;
                int width=Math.Max(2,2*dpi/96);
                // Solid integer-pixel bands avoid blending green with the magenta transparency key.
                using(Brush brush=new SolidBrush(Color.FromArgb(36,132,79)))
                {
                    e.Graphics.FillRectangle(brush,0,0,outline.Width,width);
                    e.Graphics.FillRectangle(brush,0,outline.Height-width,outline.Width,width);
                    e.Graphics.FillRectangle(brush,0,0,width,outline.Height);
                    e.Graphics.FillRectangle(brush,outline.Width-width,0,width,outline.Height);
                }
            }
            [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr hwnd);
        }

        // This lives only on a remote drop target. The source keeps Windows' native OLE drag image.
        internal sealed class CursorWindow : PreviewWindow
        {
            int iconDpi;
            IntPtr monitor;
            internal void FollowPointer()
            {
                DropTarget.POINT point;
                if(!GetPhysicalCursorPos(out point)){ClearAndHide();return;}
                IntPtr next=MonitorFromPoint(point,2);
                int dpi=iconDpi;
                if(next!=monitor || dpi==0)
                {
                    dpi=96;uint x,y;
                    try { if(GetDpiForMonitor(next,0,out x,out y)==0)dpi=(int)x; }
                    catch(EntryPointNotFoundException) { }
                    catch(DllNotFoundException) { }
                    monitor=next;
                }
                DisplayAt(new Point(point.X,point.Y),dpi);
            }
            internal void DisplayAt(Point point,int dpi)
            {
                dpi=Math.Max(96,dpi);
                if(iconDpi!=dpi)
                {
                    iconDpi=dpi;
                    Size=new Size(26*dpi/96,32*dpi/96);
                    Invalidate();
                }
                Location=new Point(point.X+16*dpi/96,point.Y+20*dpi/96);
                if(!Visible)Show();
            }
            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                // No anti-aliasing against the transparent color key: keep the paper edge crisp.
                var state=e.Graphics.Save();
                e.Graphics.ScaleTransform(iconDpi/96f,iconDpi/96f);
                Point[] page={new Point(2,1),new Point(16,1),new Point(23,8),new Point(23,29),new Point(2,29)};
                using(Brush paper=new SolidBrush(Color.White))e.Graphics.FillPolygon(paper,page);
                using(Brush fold=new SolidBrush(Color.FromArgb(213,234,249)))
                    e.Graphics.FillPolygon(fold,new[]{new Point(16,1),new Point(23,8),new Point(16,8)});
                using(Pen edge=new Pen(Color.FromArgb(48,88,120),1))
                {
                    e.Graphics.DrawPolygon(edge,page);
                    e.Graphics.DrawLines(edge,new[]{new Point(16,1),new Point(16,8),new Point(23,8)});
                }
                using(Pen line=new Pen(Color.FromArgb(136,161,180),1))
                {
                    e.Graphics.DrawLine(line,6,14,18,14);
                    e.Graphics.DrawLine(line,6,18,18,18);
                    e.Graphics.DrawLine(line,6,22,14,22);
                }
                e.Graphics.Restore(state);
            }
            [DllImport("user32.dll")] static extern bool GetPhysicalCursorPos(out DropTarget.POINT point);
            [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(DropTarget.POINT point,uint flags);
            [DllImport("shcore.dll")] static extern int GetDpiForMonitor(IntPtr monitor,int type,out uint x,out uint y);
        }
    }
}
