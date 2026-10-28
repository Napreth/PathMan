using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

public static class InstallerWindowTrace
{
    delegate bool EnumProc(IntPtr window,IntPtr parameter);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc callback,IntPtr parameter);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr window,uint command);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] static extern bool IsWindowEnabled(IntPtr window);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window,out uint process);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr window,StringBuilder text,int count);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetClassName(IntPtr window,StringBuilder text,int count);
    static volatile bool running;
    static Thread thread;
    static long watched;
    public static void Watch(IntPtr window){Interlocked.Exchange(ref watched,window.ToInt64());}
    static string Title(IntPtr window){var text=new StringBuilder(512);GetWindowText(window,text,text.Capacity);return text.ToString().Replace('\t',' ');}
    static string Describe(IntPtr window)
    {
        uint process;GetWindowThreadProcessId(window,out process);
        return window.ToInt64()+"/"+process+"/"+IsWindowVisible(window)+"/"+IsWindowEnabled(window)+"/"+Title(window);
    }
    public static void Start(string file,string installer)
    {
        running=true;watched=0;
        thread=new Thread(()=>
        {
            using(var writer=new StreamWriter(file,false,Encoding.UTF8))
            {
                writer.AutoFlush=true;string previous=null;
                while(running)
                {
                    var windows=new StringBuilder();
                    EnumWindows((window,parameter)=>
                    {
                        var cls=new StringBuilder(128);GetClassName(window,cls,cls.Capacity);
                        if(cls.ToString()!="#32770")return true;
                        uint id;GetWindowThreadProcessId(window,out id);
                        try{using(var process=Process.GetProcessById((int)id))if(process.ProcessName.Equals(installer,StringComparison.OrdinalIgnoreCase)||process.ProcessName.Equals("uninstall-worker",StringComparison.OrdinalIgnoreCase))windows.Append(" | "+Describe(window));}
                        catch(ArgumentException){}catch(InvalidOperationException){}catch(System.ComponentModel.Win32Exception){}
                        return true;
                    },IntPtr.Zero);
                    var target=new IntPtr(Interlocked.Read(ref watched));var rank=0;
                    if(target!=IntPtr.Zero)for(var above=GetWindow(target,3);above!=IntPtr.Zero;above=GetWindow(above,3))if(IsWindowVisible(above)&&!IsIconic(above))rank++;
                    var state="foreground="+Describe(GetForegroundWindow())+"\twatch="+Describe(target)+"\trank="+rank+"\twindows="+windows;
                    if(state!=previous){writer.WriteLine(DateTime.UtcNow.ToString("O")+"\t"+state);previous=state;}
                    Thread.Sleep(10);
                }
            }
        });thread.IsBackground=true;thread.Start();
    }
    public static void Stop(){running=false;if(thread!=null)thread.Join();}
}
