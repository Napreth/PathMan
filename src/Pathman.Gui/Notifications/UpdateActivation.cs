using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;
using Pathman.Core;

namespace Pathman.Gui.Notifications;

internal sealed class UpdateActivation : IDisposable
{
#if PATHMAN_UPDATE_TEST
    public const string Protocol="pathman-update-test";
#else
    public const string Protocol="pathman-update";
#endif
    private readonly CancellationTokenSource stopping=new();
    private readonly Mutex owner;
    private readonly bool owns;
    public static string Link(string tag)=>$"{Protocol}://release/{Uri.EscapeDataString(tag)}";
    public static bool IsRequested(string[] args)=>args.Length>=2 && args[0]=="--show-update" &&
        Uri.TryCreate(args[1],UriKind.Absolute,out var uri) && uri.Scheme==Protocol && uri.Host=="release";
    private static string Name(PathmanLayout layout)=>"PathMan.Update."+Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes((layout.InstallationDirectory+"|"+layout.DataDirectory).ToUpperInvariant())))[..24];
    public static void RegisterProtocol(PathmanLayout layout)
    {
        var executable=Path.Combine(layout.InstallationDirectory,"PathMan.exe");
        using var key=Registry.CurrentUser.CreateSubKey(@"Software\Classes\"+Protocol);
        key.SetValue("","URL:PathMan update");key.SetValue("URL Protocol","");key.SetValue("ExecutablePath",executable);
        using var command=key.CreateSubKey(@"shell\open\command");command.SetValue("",$"\"{executable}\" --show-update \"%1\"");
    }
    public static bool TryForward(PathmanLayout layout)
    {
        try
        {
            using var pipe=new NamedPipeClientStream(".",Name(layout),PipeDirection.Out,PipeOptions.CurrentUserOnly);
            pipe.Connect(350);AllowSetForegroundWindow(uint.MaxValue);pipe.WriteByte(1);return true;
        }
        catch(Exception error) when(error is IOException or TimeoutException or UnauthorizedAccessException) {return false;}
    }
    public UpdateActivation(PathmanLayout layout,Action open)
    {
        owner=new(false,@"Local\"+Name(layout));
        try {owns=owner.WaitOne(0);}catch(AbandonedMutexException) {owns=true;}
        if(owns) _=Task.Run(async()=>
        {
            try
            {
                while(!stopping.IsCancellationRequested)
                {
                    await using var pipe=new NamedPipeServerStream(Name(layout),PipeDirection.In,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
                    await pipe.WaitForConnectionAsync(stopping.Token);
                    var message=new byte[1];if(await pipe.ReadAsync(message,stopping.Token)==1 && message[0]==1) open();
                }
            }
            catch(Exception error) when(error is IOException or OperationCanceledException or ObjectDisposedException) {}
        });
    }
    public void Dispose() {stopping.Cancel();if(owns) owner.ReleaseMutex();owner.Dispose();}
    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(uint processId);
}
