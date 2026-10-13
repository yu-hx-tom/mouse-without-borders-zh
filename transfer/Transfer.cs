using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using System.Windows.Forms;

namespace MwbTransfer
{
    public static partial class Transfer
    {
        static readonly object Gate = new object();
        static Pending pending;
        static bool choosing;
        static int resolving;
        internal static bool TestMode=false;
        sealed class Pending
        {
            public string Peer, Directory, UserSid;
            public DateTime Expires;
            public Operation Operation;
        }

        public static void BeginDragPreview(IntPtr overlayHandle)
        { try { if(!TestMode)DragPreview.Begin(overlayHandle); } catch(Exception) { } }
        public static void EndDragPreview()
        { try { DragPreview.End(); } catch(Exception) { } }

        // Invoked at mouse-up. Only native hit-test snapshot runs on the mouse hook.
        public static void BeginDrop(string peer, IntPtr overlayHandle, Action<string> requestRemoteClipboard)
        { BeginDropCore(peer,overlayHandle,requestRemoteClipboard,null); }

        // The original OLE drag ends at the first crossing. A return to its source uses the saved path.
        public static void BeginLocalDrop(string sourcePath, IntPtr overlayHandle)
        {
            if(String.IsNullOrWhiteSpace(sourcePath)) { EndDragPreview();Notice("无法确定拖动的源文件，请重新拖放。");return; }
            BeginDropCore(null,overlayHandle,null,sourcePath);
        }

        static void BeginDropCore(string peer, IntPtr overlayHandle, Action<string> requestRemoteClipboard, string localSource)
        {
            EndDragPreview();
            try
            {
                lock(Gate)
                {
                    ExpirePending();
                    if(choosing || pending!=null) { Notice("请等待上一次文件接收完成后再拖入新文件。"); return; }
                    choosing=true;
                }
                DropTarget.Snapshot snapshot=DropTarget.CaptureSnapshot(overlayHandle);
                string snapshotError=DropTarget.LastError;
                Thread worker=new Thread(delegate() {
                    Operation op=null;
                    try
                    {
                        string target=null,userSid=null;
                        UserContext.Run(delegate {
                            string reason;
                            target=ResolveBounded(snapshot,out reason);
                            if(snapshot==null)reason=snapshotError;
                            if(target!=null) { try { target=ValidateDirectory(target); } catch(IOException ex) { reason=ex.Message; target=null; } }
                            if(target==null)
                            {
                                using(FolderBrowserDialog picker=new FolderBrowserDialog())
                                {
                                    picker.Description=(reason??"无法自动确定松开的位置。")+"\n请选择这次接收的本机文件夹。";
                                    picker.ShowNewFolderButton=true;
                                    if(picker.ShowDialog()!=DialogResult.OK) return;
                                    target=ValidateDirectory(picker.SelectedPath);
                                }
                            }
                            userSid=WindowsIdentity.GetCurrent().User.Value;
                        });
                        if(String.IsNullOrEmpty(target)) return;
                        if(localSource!=null)
                        {
                            UserContext.Run(delegate {
                                if(WindowsIdentity.GetCurrent().User.Value!=userSid) throw new IOException("当前登录用户已变化，请重新拖放。");
                                op=NewOperation(false,SafeDisplayName(localSource),0,target);
                                CopyLocalFile(localSource,target,delegate(long count) { op.SetBytes(count); },op);
                            });
                            return;
                        }
                        if(String.IsNullOrWhiteSpace(peer)) throw new IOException("无法确认发送方电脑，请重新连接后再试。");
                        op=NewOperation(false,"等待文件",0,target);
                        op.SetStatus("等待发送端…",false,false);
                        lock(Gate) pending=new Pending { Peer=peer,Directory=target,UserSid=userSid,Expires=DateTime.UtcNow.AddMinutes(2),Operation=op };
                        requestRemoteClipboard("desktop");
                    }
                    catch(Exception ex)
                    {
                        if(op!=null) op.Fail(ex); else Notice("无法开始接收："+ex.Message);
                        lock(Gate) pending=null;
                    }
                    finally { lock(Gate) choosing=false; }
                });
                worker.IsBackground=true; worker.Name="MWB choose destination"; worker.SetApartmentState(ApartmentState.STA); worker.Start();
            }
            catch(Exception ex) { lock(Gate) choosing=false; Notice("无法识别接收位置："+ex.Message); }
        }

