using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
internal static class VerifyNativeCandidate
{
    [DllImport("mscoree.dll",CharSet=CharSet.Unicode)]
    [return:MarshalAs(UnmanagedType.Bool)] static extern bool StrongNameSignatureVerificationEx(string file,[MarshalAs(UnmanagedType.Bool)] bool force,[MarshalAs(UnmanagedType.Bool)] out bool verified);
    static int Main(string[] args)
    {
        try
        {
            string folder=Path.GetFullPath(args[0]);
            AppDomain.CurrentDomain.AssemblyResolve+=delegate(object sender,ResolveEventArgs e){string f=Path.Combine(folder,new AssemblyName(e.Name).Name+".dll");return File.Exists(f)?Assembly.LoadFrom(f):null;};
            foreach(string name in new[]{"MwbNativeDrag.dll","MwbTransfer.dll"}){bool verified;if(!StrongNameSignatureVerificationEx(Path.Combine(folder,name),true,out verified)||!verified)throw new Exception("Invalid signature "+name);}
            Assembly main=Assembly.LoadFrom(Path.Combine(folder,"MouseWithoutBorders.exe"));int count=0;
            foreach(string line in File.ReadAllLines(Path.Combine(folder,"jit-methods.txt")))
            {
                string[] p=line.Split('|');MethodInfo method=main.GetType(p[0].Replace('/','+'),true).GetMethod(p[1],BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic);
                RuntimeHelpers.PrepareMethod(method.MethodHandle);count++;
            }
            Type common=main.GetType("MouseWithoutBorders.Common",true);
            foreach(string name in new[]{"get_MachineID","get_DesMachineID","get_MainForm","set_MouseDown","NamefromID","IsConnectedTo","SkSend","ConnectToRemoteClipboardSocket","ShakeHand"})
                if(common.GetMethod(name,BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic)==null)throw new Exception("Missing reflection API: "+name);
            Type connection=main.GetType("MouseWithoutBorders.TcpSk",true);
            foreach(string name in new[]{"MachineId","BackingSocket"})if(connection.GetProperty(name)==null)throw new Exception("Missing connection API "+name);
            Assembly helper=Assembly.LoadFrom(Path.Combine(folder,"MouseWithoutBordersHelper.exe"));
            Type form=helper.GetType("MouseWithoutBorders.FormHelper",true);
            foreach(string name in new[]{"WndProc","FormHelper_DragEnter","timerHelper_Tick","FormHelper_Shown","FormHelper_LocationChanged"})RuntimeHelpers.PrepareMethod(form.GetMethod(name,BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public).MethodHandle);
            Console.WriteLine("PASS "+count+" main methods + 5 helper methods JIT; reflection APIs and extension signatures. No production app launched or input injected.");return 0;
        }
        catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
}
