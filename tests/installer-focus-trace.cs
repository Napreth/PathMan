using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

// Runs inside the disposable VM's interactive user session, including the
// secure-desktop transition. No foreground/window-order APIs are invoked.
public static class InstallerFocusTrace
{
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window,out uint process);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr window,StringBuilder text,int size);
    static volatile bool running;
    static Thread thread;
    public static void Start(string file)
    {
        running=true;
        thread=new Thread(()=>
        {
            using(var writer=new StreamWriter(file,false,Encoding.UTF8))
            {
                writer.AutoFlush=true;IntPtr last=new IntPtr(-1);
                while(running)
                {
                    var window=GetForegroundWindow();
                    if(window!=last)
                    {
                        uint process;var owner=GetWindowThreadProcessId(window,out process);
                        var text=new StringBuilder(1024);GetWindowText(window,text,text.Capacity);
                        writer.WriteLine("{0:O}\t{1}\t{2}\t{3}\t{4}",DateTime.UtcNow,window.ToInt64(),process,owner,text.ToString().Replace('\t',' '));last=window;
                    }
                    Thread.Sleep(2);
                }
            }
        });
        thread.IsBackground=true;thread.Start();
    }
    public static void Stop(){running=false;if(thread!=null)thread.Join();}
}
