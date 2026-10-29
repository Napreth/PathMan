using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

class ConsoleProbe
{
    [StructLayout(LayoutKind.Sequential)]
    struct Coord { public short X, Y; }

    [DllImport("kernel32.dll")] static extern uint GetConsoleCP();
    [DllImport("kernel32.dll")] static extern uint GetConsoleOutputCP();
    [DllImport("kernel32.dll")] static extern bool SetConsoleCP(uint codePage);
    [DllImport("kernel32.dll")] static extern bool SetConsoleOutputCP(uint codePage);
    [DllImport("kernel32.dll")] static extern IntPtr GetStdHandle(int handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern bool ReadConsoleOutputCharacter(IntPtr handle, StringBuilder text, uint length, Coord position, out uint read);

    static string ReadScreen()
    {
        var text = new StringBuilder(8000);
        uint read;
        if (!ReadConsoleOutputCharacter(GetStdHandle(-11), text, 8000, new Coord(), out read))
            throw new IOException("The console screen buffer could not be read.");
        return text.ToString();
    }

    static string CodePages() { return GetConsoleCP() + "/" + GetConsoleOutputCP(); }

    static int Main(string[] args)
    {
        uint inputCodePage = uint.Parse(args[2]);
        uint outputCodePage = uint.Parse(args[3]);
        if (!SetConsoleCP(inputCodePage) || !SetConsoleOutputCP(outputCodePage))
            throw new IOException("The probe requires its own console.");
        Console.Clear();
        const string marker = "PATHMAN-CONSOLE-HISTORY-MUST-SURVIVE";
        Console.WriteLine(marker);
        bool before = ReadScreen().Contains(marker);
        var changes = new List<string>();
        changes.Add(CodePages());
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("ComSpec"));
        start.UseShellExecute = false;
        start.Arguments = "/d /v:off /c \"\"" + args[0] + "\"\"";
        var process = Process.Start(start);
        try
        {
            var timer = Stopwatch.StartNew();
            while (!process.WaitForExit(1))
            {
                string current = CodePages();
                if (current != changes[changes.Count - 1]) changes.Add(current);
                if (timer.ElapsedMilliseconds > 15000)
                {
                    process.Kill();
                    throw new IOException("The shell switch timed out.");
                }
            }
            string final = CodePages();
            if (final != changes[changes.Count - 1]) changes.Add(final);
            bool history = before && ReadScreen().Contains(marker);
            string json = "{\"ExitCode\":" + process.ExitCode +
                ",\"HistoryPreserved\":" + history.ToString().ToLowerInvariant() +
                ",\"CodePages\":[\"" + string.Join("\",\"", changes.ToArray()) + "\"]}";
            File.WriteAllText(args[1], json, new UTF8Encoding(false));
            return process.ExitCode;
        }
        finally { process.Dispose(); }
    }
}
