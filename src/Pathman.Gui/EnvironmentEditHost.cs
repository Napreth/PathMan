using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text.Json;
using Pathman.Core.EnvironmentVariables;
using Pathman.Core.Models;

namespace Pathman.Gui;

internal sealed record ElevatedEnvironmentRequest(EnvironmentMutation Mutation, string DataDirectory);
internal sealed record ElevatedEnvironmentResult(string? Error);
internal static class EnvironmentEditHost
{
    public static async Task ApplyAsync(EnvironmentMutation mutation, EnvironmentWorkspaceStore store, string dataDirectory)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var administrator = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        if (mutation.Scope != VariableScope.Machine || administrator)
        { await Task.Run(() => WindowsEnvironmentVariableEditor.Apply(mutation, store)); return; }
        var requests = Path.Combine(dataDirectory, "requests"); Directory.CreateDirectory(requests);
        var request = Path.Combine(requests, Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(request, JsonSerializer.Serialize(new ElevatedEnvironmentRequest(mutation, dataDirectory), EnvironmentWorkspaceStore.JsonOptions));
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute=true, Verb="runas", WindowStyle=ProcessWindowStyle.Hidden };
            start.ArgumentList.Add("--apply-environment"); start.ArgumentList.Add(request);
            using var helper = Process.Start(start) ?? throw new IOException("无法启动管理员操作。");
            await helper.WaitForExitAsync();
            var resultPath = request + ".result";
            if (!File.Exists(resultPath)) throw new IOException("管理员操作未返回结果。");
            var result = JsonSerializer.Deserialize<ElevatedEnvironmentResult>(File.ReadAllText(resultPath), EnvironmentWorkspaceStore.JsonOptions)!;
            if (result.Error is not null) throw new IOException(result.Error);
        }
        catch (Win32Exception error) when (error.NativeErrorCode == 1223) { throw new OperationCanceledException("已取消管理员操作。"); }
        finally { File.Delete(request); File.Delete(request + ".result"); }
    }
    public static int Run(string file)
    {
        string? error = null;
        try
        {
            if (new FileInfo(file).Length > 1024*1024) throw new InvalidDataException("操作请求过大。");
            var request = JsonSerializer.Deserialize<ElevatedEnvironmentRequest>(File.ReadAllText(file), EnvironmentWorkspaceStore.JsonOptions)!;
            if (request.Mutation.Scope != VariableScope.Machine) throw new InvalidDataException("管理员操作仅支持系统变量。");
            WindowsEnvironmentVariableEditor.Apply(request.Mutation, new EnvironmentWorkspaceStore(request.DataDirectory));
        }
        catch (Exception exception) { error = exception.Message; }
        File.WriteAllText(file + ".result", JsonSerializer.Serialize(new ElevatedEnvironmentResult(error), EnvironmentWorkspaceStore.JsonOptions));
        return error is null ? 0 : 1;
    }
}
