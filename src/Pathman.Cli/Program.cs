using Pathman.Core;
using Pathman.Cli;

Console.OutputEncoding = System.Text.Encoding.UTF8;
try
{
    var layout = PathmanLayout.Discover();
    switch (args)
    {
        case ["list", .. var rest]:
            VersionCommands.List(layout,rest);
            return 0;
        case ["add", .. var rest]:
            VersionCommands.Add(layout,rest);
            return 0;
        case ["delete", var project]:
            VersionCommands.Delete(layout,project,null);
            return 0;
        case ["delete", var project,var version]:
            VersionCommands.Delete(layout,project,version);
            return 0;
        case ["switch", var project,var version]:
            VersionCommands.Switch(layout,project,version);
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
                                System.ComponentModel.Win32Exception or UnauthorizedAccessException or System.Security.SecurityException or OperationCanceledException or System.Text.Json.JsonException)
{
    Console.Error.WriteLine($"PathMan: {exception.Message}");
    return 1;
}

static void PrintHelp() => Console.WriteLine("""
    PathMan — 版本管理

      pathman list [项目]
      pathman add <项目> <版本> <值> [--variable <变量名>] [--type User|Machine] [--replace]
      pathman switch <项目> <版本>
      pathman delete <项目> [版本]
    """);