        static string ResolveBounded(DropTarget.Snapshot snapshot,out string reason)
        {
            reason=DropTarget.LastError;
            if(snapshot==null) return null;
            if(Interlocked.CompareExchange(ref resolving,1,0)!=0) { reason="资源管理器的上次目录查询仍未响应。";return null; }
            string path=null,error=null;
            Thread resolver=new Thread(delegate() {
                try { UserContext.Run(delegate { path=DropTarget.Resolve(snapshot); error=DropTarget.LastError; }); }
                catch(Exception ex) { error="读取目标目录失败："+ex.Message; }
                finally { Interlocked.Exchange(ref resolving,0); }
            });
            resolver.IsBackground=true; resolver.Name="MWB bounded folder lookup"; resolver.SetApartmentState(ApartmentState.STA);
            try { resolver.Start(); } catch { Interlocked.Exchange(ref resolving,0);throw; }
            if(!resolver.Join(1800)) { reason="资源管理器没有及时响应，无法确认落点。";return null; }
            reason=error;return path;
        }

        static void ExpirePending()
        {
            if(pending!=null && DateTime.UtcNow>pending.Expires)
            { if(!pending.Operation.CancelRequested)pending.Operation.Fail(new IOException("等待发送端超时，请重新拖入文件。")); pending=null; }
        }
        internal static void CheckPendingExpiry() { lock(Gate) ExpirePending(); }
        static bool CancelWaiting(Operation operation)
        {
            lock(Gate)
            {
                if(pending==null || !Object.ReferenceEquals(pending.Operation,operation))return false;
                // Keep one cancelled request until its late socket is consumed or it times out.
                // Legacy requests have no transfer ID; it must not claim a later drop's directory.
                pending.Directory=pending.UserSid=String.Empty;
                return true;
            }
        }

        // Same authenticated encrypted stream and exact body framing as the original sender.
        public static bool SendFile(Socket socket, Stream encrypted, string path)
        {
            Operation op=null;
            try
            {
                UserContext.Run(delegate {
                    using(FileStream source=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))
                    {
                        op=NewOperation(true,Path.GetFileName(path),source.Length,"发送至另一台电脑");
                        op.BindSocket(socket);
                        WriteBody(source,encrypted,source.Length,delegate(long count) { op.SetBytes(count); },op);
                    }
                });
                op.CompleteSend();
                return true;
            }
            catch(Exception ex)
            {
                if(op==null) op=NewOperation(true,SafeDisplayName(path),0,"发送失败");
                op.Fail(ex);
                try { if(socket!=null) socket.Close(); } catch { }
                return false;
            }
            finally { if(op!=null)op.DetachSocket(socket); }
        }

        // Header parsing and original peer authentication already succeeded before this hook.
        public static bool TryReceiveFile(string peer, Socket socket, Stream encrypted, Stream decrypted, string postAct, string headerName, long expectedTotal)
        {
            if(!String.Equals(postAct,"desktop",StringComparison.OrdinalIgnoreCase) ||
                (headerName!=null && (headerName.StartsWith("image",StringComparison.OrdinalIgnoreCase) || headerName.StartsWith("text",StringComparison.OrdinalIgnoreCase)))) return false;
            Pending context=null; Operation op=null;
            try
            {
                lock(Gate)
                {
                    ExpirePending();
                    if(pending!=null && String.Equals(pending.Peer,peer,StringComparison.OrdinalIgnoreCase)) { context=pending; pending=null; }
                }
                if(context==null) throw new IOException("没有与这次传输匹配的接收位置，请重新拖放文件。");
                op=context.Operation;
                op.BindSocket(socket);
                string name=SafeFileName(headerName);
                if(expectedTotal<0) throw new IOException("发送端提供的文件大小无效。");
                op.Begin(name,expectedTotal,context.Directory);
                UserContext.Run(delegate {
                    if(WindowsIdentity.GetCurrent().User.Value!=context.UserSid) throw new IOException("当前登录用户已变化，请重新拖放。");
                    string final=ReceiveBody(decrypted,context.Directory,name,expectedTotal,delegate(long count) { op.SetBytes(count); },op);
                    op.SetLocation(final);
                });
            }
            catch(Exception ex)
            {
                if(op==null) op=NewOperation(false,SafeDisplayName(headerName),Math.Max(0,expectedTotal),"接收失败");
                op.Fail(ex);
            }
            finally { if(op!=null)op.DetachSocket(socket);try { if(socket!=null) socket.Close(); } catch { } }
            return true;
        }

