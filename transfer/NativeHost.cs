using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Reflection;
using System.Windows.Forms;

namespace MwbTransfer
{
    // The binary patch calls this versioned extension; reflect only fixed audited
    // members rather than importing the modified Microsoft assembly at compile time.
    internal static class NativeHost
    {
        static readonly Assembly Main=Assembly.GetEntryAssembly();
        static readonly Type Common=Main.GetType("MouseWithoutBorders.Common",true);
        static readonly Type IdType=Main.GetType("MouseWithoutBorders.ID",true);
        static readonly Type DataType=Main.GetType("MouseWithoutBorders.DATA",true);
        static readonly Type PostType=Main.GetType("MouseWithoutBorders.ClipboardPostAction",true);
        static readonly Dictionary<string,MethodInfo> Methods=FindMethods();
        static Dictionary<string,MethodInfo> FindMethods()
        {
            Dictionary<string,MethodInfo> result=new Dictionary<string,MethodInfo>();
            foreach(string name in new[]{"get_MachineID","get_DesMachineID","get_MainForm","set_MouseDown","NamefromID","IsConnectedTo","SkSend","ConnectToRemoteClipboardSocket","ShakeHand"})
                result.Add(name,Common.GetMethod(name,BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic));
            return result;
        }
        static object Call(string name,params object[] arguments){return Methods[name].Invoke(null,arguments);}
        internal static uint Local {get{return Convert.ToUInt32(Call("get_MachineID"));}}
        internal static uint Pointer {get{uint id=Convert.ToUInt32(Call("get_DesMachineID"));return id<=1?Local:id;}}
        internal static Form Form {get{return (Form)Call("get_MainForm");}}
        internal static void SetDown(bool down){Call("set_MouseDown",down);}
        internal static string Name(uint peer){return (string)Call("NamefromID",Enum.ToObject(IdType,peer));}
        internal static bool Connected(uint peer){return peer==Local||(bool)Call("IsConnectedTo",Enum.ToObject(IdType,peer));}
        internal static void Send(int type,uint peer,Guid id,uint argument)
        {
            byte[] bytes=NativeWire.Encode(type,Local,peer,id,argument);
            object packet=Activator.CreateInstance(DataType,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic,null,new object[]{bytes},null);
            Call("SkSend",packet,null,false);
        }
        internal static bool ConnectFile(string peer,Guid id,bool sending)
        {
            // Return false only before a socket is obtained: a reverse push is
            // safe only before an uncertain handshake/claim or file write.
            TcpClient client=null;
            try{client=(TcpClient)Call("ConnectToRemoteClipboardSocket",peer);}catch{return false;}
            if(client==null)return false;
            try
            {
                if(!Transfer.BindNativeOutbound(client.Client,id,peer,sending))throw new InvalidOperationException("文件请求已取消或已被领取。");
                object[] arguments={peer,client.Client,null,null,sending,Enum.ToObject(PostType,sending?4:3)};
                if(!(bool)Call("ShakeHand",arguments))throw new InvalidOperationException("另一台电脑未接受本次新版文件请求。");
                Transfer.ServeNativeSocket(client.Client,(System.IO.Stream)arguments[2],(System.IO.Stream)arguments[3],(string)arguments[0]);
            }
            catch(Exception){Transfer.FailNativeRequest(id,"本次文件连接未完成，请重新拖放。");}
            finally{Transfer.ReleaseNativeSocket(client.Client);client.Close();}
            return true;
        }
    }
}
