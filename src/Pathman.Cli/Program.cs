using Pathman.Core;
using Pathman.Core.Shims;

Console.OutputEncoding = System.Text.Encoding.UTF8;
try
{
    string? dataDirectory = null;
    if (args.Length >= 2 && args[0] == "--data-root")
    {
        dataDirectory = args[1];
        args = args[2..];
    }
    var layout = PathmanLayout.Discover(dataDirectory);
    var shims = new ShimManager(layout);
    var startup = new StartupManager(layout);
    var background = new BackgroundProcess(layout);
    switch (args)
    {
        case ["layout"]:
            Console.WriteLine($"程序目录: {layout.InstallationDirectory}");
            Console.WriteLine($"bin:      {layout.BinDirectory}");
            Console.WriteLine($"internal: {layout.InternalDirectory}");
            Console.WriteLine($"shims:    {layout.ShimsDirectory}");
            return 0;
        case ["shim", "list"]:
            foreach (var alias in shims.List()) Console.WriteLine($"{alias.Name}\t{alias.Target}");
            return 0;
        case ["shim", "refresh"]:
            Console.WriteLine($"已重新链接 {shims.Refresh()} 个别名。");
            return 0;
        case ["shim", "add", var name, var target]:
            var created = shims.Add(name, target);
            Console.WriteLine($"已创建：{created.Name} → {created.Target}");
            return 0;
        case ["shim", "remove", var name]:
            shims.Remove(name);
            Console.WriteLine($"已删除：{name}");
            return 0;
        case ["startup", "status"]:
            Console.WriteLine(startup.IsEnabled ? "自启已启用" : "自启已禁用");
            return 0;
        case ["startup", "enable"]:
            startup.SetEnabled(true);
            Console.WriteLine("已启用当前用户登录时自启。");
            return 0;
        case ["startup", "disable"]:
            startup.SetEnabled(false);
            Console.WriteLine("已禁用当前安装的登录自启。");
            return 0;
        case ["background", "start"]:
            background.Start();
            Console.WriteLine("后台程序已启动。");
            return 0;
        case ["background", "stop"]:
            new Pathman.Core.EnvironmentVariables.EnvironmentMonitor(new Pathman.Core.EnvironmentVariables.EnvironmentWorkspaceStore(layout.DataDirectory)).SetEnabled(false);
            startup.SetEnabled(false);
            background.Stop();
            Console.WriteLine("后台程序已停止。");
            return 0;
        case ["background", "status"]:
            Console.WriteLine(background.IsRunning ? "后台程序正在运行" : "后台程序未运行");
            return 0;
        case [] or ["--help"] or ["-h"]:
            PrintHelp();
            return 0;
        default:
            PrintHelp();
            return 2;
    }
}
catch (Exception exception) when (exception is IOException or ArgumentException or
                                System.ComponentModel.Win32Exception or UnauthorizedAccessException)
{
    Console.Error.WriteLine($"PathMan: {exception.Message}");
    return 1;
}

static void PrintHelp() => Console.WriteLine("""
    PathMan — Windows 环境变量管理器

      pathman layout
      pathman shim list
      pathman shim refresh
      pathman shim add <别名> <目标.exe>
      pathman shim remove <别名>
      pathman startup <status|enable|disable>
      pathman background <status|start|stop>

    可在命令前指定 --data-root <目录>，用于隔离配置或测试。
    同卷直接创建硬链接；跨卷复制一份垫片后复用本地硬链接。
    """);
