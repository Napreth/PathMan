using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

internal static class ShimConsoleProbe
{
    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public uint Size; public IntPtr Reserved,Desktop,Title;
        public uint X,Y,XSize,YSize,XCount,YCount,Fill,Flags;
        public ushort Show,ReservedSize; public IntPtr ReservedData,Input,Output,Error;
    }
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] private static extern uint GetConsoleTitleW(StringBuilder text,uint size);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] private static extern bool SetConsoleTitleW(string title);
    [DllImport("kernel32.dll")] private static extern void GetStartupInfoW(out StartupInfo info);
    [DllImport("kernel32.dll")] private static extern uint GetConsoleProcessList(uint[] clients,uint size);
    private static string Title() {var text=new StringBuilder(65536);GetConsoleTitleW(text,(uint)text.Capacity);return text.ToString();}
    private static void Write(string prefix,string name,string value) {File.WriteAllText(prefix+"."+name,value,new UTF8Encoding(false));}
    private static void Wait(string path) {for(var i=0;i<1200;i++){if(File.Exists(path)) return;Thread.Sleep(100);}throw new TimeoutException(path);}
    public static int Main(string[] args)
    {
        if(args.Length<2) return 2;
        var prefix=args[1];Directory.CreateDirectory(Path.GetDirectoryName(prefix));
        try
        {
            if(args[0]=="observe")
            {
                StartupInfo startup;GetStartupInfoW(out startup);
                Write(prefix,"initial",Title());Write(prefix,"startup",Marshal.PtrToStringUni(startup.Title) ?? "");
                Write(prefix,"clients",GetConsoleProcessList(new uint[16],16).ToString());
                if(args.Length>2) SetConsoleTitleW(args[2]);
                Write(prefix,"ready",Title());Console.WriteLine("Target is running: "+Title());
                Wait(prefix+".release");return 37;
            }
            if(args[0]=="parent" && args.Length>2)
            {
                SetConsoleTitleW("PathMan caller title");Write(prefix,"before",Title());
                using(var child=Process.Start(new ProcessStartInfo(args[2],"observe \""+prefix+".child\"") {UseShellExecute=false}))
                {
                    Wait(prefix+".child.ready");Write(prefix,"during",Title());Write(prefix+".child","release","");
                    if(!child.WaitForExit(15000)) throw new TimeoutException("child exit");
                    Write(prefix,"exit",child.ExitCode.ToString());
                }
                Write(prefix,"ready",Title());Console.WriteLine("Caller restored: "+Title());
                Wait(prefix+".release");return 0;
            }
            return 2;
        }
        catch(Exception error) {Write(prefix,"error",error.ToString());return 1;}
    }
}
