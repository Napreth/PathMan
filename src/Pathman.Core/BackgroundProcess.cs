using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Pathman.Core.EnvironmentVariables;

namespace Pathman.Core;

public sealed class BackgroundProcess(PathmanLayout layout)
{
    // Per-user, per-installation names prevent another installation from stopping this process.
    public string InstanceName
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return @"Local\PathMan." + identity.User!.Value + "." +
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(layout.InstallationDirectory.ToUpperInvariant())))[..16];
        }
    }
    public string StopEventName => InstanceName + ".Stop";
    public string ReadyEventName => InstanceName + ".Ready";

    public bool IsRunning
    {
        get
        {
            if (!EventWaitHandle.TryOpenExisting(ReadyEventName, out var ready)) return false;
            using (ready) return ready.WaitOne(0);
        }
    }

    public void Start()
    {
        if (IsRunning) return;
        var state=new EnvironmentWorkspaceStore(layout.DataDirectory).Read();
        if(!StartupManager.CanRun(state,new StartupManager(layout).IsEnabled))
            throw new IOException("后台运行与登录自启必须同时开启，后台程序才会运行。");
        if (!File.Exists(layout.BackgroundExecutable))
            throw new FileNotFoundException("后台程序不存在。", layout.BackgroundExecutable);
        using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, ReadyEventName);
        // Shell activation detaches the background process from redirected CLI pipes.
        // Otherwise `pathman background start | ...` can wait until the background exits.
        var start = new ProcessStartInfo(layout.BackgroundExecutable)
        {
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        start.ArgumentList.Add("--data-root");
        start.ArgumentList.Add(layout.DataDirectory);
        using var process = Process.Start(start);
        if(process is null) throw new IOException("无法启动后台程序。");
        for(var retry=0;retry<100;retry++)
        {if(ready.WaitOne(100)) return;if(process.HasExited) throw new IOException("后台程序拒绝启动，请检查后台运行和登录自启设置。");}
        throw new IOException("后台程序未能在 10 秒内启动。");
    }

    public void Stop()
    {
        if (!EventWaitHandle.TryOpenExisting(StopEventName, out var stop)) return;
        using (stop) stop.Set();
        for (var retry = 0; retry < 100 && IsRunning; retry++) Thread.Sleep(50);
        if (IsRunning) throw new IOException("后台程序未能在 5 秒内停止。");
    }
}
