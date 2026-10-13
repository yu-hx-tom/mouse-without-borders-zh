using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Principal;

namespace MwbTransfer
{
    public static partial class Transfer
    {
        internal static void TestReceiveRequest(Guid id,string directory)
        {
            TestMode=true;
            NativeRequest entry=new NativeRequest {Id=id,Peer="source",Path=directory,Sid=WindowsIdentity.GetCurrent().User.Value,Sending=false,Expires=DateTime.UtcNow.AddMinutes(1),Operation=NewOperation(false,"test",0,directory)};
            entry.Operation.CancelNative=delegate{ForgetNativeRequest(entry);};
            lock(NativeGate)NativeRequests.Add(id,entry);
        }
        internal static int TestNativeCount { get {lock(NativeGate)return NativeRequests.Count;} }
        internal static void TestCancel(Guid id){NativeRequests[id].Operation.RequestCancel();}
        internal static bool TestFailed;
        internal static void TestServe(Socket socket,Stream stream,string peer)
        {
            NativeRequest entry=NativeSockets[socket].Request;
            ServeNativeSocket(socket,stream,stream,peer);
            TestFailed=entry.Operation!=null&&entry.Operation.Failed;
        }
    }
}
internal static class NativeFileRequestTests
{
    static int checks;
    static void Check(bool condition,string message){if(!condition)throw new Exception(message);checks++;}
    static byte[] Read(Stream stream,int size){byte[] data=new byte[size];int n=0;while(n<size){int read=stream.Read(data,n,size-n);if(read==0)throw new EndOfStreamException();n+=read;}return data;}
    static byte[] Hello(int type){byte[] b=new byte[64];Array.Copy(BitConverter.GetBytes(type),b,4);return b;}
    static void Receive(string[] args)
    {
        Guid id=new Guid(args[2]);string folder=args[3];MwbTransfer.Transfer.TestReceiveRequest(id,folder);
        using(TcpClient client=new TcpClient("127.0.0.1",Int32.Parse(args[1])))
        {
            client.Client.ReceiveTimeout=client.Client.SendTimeout=5000;
            bool push=args.Length>4&&args[4]=="push";
            if(!push)Check(MwbTransfer.Transfer.BindNativeOutbound(client.Client,id,"source",false),"target socket bound");
            NetworkStream stream=client.GetStream();byte[] hello=MwbTransfer.Transfer.StampNativeHandshake(Hello(69),client.Client);
            stream.Write(hello,0,64);stream.Flush();
            Check(MwbTransfer.Transfer.AcceptNativeHandshake(client.Client,Read(stream,64),"source"),"target accepts supported listener hello");
            MwbTransfer.Transfer.TestServe(client.Client,stream,"source");
            Check(MwbTransfer.Transfer.TestNativeCount==0,"receive request cleared");
            if(MwbTransfer.Transfer.TestFailed)File.WriteAllText(Path.Combine(folder,"expected-error.marker"),"failed");
        }
    }
    static void SendCase(string root,int length,bool missing,bool push)
    {
        Guid id=Guid.NewGuid();string dir=Path.Combine(root,id.ToString("N"));Directory.CreateDirectory(dir);
        string target=Path.Combine(dir,"target");Directory.CreateDirectory(target);
        string source=Path.Combine(dir,"合成文件.bin");byte[] bytes=new byte[length];new Random(123).NextBytes(bytes);
        if(!missing)File.WriteAllBytes(source,bytes);
        Check(MwbTransfer.Transfer.OfferNativeFile(id,"receiver",source),"immutable source offer");
        TcpListener listener=new TcpListener(IPAddress.Loopback,0);listener.Start();
        try
        {
            int port=((IPEndPoint)listener.LocalEndpoint).Port;
            ProcessStartInfo start=new ProcessStartInfo(Process.GetCurrentProcess().MainModule.FileName,"receive "+port+" "+id+" \""+target+"\""+(push?" push":""));
            start.UseShellExecute=false;start.CreateNoWindow=true;start.WindowStyle=ProcessWindowStyle.Hidden;start.RedirectStandardOutput=true;start.RedirectStandardError=true;
            using(Process child=Process.Start(start))
            using(TcpClient client=listener.AcceptTcpClient())
            {
                client.Client.ReceiveTimeout=client.Client.SendTimeout=5000;NetworkStream stream=client.GetStream();
                if(push)Check(MwbTransfer.Transfer.BindNativeOutbound(client.Client,id,"receiver",true),"source reverse push socket bound");
                byte[] hello=MwbTransfer.Transfer.StampNativeHandshake(Hello(79),client.Client);stream.Write(hello,0,64);stream.Flush();
                Check(MwbTransfer.Transfer.AcceptNativeHandshake(client.Client,Read(stream,64),"receiver"),"source matches incoming request with peer");
                MwbTransfer.Transfer.TestServe(client.Client,stream,"receiver");
                if(!child.WaitForExit(10000))throw new Exception("Loopback receive timed out");
                string result=child.StandardOutput.ReadToEnd()+child.StandardError.ReadToEnd();Check(child.ExitCode==0,"receiver child: "+result);
            }
        }
        finally{listener.Stop();}
        Check(MwbTransfer.Transfer.TestNativeCount==0,"source snapshot cleared");
        string received=Path.Combine(target,Path.GetFileName(source));
        if(missing){Check(!File.Exists(received),"missing source cannot create a success file");Check(Directory.GetFiles(target,"*.partial").Length==0,"source error creates no empty partial");Check(File.Exists(Path.Combine(target,"expected-error.marker")),"source failure has explicit status");}
        else{Check(File.Exists(source),"source retained");Check(File.Exists(received),"target committed");using(SHA256 hash=SHA256.Create())Check(Convert.ToBase64String(hash.ComputeHash(File.ReadAllBytes(received)))==Convert.ToBase64String(hash.ComputeHash(bytes)),"exact payload hash");}
    }
    static void BindingCases(string root)
    {
        Guid old=Guid.NewGuid(),fresh=Guid.NewGuid();
        MwbTransfer.Transfer.TestReceiveRequest(old,root);MwbTransfer.Transfer.TestCancel(old);
        MwbTransfer.Transfer.TestReceiveRequest(fresh,root);
        using(Socket socket=new Socket(AddressFamily.InterNetwork,SocketType.Stream,ProtocolType.Tcp))
        {
            Check(!MwbTransfer.Transfer.BindNativeOutbound(socket,old,"source",false),"cancelled ID cannot bind new pending");
            Check(!MwbTransfer.Transfer.BindNativeOutbound(socket,fresh,"wrong-peer",false),"wrong peer rejected");
            Check(!MwbTransfer.Transfer.BindNativeOutbound(socket,fresh,"source",true),"wrong direction rejected");
            Check(MwbTransfer.Transfer.BindNativeOutbound(socket,fresh,"source",false),"new ID available immediately after cancel");
            using(Socket duplicate=new Socket(AddressFamily.InterNetwork,SocketType.Stream,ProtocolType.Tcp))Check(!MwbTransfer.Transfer.BindNativeOutbound(duplicate,fresh,"source",false),"single-socket claim");
            Check(!MwbTransfer.Transfer.AcceptNativeHandshake(socket,Hello(79),"source"),"legacy hello cannot downgrade bound native request");
            MwbTransfer.Transfer.ReleaseNativeSocket(socket);
        }
        Check(MwbTransfer.Transfer.TestNativeCount==0,"failed handshake clears pending");
    }
    static int Main(string[] args)
    {
        try
        {
            MwbTransfer.Transfer.TestMode=true;
            if(args.Length>0&&args[0]=="receive"){Receive(args);return 0;}
            string root=Path.GetFullPath(args[0]);Directory.CreateDirectory(root);
            SendCase(root,0,false,false);SendCase(root,65537,false,false);SendCase(root,1048593,false,false);SendCase(root,0,true,false);
            SendCase(root,65537,false,true);SendCase(root,0,true,true);BindingCases(root);
            Console.WriteLine("PASS "+checks+" request-binding/plaintext loopback checks; real encrypted handshake and remote desktop remain separate.");return 0;
        }
        catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
}
