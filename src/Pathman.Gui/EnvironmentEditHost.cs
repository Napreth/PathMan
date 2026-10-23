using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text.Json;
using Pathman.Core.EnvironmentVariables;
using Pathman.Core.Models;

namespace Pathman.Gui;

internal sealed record ElevatedEnvironmentRequest(EnvironmentMutation[] Mutations, VariableSnapshot[] Preconditions, string DataDirectory);
internal sealed record ElevatedEnvironmentResult(string? Error);
internal static class EnvironmentEditHost
{
    public static Task ApplyAsync(EnvironmentMutation mutation, EnvironmentWorkspaceStore store, string dataDirectory) =>
        ApplyBatchAsync([mutation], [], store, dataDirectory);
    public static async Task ApplyBatchAsync(IReadOnlyList<EnvironmentMutation> mutations, IReadOnlyList<VariableSnapshot> preconditions,
        EnvironmentWorkspaceStore store, string dataDirectory)
    {
        if (mutations.Count == 0) return;
        var scope = mutations[0].Scope;
        if (mutations.Any(item => item.Scope != scope)) throw new ArgumentException("一次保存只支持一种作用域。");
        using var identity = WindowsIdentity.GetCurrent();
        var administrator = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        if (scope != VariableScope.Machine || administrator)
        { await Task.Run(() => Apply(mutations, preconditions, store)); return; }
        var requests = Path.Combine(dataDirectory, "requests"); Directory.CreateDirectory(requests);
        var request = Path.Combine(requests, Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(request, JsonSerializer.Serialize(new ElevatedEnvironmentRequest(mutations.ToArray(), preconditions.ToArray(), dataDirectory), EnvironmentWorkspaceStore.JsonOptions));
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
            if (request.Mutations.Length == 0 || request.Mutations.Any(item => item.Scope != VariableScope.Machine) || request.Preconditions.Any(item => item.Scope != VariableScope.Machine))
                throw new InvalidDataException("管理员操作仅支持系统变量。");
            Apply(request.Mutations, request.Preconditions, new EnvironmentWorkspaceStore(request.DataDirectory));
        }
        catch (Exception exception) { error = exception.Message; }
        File.WriteAllText(file + ".result", JsonSerializer.Serialize(new ElevatedEnvironmentResult(error), EnvironmentWorkspaceStore.JsonOptions));
        return error is null ? 0 : 1;
    }
    private static void Apply(IReadOnlyList<EnvironmentMutation> mutations, IReadOnlyList<VariableSnapshot> preconditions, EnvironmentWorkspaceStore store)
    {
        if (mutations.Count == 1 && preconditions.Count == 0) WindowsEnvironmentVariableEditor.Apply(mutations[0], store);
        else WindowsEnvironmentVariableEditor.ApplyBatch(mutations, preconditions, store);
    }
}