        internal static void WriteBody(Stream source,Stream encrypted,long length,Action<long> progress,Operation operation=null)
        {
            CopyExact(source,encrypted,length,progress,operation);
            if(operation!=null)operation.ThrowIfCancelled();
            byte[] padding=new byte[32-(int)(length%32)];
            encrypted.Write(padding,0,padding.Length); encrypted.Flush();
            if(operation!=null)operation.ThrowIfCancelled();
        }
        internal static void CopyExact(Stream source,Stream destination,long length,Action<long> progress,Operation operation=null)
        {
            if(length<0) throw new IOException("文件大小无效。");
            byte[] buffer=new byte[65536]; long count=0;
            while(count<length)
            {
                if(operation!=null)operation.ThrowIfCancelled();
                int n=source.Read(buffer,0,(int)Math.Min(buffer.Length,length-count));
                if(n<=0) throw new EndOfStreamException("传输中断，文件尚未完整接收。");
                if(operation!=null)operation.ThrowIfCancelled();
                destination.Write(buffer,0,n); count+=n; if(progress!=null) progress(count);
                if(operation!=null)operation.ThrowIfCancelled();
            }
        }
        internal static string ReceiveBody(Stream source,string directory,string name,long length,Action<long> progress,Operation operation=null)
        { return SaveBody(source,directory,name,length,progress,operation,true); }

        internal static string CopyLocalFile(string sourcePath,string directory,Action<long> progress,Operation operation=null)
        {
            string result=null;
            UserContext.Run(delegate {
                if(operation!=null)operation.ThrowIfCancelled();
                if(String.IsNullOrWhiteSpace(sourcePath)||!Path.IsPathRooted(sourcePath)||sourcePath.StartsWith(@"\\"))
                    throw new IOException("请选择本机的实际文件。");
                string name=SafeFileName(sourcePath);
                sourcePath=Path.GetFullPath(sourcePath);
                string sourceDirectory=ValidateDirectory(Path.GetDirectoryName(sourcePath));
                directory=ValidateDirectory(directory);
                if(new DriveInfo(Path.GetPathRoot(sourcePath)).DriveType==DriveType.Network ||
                    new DriveInfo(Path.GetPathRoot(directory)).DriveType==DriveType.Network)
                    throw new IOException("暂不支持网络驱动器，请选择本机文件和文件夹。");
                FileAttributes attributes=File.GetAttributes(sourcePath);
                if((attributes&(FileAttributes.Directory|FileAttributes.ReparsePoint|FileAttributes.Device))!=0)
                    throw new IOException("请选择本机的实际文件，暂不支持文件夹或链接。");
                using(FileStream source=new FileStream(sourcePath,FileMode.Open,FileAccess.Read,FileShare.Read,65536,FileOptions.SequentialScan))
                {
                    if(operation!=null) { operation.Begin(name,source.Length,directory);operation.SetStatus("正在复制",false,false); }
                    if(String.Equals(sourceDirectory.TrimEnd('\\','/'),directory.TrimEnd('\\','/'),StringComparison.OrdinalIgnoreCase))
                    {
                        if(operation!=null)operation.CompleteLocalNoOp(sourcePath);
                        result=sourcePath;return;
                    }
                    result=SaveBody(source,directory,name,source.Length,progress,operation,false);
                }
            });
            return result;
        }

