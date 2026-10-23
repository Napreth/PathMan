using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Pathman.Core.EnvironmentVariables;
using Pathman.Core.Models;

namespace Pathman.Gui.Views;

public partial class VariablesView
{
    private EnvironmentPathCleanupPlan? cleanupPlan;
    private sealed record CleanupChangeDisplay(string Title, string Before, string After);
    private sealed record CleanupWarningDisplay(PathCleanupWarning Warning)
    {
        public string Title => $"{(Warning.Scope == VariableScope.User ? "用户" : "系统")} · {Warning.Name}";
        public string Message => Warning.Message;
    }
    private async void PreviewCleanup(object sender, RoutedEventArgs e)
    {
        if (busy || !await ConfirmLeaveAsync()) return;
        await ReloadAsync();
        cleanupPlan=EnvironmentPathCleanup.Analyze(allVariables, RuntimeValues());
        ShowCleanupPlan(); CleanupStatus.Text="";
        EditorPanel.Visibility=Visibility.Collapsed; CleanupPanel.Visibility=Visibility.Visible;
    }
    private void ShowCleanupPlan()
    {
        if (cleanupPlan is null) return;
        CleanupChanges.ItemsSource=cleanupPlan.Scopes.SelectMany(scope=>scope.Mutations.Select(change=>new CleanupChangeDisplay(
            $"{(scope.Scope==VariableScope.User ? "用户" : "系统")} · {change.Name}{(change.Action=="create" ? "（新建）" : "")}",
            change.Action=="create" ? "未定义" : (change.ExpectedValue ?? "").Replace(';','\n'), (change.Value ?? "").Replace(';','\n')))).ToArray();
        CleanupWarnings.ItemsSource=cleanupPlan.Warnings.Select(warning=>new CleanupWarningDisplay(warning)).ToArray();
        CleanupWarningsSurface.Visibility=cleanupPlan.Warnings.Count>0 ? Visibility.Visible : Visibility.Collapsed;
        CleanupEmpty.Visibility=cleanupPlan.HasChanges ? Visibility.Collapsed : Visibility.Visible;
        ApplyCleanupButton.IsEnabled=cleanupPlan.HasChanges;
    }
    private void CloseCleanup(object sender, RoutedEventArgs e)
    { if (!busy) { CleanupPanel.Visibility=Visibility.Collapsed; EditorPanel.Visibility=Visibility.Visible; } }
    private async void OpenCleanupWarning(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: CleanupWarningDisplay display }) await OpenVariable(display.Warning.Scope, display.Warning.Name);
    }
    private async void ApplyCleanup(object sender, RoutedEventArgs e)
    {
        if (busy || cleanupPlan?.HasChanges != true) return;
        var plan=cleanupPlan;
        busy=true; RefreshButton.IsEnabled=CleanupButton.IsEnabled=ApplyCleanupButton.IsEnabled=false;
        var applied=new List<string>();
        string status;
        try
        {
            var backups=Path.Combine(layout.DataDirectory,"backups"); Directory.CreateDirectory(backups);
            var backup=Path.Combine(backups,$"path-cleanup-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.json");
            await File.WriteAllTextAsync(backup,JsonSerializer.Serialize(plan,EnvironmentWorkspaceStore.JsonOptions));
            CleanupStatus.ToolTip=backup;
            foreach(var scope in plan.Scopes.Where(scope=>scope.Mutations.Count>0))
            {
                await EnvironmentEditHost.ApplyBatchAsync(scope.Mutations,scope.Preconditions,store,layout.DataDirectory);
                applied.Add(scope.Scope==VariableScope.Machine ? "系统 Path" : "用户 Path");
            }
            status="清理完成。原始值已备份。";
        }
        catch(Exception error) when(Expected(error) || error is OperationCanceledException)
        { status=(applied.Count>0 ? $"已清理{string.Join("、",applied)}；其余未应用。" : "未应用清理。") + error.Message; }
        finally { busy=false; RefreshButton.IsEnabled=CleanupButton.IsEnabled=true; }
        await ReloadAsync();
        cleanupPlan=EnvironmentPathCleanup.Analyze(allVariables,RuntimeValues()); ShowCleanupPlan(); CleanupStatus.Text=status;
    }
}
