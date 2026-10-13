using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace MwbTransfer
{
    public static partial class Transfer
    {
        const int NativeMagic=0x3942574d;
        static readonly object NativeGate=new object();
        internal sealed class NativeRequest
        {
            internal Guid Id;internal string Peer,Path,Sid;internal bool Sending,Claimed;
            internal Operation Operation;internal DateTime Expires;
        }
        sealed class NativeConnection { internal NativeRequest Request;internal bool Outbound,Authenticated; }
        static readonly Dictionary<Guid,NativeRequest> NativeRequests=new Dictionary<Guid,NativeRequest>();
        static readonly Dictionary<Socket,NativeConnection> NativeSockets=new Dictionary<Socket,NativeConnection>();
        static bool nativeChoosing;
        public static void TickNativeRequests(){lock(NativeGate)ExpireNativeRequests();}
        // v9.0.0.7: background-thread dialogs had no owner and could open behind
        // other windows. MB_TOPMOST|MB_SETFOREGROUND keeps the notice visible.
        public static void ShowNativeMessage(string title,string message)
        {
            Thread thread=new Thread(delegate(){UserContext.Run(delegate {MessageBoxW(IntPtr.Zero,message,title,0x40|0x40000|0x10000);});});
            thread.IsBackground=true;thread.SetApartmentState(ApartmentState.STA);thread.Start();
        }
        [System.Runtime.InteropServices.DllImport("user32.dll",CharSet=System.Runtime.InteropServices.CharSet.Unicode)]
        static extern int MessageBoxW(IntPtr owner,string text,string caption,uint type);

        // Folder check for the source-side capture, as the interactive user.
        public static bool IsNativeDirectory(string path)
        {
            bool folder=false;
            if(String.IsNullOrWhiteSpace(path))return false;
            UserContext.Run(delegate { folder=Directory.Exists(path); });
            return folder;
        }

        // A hidden topmost owner keeps the folder picker in front of other windows.
        static Form TopmostOwner()
        {
            Form owner=new Form{FormBorderStyle=FormBorderStyle.None,ShowInTaskbar=false,TopMost=true,StartPosition=FormStartPosition.Manual,
                Location=new System.Drawing.Point(-32000,-32000),Size=new System.Drawing.Size(1,1),Opacity=0};
            owner.Show();owner.Activate();return owner;
        }

        // A source snapshot is independent of normal clipboard contents. It may
        // be claimed once, by the destination authorized for this exact release.
        public static bool OfferNativeFile(Guid id,string peer,string path)
        {
            if(id==Guid.Empty||String.IsNullOrWhiteSpace(peer)||String.IsNullOrWhiteSpace(path))return false;
            string sid=null;
            UserContext.Run(delegate { sid=WindowsIdentity.GetCurrent().User.Value; });
            lock(NativeGate)
            {
                ExpireNativeRequests();
                if(NativeRequests.ContainsKey(id)||NativeRequests.Count>=16)return false;
                NativeRequests.Add(id,new NativeRequest {Id=id,Peer=peer,Path=path,Sid=sid,Sending=true,Expires=DateTime.UtcNow.AddMinutes(2)});
                return true;
            }
        }

        public static object CaptureNativeDrop(IntPtr overlay) { return DropTarget.CaptureSnapshot(overlay); }

        public static void BeginNativeDrop(Guid id,string peer,object captured,Action<Guid,string> request)
        {
            // v9.0.0.8: no EndDragPreview here. The released leg's preview ended at
            // release; a later leg may be showing its own preview right now.
            lock(NativeGate)
            {
                ExpireNativeRequests();
                if(id==Guid.Empty||NativeRequests.ContainsKey(id))return;
                // v9.0.0.7: these were silent drops.
                if(nativeChoosing){Notice("上一次的接收文件夹选择尚未完成，本次拖放未传输。请先完成或关闭那个选择窗口。");return;}
                if(NativeRequests.Count>=16){Notice("等待中的文件请求过多，本次拖放未传输。请稍后重试。");return;}
                nativeChoosing=true;
            }
            Thread worker=new Thread(delegate() {
                NativeRequest entry=null;
                try
                {
                    string target=null,sid=null;
                    UserContext.Run(delegate {
                        string reason;target=ResolveBounded(captured as DropTarget.Snapshot,out reason);
                        if(target!=null){try{target=ValidateDirectory(target);}catch(IOException ex){target=null;reason=ex.Message;}}
                        if(target==null)
                        {
                            using(Form owner=TopmostOwner())
                            using(FolderBrowserDialog picker=new FolderBrowserDialog())
                            {
                                picker.Description=(reason??"无法确认松开的位置。")+"\n请选择本次接收文件夹。";
                                if(picker.ShowDialog(owner)!=DialogResult.OK)return;
                                target=ValidateDirectory(picker.SelectedPath);
                            }
                        }
                        sid=WindowsIdentity.GetCurrent().User.Value;
                    });
                    if(target==null)return;
                    entry=new NativeRequest {Id=id,Peer=peer,Path=target,Sid=sid,Sending=false,Expires=DateTime.UtcNow.AddMinutes(2),Operation=NewOperation(false,"等待文件",0,target)};
                    NativeRequest fixedEntry=entry;
                    entry.Operation.CancelNative=delegate { ForgetNativeRequest(fixedEntry); };
                    lock(NativeGate) { if(NativeRequests.ContainsKey(id))throw new IOException("重复的文件请求。");NativeRequests.Add(id,entry); }
                    request(id,peer);
                }
                catch(Exception ex){if(entry!=null){entry.Operation.Fail(ex);ForgetNativeRequest(entry);}else Notice("无法开始接收："+ex.Message);}
                finally{lock(NativeGate)nativeChoosing=false;}
            });
            worker.IsBackground=true;worker.SetApartmentState(ApartmentState.STA);worker.Start();
        }

        static void ExpireNativeRequests()
        {
            List<NativeRequest> expired=new List<NativeRequest>();
            foreach(NativeRequest entry in NativeRequests.Values)if(!entry.Claimed&&DateTime.UtcNow>entry.Expires)expired.Add(entry);
            foreach(NativeRequest entry in expired)
            {
                NativeRequests.Remove(entry.Id);entry.Path=entry.Sid=null;
                if(entry.Operation!=null)entry.Operation.Fail(new IOException("等待文件连接超时，请重新拖放。"));
            }
        }
        static void ForgetNativeRequest(NativeRequest entry)
        {
            lock(NativeGate)
            {
                NativeRequest current;
                if(NativeRequests.TryGetValue(entry.Id,out current)&&Object.ReferenceEquals(current,entry))NativeRequests.Remove(entry.Id);
                // An active worker holds the snapshot until its socket finishes.
                if(!entry.Claimed)entry.Path=entry.Sid=null;
            }
        }

        public static bool BindNativeOutbound(Socket socket,Guid id,string peer,bool sending)
        {
            lock(NativeGate)
            {
                ExpireNativeRequests();NativeRequest entry;
                if(socket==null||NativeSockets.ContainsKey(socket)||!NativeRequests.TryGetValue(id,out entry)||entry.Claimed||entry.Sending!=sending||!String.Equals(entry.Peer,peer,StringComparison.OrdinalIgnoreCase))return false;
                entry.Claimed=true;NativeSockets.Add(socket,new NativeConnection {Request=entry,Outbound=true});return true;
            }
        }

        // Hook immediately after DATA.Bytes in the original encrypted handshake.
        public static byte[] StampNativeHandshake(byte[] original,Socket socket)
        {
            if(original==null||original.Length!=64)throw new IOException("握手长度错误。");
            byte[] header=(byte[])original.Clone();WriteInt(header,4,NativeMagic);WriteHandshakeId(header,Guid.Empty);
            lock(NativeGate)
            {
                NativeConnection connection;
                if(NativeSockets.TryGetValue(socket,out connection)&&connection.Outbound)
                {
                    WriteHandshakeId(header,connection.Request.Id);
                    WriteInt(header,0,connection.Request.Sending?79:69);
                    WriteInt(header,16,connection.Request.Sending?4:3);
                }
            }
            return header;
        }

        // Called only AFTER the original peer-name/ID/pairing checks succeeded.
        public static bool AcceptNativeHandshake(Socket socket,byte[] header,string authenticatedPeer)
        {
            if(header==null||header.Length!=64)return false;
            int magic=BitConverter.ToInt32(header,4),role=BitConverter.ToInt32(header,16),type=BitConverter.ToInt32(header,0);
            Guid id=ReadHandshakeId(header);
            if(role<0||role>4)return false;
            lock(NativeGate)
            {
                NativeConnection outbound;
                if(NativeSockets.TryGetValue(socket,out outbound))
                {
                    bool valid=outbound.Outbound&&magic==NativeMagic&&id==Guid.Empty&&role==0&&(type==69||type==79)&&String.Equals(authenticatedPeer,outbound.Request.Peer,StringComparison.OrdinalIgnoreCase);
                    outbound.Authenticated=valid;return valid;
                }
                if(role!=3&&role!=4)return id==Guid.Empty || magic!=NativeMagic;
                if(magic!=NativeMagic||id==Guid.Empty||(role==3?type!=69:type!=79))return false;
                ExpireNativeRequests();NativeRequest request;
                bool sending=role==3;
                if(!NativeRequests.TryGetValue(id,out request)||request.Claimed||request.Sending!=sending||!String.Equals(request.Peer,authenticatedPeer,StringComparison.OrdinalIgnoreCase))return false;
                request.Claimed=true;NativeSockets.Add(socket,new NativeConnection {Request=request,Authenticated=true});return true;
            }
        }

        public static bool ServeNativeSocket(Socket socket,Stream encrypted,Stream decrypted,string authenticatedPeer)
        {
            NativeConnection connection;
            lock(NativeGate) { if(!NativeSockets.TryGetValue(socket,out connection))return false; }
            NativeRequest entry=connection.Request;
            try
            {
                if(!connection.Authenticated||!String.Equals(entry.Peer,authenticatedPeer,StringComparison.OrdinalIgnoreCase))throw new IOException("文件连接身份校验失败。");
                if(entry.Sending)SendNative(entry,socket,encrypted,decrypted);else ReceiveNative(entry,socket,encrypted,decrypted);
            }
            catch(Exception ex){if(entry.Operation!=null)entry.Operation.Fail(ex);}
            finally { ReleaseNativeSocket(socket); }
            return true;
        }

        public static void ReleaseNativeSocket(Socket socket)
        {
            NativeConnection connection;
            lock(NativeGate)
            {
                if(NativeSockets.TryGetValue(socket,out connection))
                {
                    NativeSockets.Remove(socket);ForgetNativeRequest(connection.Request);
                    if(connection.Request.Operation!=null&&!connection.Request.Operation.Finished)
                        connection.Request.Operation.Fail(new IOException("本次文件连接未完成。"));
                    connection.Request.Path=connection.Request.Sid=null;
                }
            }
            try{socket.Close();}catch{}
        }
        public static void FailNativeRequest(Guid id,string reason)
        {
            lock(NativeGate)
            {
                NativeRequest entry;if(!NativeRequests.TryGetValue(id,out entry))return;
                if(entry.Operation!=null)entry.Operation.Fail(new IOException(reason));
                ForgetNativeRequest(entry);
            }
        }

        static void SendNative(NativeRequest entry,Socket socket,Stream encrypted,Stream decrypted)
        {
            bool headerSent=false;
            try
            {
                UserContext.Run(delegate {
                    if(WindowsIdentity.GetCurrent().User.Value!=entry.Sid)throw new IOException("登录用户已变化。");
                    string path=entry.Path;
                    ValidateDirectory(Path.GetDirectoryName(path));
                    if((File.GetAttributes(path)&FileAttributes.Directory)!=0)throw new IOException("暂不支持直接发送文件夹，请先压缩为单个文件。");
                    // OneDrive placeholders are ordinary files to the reader (opening hydrates them).
                    if(ReparsePolicy.Redirects(new FileInfo(path)))throw new IOException("暂不支持发送符号链接等链接文件，请选择实际文件。");
                    using(FileStream source=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))
                    {
                        string name=SafeFileName(Path.GetFileName(path));
                        entry.Operation=NewOperation(true,name,source.Length,"发送至另一台电脑");entry.Operation.BindSocket(socket);
                        byte[] header=NativeHeader(entry.Id,0,source.Length,name);
                        encrypted.Write(header,0,header.Length);headerSent=true;
                        WriteBody(source,encrypted,source.Length,delegate(long n){entry.Operation.SetBytes(n);},entry.Operation);
                        socket.Shutdown(SocketShutdown.Send);
                        byte[] ack=ReadNativeBlock(decrypted,32);
                        if(BitConverter.ToInt32(ack,0)!=NativeMagic||!SameId(ack,8,entry.Id)||BitConverter.ToInt32(ack,4)!=0)throw new IOException("接收端没有确认完整保存。");
                        entry.Operation.SetStatus("发送完成 · 对方已保存",true,false);
                    }
                });
            }
            catch(Exception ex)
            {
                if(!headerSent){try{byte[] error=NativeHeader(entry.Id,1,0,"");encrypted.Write(error,0,error.Length);encrypted.Flush();socket.Shutdown(SocketShutdown.Send);}catch{}}
                if(entry.Operation==null)entry.Operation=NewOperation(true,SafeDisplayName(entry.Path),0,"发送失败");
                entry.Operation.Fail(ex);
            }
            finally{if(entry.Operation!=null)entry.Operation.DetachSocket(socket);}
        }
        static void ReceiveNative(NativeRequest entry,Socket socket,Stream encrypted,Stream decrypted)
        {
            Operation op=entry.Operation;
            try
            {
                op.BindSocket(socket);op.ThrowIfCancelled();
                byte[] header=ReadNativeBlock(decrypted,1024);
                if(BitConverter.ToInt32(header,0)!=NativeMagic||!SameId(header,8,entry.Id))throw new IOException("文件请求编号不匹配。");
                if(BitConverter.ToInt32(header,4)!=0)throw new IOException("发送端无法读取本次源文件，请重新选择文件。");
                long length=BitConverter.ToInt64(header,24);int count=BitConverter.ToInt32(header,32);
                if(length<0||count<1||count>988)throw new IOException("文件头无效。");
                string name=SafeFileName(new UTF8Encoding(false,true).GetString(header,36,count));
                op.Begin(name,length,entry.Path);
                UserContext.Run(delegate {
                    if(WindowsIdentity.GetCurrent().User.Value!=entry.Sid)throw new IOException("登录用户已变化。");
                    ReceiveBody(decrypted,entry.Path,name,length,delegate(long n){op.SetBytes(n);},op);
                });
                byte[] ack=new byte[32];WriteInt(ack,0,NativeMagic);Array.Copy(entry.Id.ToByteArray(),0,ack,8,16);encrypted.Write(ack,0,ack.Length);encrypted.Flush();
            }
            finally{op.DetachSocket(socket);}
        }
        internal static byte[] NativeHeader(Guid id,int status,long length,string name)
        {
            byte[] text=new UTF8Encoding(false,true).GetBytes(name);
            if(id==Guid.Empty||length<0||text.Length>988)throw new IOException("文件头无效。");
            byte[] header=new byte[1024];WriteInt(header,0,NativeMagic);WriteInt(header,4,status);Array.Copy(id.ToByteArray(),0,header,8,16);
            Array.Copy(BitConverter.GetBytes(length),0,header,24,8);WriteInt(header,32,text.Length);Array.Copy(text,0,header,36,text.Length);return header;
        }
        static byte[] ReadNativeBlock(Stream stream,int length)
        {
            byte[] bytes=new byte[length];int offset=0;
            while(offset<length){int n=stream.Read(bytes,offset,length-offset);if(n<=0)throw new EndOfStreamException("文件连接已中断。");offset+=n;}return bytes;
        }
        static void WriteInt(byte[] bytes,int offset,int value){Array.Copy(BitConverter.GetBytes(value),0,bytes,offset,4);}
        static bool SameId(byte[] bytes,int offset,Guid id){byte[] value=id.ToByteArray();for(int i=0;i<16;i++)if(bytes[offset+i]!=value[i])return false;return true;}
        static Guid ReadHandshakeId(byte[] bytes){byte[] id=new byte[16];Array.Copy(bytes,12,id,0,4);Array.Copy(bytes,20,id,4,12);return new Guid(id);}
        static void WriteHandshakeId(byte[] bytes,Guid id){byte[] value=id.ToByteArray();Array.Copy(value,0,bytes,12,4);Array.Copy(value,4,bytes,20,12);}
    }
}