        static string SaveBody(Stream source,string directory,string name,long length,Action<long> progress,Operation operation,bool wireFraming)
        {
            if(operation!=null)operation.ThrowIfCancelled();
            directory=ValidateDirectory(directory); name=SafeFileName(name);
            if(length<0) throw new IOException("文件大小无效。");
            string temporary=Path.Combine(directory,".mwb-"+Guid.NewGuid().ToString("N")+".partial");
            bool created=false;
            try
            {
                using(FileStream output=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None,65536,FileOptions.SequentialScan))
                {
                    created=true;
                    CopyExact(source,output,length,progress,operation);
                    if(operation!=null)operation.ThrowIfCancelled();
                    if(wireFraming)
                    {
                        int pad=32-(int)(length%32);
                        for(int i=0;i<pad;i++) if(source.ReadByte()!=0) throw new InvalidDataException("文件尾部校验失败，未保存不完整文件。");
                        if(source.ReadByte()!=-1) throw new InvalidDataException("实际数据超出声明大小，未保存文件。");
                    }
                    if(operation!=null)operation.ThrowIfCancelled();
                    output.Flush(true);
                    if(operation!=null)operation.ThrowIfCancelled();
                }
                ValidateDirectory(directory);
                string stem=Path.GetFileNameWithoutExtension(name),extension=Path.GetExtension(name);
                for(int i=0;i<10000;i++)
                {
                    if(operation!=null)operation.ThrowIfCancelled();
                    string final=Path.Combine(directory,i==0?name:stem+" ("+i+")"+extension);
                    if(File.Exists(final) || Directory.Exists(final)) continue;
                    try { if(operation==null)File.Move(temporary,final);else operation.CommitFile(temporary,final);return final; }
                    catch(IOException) { if(File.Exists(final)||Directory.Exists(final)) continue; throw; }
                }
                throw new IOException("同名文件过多，请换一个接收文件夹。");
            }
            catch
            {
                // User requested stop-only cancellation: leave this incomplete file for manual handling.
                if(created && operation!=null)operation.SetLocation(temporary);
                throw;
            }
        }
        internal static string SafeFileName(string remote)
        {
            if(String.IsNullOrWhiteSpace(remote)) throw new IOException("文件名为空。");
            // The legacy header carries a full source path. Strip directories, but never strip ADS colons.
            string name=remote.Substring(Math.Max(remote.LastIndexOf('\\'),remote.LastIndexOf('/'))+1);
            if(String.IsNullOrWhiteSpace(name)||name=="."||name==".."||name.EndsWith(".")||name.EndsWith(" ")||name.IndexOfAny(Path.GetInvalidFileNameChars())>=0)
                throw new IOException("发送方文件名不受支持。");
            string device=name.Split('.')[0].ToUpperInvariant();
            if(device=="CON"||device=="PRN"||device=="AUX"||device=="NUL"||device=="CONIN$"||device=="CONOUT$"||
                (device.Length==4&&(device.StartsWith("COM")||device.StartsWith("LPT"))&&"0123456789¹²³".IndexOf(device[3])>=0)) throw new IOException("文件名为系统保留名称。");
            return name;
        }
        internal static string ValidateDirectory(string value)
        {
            if(String.IsNullOrWhiteSpace(value)||!Path.IsPathRooted(value)||value.StartsWith(@"\\")||value.StartsWith(@"\\?\")||value.StartsWith(@"\\.\"))
                throw new IOException("请选择本机的实际文件夹。");
            string path=Path.GetFullPath(value);
            if(!Directory.Exists(path)) throw new DirectoryNotFoundException("接收文件夹不存在或无权访问。");
            DirectoryInfo probe=new DirectoryInfo(path);
            // Cloud placeholders (OneDrive) are accepted; junctions/symlinks are not (v9.0.0.7).
            while(probe!=null) { if(ReparsePolicy.Redirects(probe)) throw new IOException("暂不支持链接或重定向文件夹（如目录联接、符号链接），请选择实际文件夹。"); probe=probe.Parent; }
            return path;
        }
        static string SafeDisplayName(string path) { try { return Path.GetFileName(path)??"文件"; } catch { return "文件"; } }
        static Operation NewOperation(bool sending,string name,long total,string location)
        {
            Operation op=new Operation(sending,name,total,location); if(!TestMode) ProgressWindow.Add(op); return op;
        }
        static void Notice(string text) { Operation op=NewOperation(false,"文件传输",0,""); op.SetStatus(text,true,true); }

        internal sealed class Operation
        {
            internal readonly object Sync=new object();
            internal readonly bool Sending;
            internal string Name,Location,Status="正在传输";
            internal long Total,Bytes;
            internal bool Finished,Failed,Dismissed;
            internal bool CancelRequested;
            Socket transferSocket;
            internal Action CancelNative;
            internal Stopwatch Clock=Stopwatch.StartNew();
            internal Operation(bool sending,string name,long total,string location) { Sending=sending; Name=name; Total=total; Location=location; }
            internal void Begin(string name,long total,string location) { lock(Sync){if(Dismissed)return;Name=name;Total=total;Location=location;Bytes=0;Clock.Restart();Status="正在接收";} }
            internal void SetBytes(long bytes) { lock(Sync) if(!Dismissed)Bytes=bytes; }
            internal void SetLocation(string location) { lock(Sync) if(!Dismissed)Location=location; }
            internal void SetStatus(string status,bool finished,bool failed) { lock(Sync){if(!Dismissed)Status=status;Finished=finished;Failed=failed;if(finished)Clock.Stop();} }
            internal void ClearDisplay() { lock(Sync){Dismissed=true;Name=Location=Status=String.Empty;Total=Bytes=0;Clock.Reset();} }
            internal void BindSocket(Socket socket)
            {
                bool abort;
                lock(Sync){abort=CancelRequested;if(!abort)transferSocket=socket;}
                if(abort){AbortSocket(socket);ThrowIfCancelled();}
            }
            internal void DetachSocket(Socket socket) { lock(Sync) if(Object.ReferenceEquals(transferSocket,socket))transferSocket=null; }
            internal bool RequestCancel()
            {
                Socket socket;
                lock(Sync)
                {
                    if(Finished||CancelRequested)return false;
                    CancelRequested=true;socket=transferSocket;
                    if(!Dismissed)Status="正在停止本次传输…";
                }
                // Never hold the display/commit lock during socket disposal or pending-request work.
                AbortSocket(socket);
                if(CancelNative!=null) { CancelNative();SetStatus("已取消",true,false);return true; }
                if(CancelWaiting(this))SetStatus("已取消",true,false);
                return true;
            }
            internal void ThrowIfCancelled() { lock(Sync) if(CancelRequested)throw new OperationCanceledException("已取消本次传输。"); }
            internal void CompleteSend()
            {
                lock(Sync){ThrowIfCancelled();SetStatus("已发送 · 接收端正在完成保存",true,false);}
            }
            internal void CompleteLocalNoOp(string source)
            {
                lock(Sync){ThrowIfCancelled();SetLocation(source);SetStatus("文件仍在原文件夹中",true,false);}
            }
            internal void CommitFile(string temporary,string final)
            {
                lock(Sync)
                {
                    ThrowIfCancelled();
                    File.Move(temporary,final);
                    SetLocation(final);SetStatus("接收完成 · 已保存",true,false);
                }
            }
            static void AbortSocket(Socket socket)
            {
                if(socket==null)return;
                try { socket.LingerState=new LingerOption(true,0); } catch { }
                try { socket.Close(0); } catch { }
            }
            internal void Fail(Exception ex)
            {
                if(CancelRequested){SetStatus("已取消",true,false);return;}
                bool disconnected=false;
                for(Exception current=ex;current!=null;current=current.InnerException)
                    if(current is SocketException || current is EndOfStreamException)disconnected=true;
                SetStatus(disconnected?"传输已停止：对方取消或连接中断。":"未完成："+ex.Message,true,true);
            }
        }
    }

    internal static class UserContext
    {
        [DllImport("wtsapi32.dll",SetLastError=true)] static extern bool WTSQueryUserToken(uint sessionId,out IntPtr token);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
        internal static void Run(Action action)
        {
            using(WindowsIdentity identity=WindowsIdentity.GetCurrent())
            {
                if(!identity.IsSystem) { action(); return; }
                IntPtr token;
                if(!WTSQueryUserToken((uint)Process.GetCurrentProcess().SessionId,out token)) throw new IOException("无法取得当前登录用户身份，已停止文件操作。");
                try { using(WindowsImpersonationContext context=WindowsIdentity.Impersonate(token)) action(); }
                finally { CloseHandle(token); }
            }
        }
    }

    internal sealed class ProgressWindow : Form
    {
        static readonly object QueueGate=new object();
        static readonly Queue<Transfer.Operation> Pending=new Queue<Transfer.Operation>();
        static bool started;
        internal readonly Transfer.Operation Operation;
        readonly Label summary=new Label(),headline=new Label(),name=new Label(),location=new Label(),speedLabel=new Label(),amount=new Label(),note=new Label();
        readonly ProgressBar bar=new ProgressBar();
        readonly Button close=new Button(),cancelTransfer=new Button();
        readonly ToolTip tips=new ToolTip();
        Font bodyFont,headingFont,smallFont;
        long lastBytes;
        double lastSeconds,speed;
        int layoutDpi=96;
        bool layingOut;

        // Each task owns a disposable window. The context retains only currently open windows.
        internal static void Add(Transfer.Operation op)
        {
            lock(QueueGate)
            {
                Pending.Enqueue(op); if(started) return; started=true;
                Thread t=new Thread(delegate() {
                    IntPtr previous=EnterDpiContext();
                    try { using(ProgressContext context=new ProgressContext()) Application.Run(context); }
                    catch(Exception) { /* A panel failure must not stop shared input or retain transfer details. */ }
                    finally { RestoreDpiContext(previous); lock(QueueGate) started=false; }
                });
                t.Name="MWB transfer progress"; t.IsBackground=true; t.SetApartmentState(ApartmentState.STA); t.Start();
            }
        }
        internal sealed class ProgressContext : ApplicationContext
        {
            internal readonly List<ProgressWindow> Windows=new List<ProgressWindow>();
            readonly System.Windows.Forms.Timer timer=new System.Windows.Forms.Timer();
            internal ProgressContext()
            {
                timer.Interval=200;
                timer.Tick+=delegate { try { RefreshWindows(); } catch(Exception) { /* No transfer log or history. */ } };
                timer.Start();
            }
            internal ProgressWindow Open(Transfer.Operation op)
            {
                lock(op.Sync) if(op.Dismissed || (op.Finished&&!op.Failed)) { op.ClearDisplay();return null; }
                ProgressWindow window=new ProgressWindow(op);
                Windows.Add(window);
                window.FormClosed+=delegate { Windows.Remove(window); };
                if(!Transfer.TestMode) window.Show();
                return window;
            }
            internal void RefreshWindows()
            {
                Transfer.CheckPendingExpiry();
                Transfer.Operation[] fresh;
                lock(QueueGate) { fresh=Pending.ToArray();Pending.Clear(); }
                foreach(Transfer.Operation op in fresh) Open(op);
                foreach(ProgressWindow window in Windows.ToArray())
                { if(window.IsDisposed) Windows.Remove(window); else window.RefreshOperation(); }
            }
            protected override void Dispose(bool disposing)
            {
                if(disposing)
                {
                    timer.Stop();timer.Dispose();
                    foreach(ProgressWindow window in Windows.ToArray()) window.Dispose();
                    Windows.Clear();
                }
                base.Dispose(disposing);
            }
        }
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams { get { CreateParams p=base.CreateParams;p.ExStyle|=0x08000000;return p; } }
        internal ProgressWindow(Transfer.Operation op)
        {
            Operation=op;
            AutoScaleMode=AutoScaleMode.None;
            BackColor=SystemColors.Window; ForeColor=SystemColors.WindowText;
            Text="文件传输";ShowIcon=false;MaximizeBox=false;FormBorderStyle=FormBorderStyle.FixedSingle;
            StartPosition=FormStartPosition.Manual;
            foreach(Label label in new[]{summary,headline,name,location,speedLabel,amount,note})
            { label.AutoSize=false;label.AutoEllipsis=true;label.UseMnemonic=false;label.TextAlign=ContentAlignment.MiddleLeft;Controls.Add(label); }
            bar.Maximum=1000;bar.Style=ProgressBarStyle.Continuous;Controls.Add(bar);
            close.Text="关闭";close.UseVisualStyleBackColor=true;close.Click+=delegate { Close(); };Controls.Add(close);
            cancelTransfer.Text="取消传输";cancelTransfer.UseVisualStyleBackColor=true;
            cancelTransfer.Click+=delegate { Operation.RequestCancel();RefreshOperation(); };Controls.Add(cancelTransfer);
            AcceptButton=close;CancelButton=close;
            ApplyDpi(96);
            Rectangle area=Screen.FromPoint(Cursor.Position).WorkingArea;
            Location=new Point(area.Left+(area.Width-Width)/2,area.Top+(area.Height-Height)/2);
            RefreshOperation();
        }
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            int dpi=96;
            try { uint value=GetDpiForWindow(Handle);if(value>0)dpi=(int)value; }
            catch(EntryPointNotFoundException) { using(Graphics g=CreateGraphics())dpi=(int)Math.Round(g.DpiX); }
            ApplyDpi(dpi);
            Rectangle area=Screen.FromHandle(Handle).WorkingArea;
            Location=new Point(area.Left+(area.Width-Width)/2,area.Top+(area.Height-Height)/2);
        }
        int Px(int logical) { return (int)Math.Round(logical*layoutDpi/96.0); }
        void Place(Control control,int x,int y,int width,int height) { control.SetBounds(Px(x),Px(y),Px(width),Px(height)); }
        internal void ApplyDpi(int dpi)
        {
            dpi=Math.Max(96,Math.Min(768,dpi));
            // WinForms ignores assignment of equal Fonts. Keep their still-live instances on a repeated DPI.
            if(bodyFont!=null && layoutDpi==dpi)return;
            if(layingOut)return;
            layingOut=true;SuspendLayout();
            try
            {
                layoutDpi=dpi;
                Font oldBody=bodyFont,oldHeading=headingFont,oldSmall=smallFont;
                float scale=layoutDpi/96f;
                bodyFont=new Font("Microsoft YaHei UI",13f*scale,FontStyle.Regular,GraphicsUnit.Pixel);
                headingFont=new Font("Microsoft YaHei UI",23f*scale,FontStyle.Regular,GraphicsUnit.Pixel);
                smallFont=new Font("Microsoft YaHei UI",12f*scale,FontStyle.Regular,GraphicsUnit.Pixel);
                Font=bodyFont;headline.Font=headingFont;note.Font=smallFont;
                note.ForeColor=SystemInformation.HighContrast?SystemColors.WindowText:Color.FromArgb(102,102,102);
                ClientSize=new Size(Px(490),Px(278));
                Place(summary,24,14,442,23);Place(headline,24,41,442,36);
                Place(bar,26,88,438,13);
                Place(name,24,116,442,24);Place(location,24,143,442,24);
                Place(speedLabel,24,170,442,24);Place(amount,24,197,442,24);
                Place(note,24,241,230,24);Place(cancelTransfer,262,237,108,30);Place(close,382,237,84,30);
                if(oldBody!=null)oldBody.Dispose();if(oldHeading!=null)oldHeading.Dispose();if(oldSmall!=null)oldSmall.Dispose();
            }
            finally { ResumeLayout(false);layingOut=false; }
        }
        protected override void WndProc(ref Message message)
        {
            if(message.Msg==0x02E0 && !layingOut)
            {
                int dpi=(int)(message.WParam.ToInt64()&0xffff);
                RECT rectangle=(RECT)Marshal.PtrToStructure(message.LParam,typeof(RECT));
                // Suggested bounds describe the non-client frame, not the client content area.
                ApplyDpi(dpi);
                SetWindowPos(Handle,IntPtr.Zero,rectangle.Left,rectangle.Top,rectangle.Right-rectangle.Left,rectangle.Bottom-rectangle.Top,0x0014);
                // Old WinForms must not apply its own font or bounds scaling a second time.
                ClientSize=new Size(Px(490),Px(278));
                message.Result=IntPtr.Zero;return;
            }
            base.WndProc(ref message);
        }
        internal void RefreshOperation()
        {
            if(IsDisposed)return;
            lock(Operation.Sync) if((Operation.Dismissed || (Operation.Finished&&!Operation.Failed)) && IsHandleCreated) { Close();return; }
            lock(Operation.Sync)
            {
                double seconds=Operation.Clock.Elapsed.TotalSeconds;
                if(seconds<lastSeconds) { lastSeconds=0;lastBytes=0;speed=0; }
                if(seconds-lastSeconds>=.45 && !Operation.Finished)
                { double now=(Operation.Bytes-lastBytes)/(seconds-lastSeconds);speed=lastSeconds==0?now:.55*speed+.45*now;lastBytes=Operation.Bytes;lastSeconds=seconds; }
                int value=Operation.Total>0?(int)Math.Min(1000,(double)Operation.Bytes/Operation.Total*1000):Operation.Finished&&!Operation.Failed?1000:0;
                bool waiting=!Operation.Finished && Operation.Status.StartsWith("等待",StringComparison.Ordinal);
                string percent=(value/10.0).ToString("0.0")+"%";
                summary.Text=Operation.Failed?Operation.Status:waiting?Operation.Status:(Operation.Sending?"正在发送 1 个文件":"正在接收 1 个文件");
                headline.Text=Operation.Failed?"传输未完成":Operation.Finished?(Operation.Sending?"已发送":"接收完成"):waiting?"等待文件…":"已完成 "+percent;
                if(Operation.CancelRequested&&!Operation.Finished){summary.Text="正在停止本次传输…";headline.Text="正在取消";}
                headline.ForeColor=Operation.Failed&&!SystemInformation.HighContrast?Color.FromArgb(176,38,29):SystemColors.WindowText;
                bar.Style=waiting?ProgressBarStyle.Marquee:ProgressBarStyle.Continuous;bar.Value=Math.Max(0,value);
                name.Text="名称："+Operation.Name;
                location.Text=(Operation.Sending?"目标：":"位置：")+Operation.Location;
                double rate=Operation.Finished?Operation.Bytes/Math.Max(.001,seconds):speed;
                speedLabel.Text=(Operation.Finished?"平均速度：":"速度：")+(waiting?"—":SizeText((long)Math.Max(0,rate))+"/s");
                amount.Text="已传："+SizeText(Operation.Bytes)+" / "+SizeText(Operation.Total);
                note.Text=Operation.Failed?"请重新拖放文件以重试。":Operation.Finished?"传输窗口即将关闭。":"关闭窗口后，传输仍会继续。";
                cancelTransfer.Enabled=!Operation.Finished&&!Operation.CancelRequested;
                cancelTransfer.Text=Operation.CancelRequested&&!Operation.Finished?"正在取消…":"取消传输";
                if(Operation.Finished&&!Operation.Failed)
                {
                    summary.Text=Operation.Sending?"已发出，接收端仍需完成保存":"文件已保存到目标文件夹";
                }
                Text=Operation.Failed?"文件传输未完成":Operation.Finished?headline.Text+" — 文件传输":waiting?"等待文件":"文件传输 — "+percent;
                tips.SetToolTip(name,Operation.Name);tips.SetToolTip(location,Operation.Location);tips.SetToolTip(summary,Operation.Status);
            }
        }
        protected override void Dispose(bool disposing)
        {
            if(disposing && !IsDisposed)
            {
                Operation.ClearDisplay();
                foreach(Control control in Controls)control.Text=String.Empty;
                Text=String.Empty;tips.RemoveAll();tips.Dispose();
            }
            base.Dispose(disposing);
            if(disposing) { if(bodyFont!=null)bodyFont.Dispose();if(headingFont!=null)headingFont.Dispose();if(smallFont!=null)smallFont.Dispose(); }
        }
        static string SizeText(long n) { if(n>=1073741824)return(n/1073741824.0).ToString("0.00")+" GB";if(n>=1048576)return(n/1048576.0).ToString("0.0")+" MB";if(n>=1024)return(n/1024.0).ToString("0.0")+" KB";return n+" B"; }
        internal static IntPtr EnterDpiContext()
        {
            try { IntPtr previous=SetThreadDpiAwarenessContext(new IntPtr(-4));return previous!=IntPtr.Zero?previous:SetThreadDpiAwarenessContext(new IntPtr(-3)); }
            catch(EntryPointNotFoundException) { return IntPtr.Zero; }
        }
        internal static void RestoreDpiContext(IntPtr previous) { if(previous!=IntPtr.Zero)try { SetThreadDpiAwarenessContext(previous); }catch(EntryPointNotFoundException){} }
        [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left,Top,Right,Bottom; }
        [DllImport("user32.dll")] static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
        [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr window);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr window,IntPtr after,int x,int y,int width,int height,uint flags);
    }
}
