using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Pathman.Core.EnvironmentVariables;
using Pathman.Core.Models;

namespace Pathman.Gui.Views;

public partial class VariablesView
{
    private EnvironmentPathCleanupPlan? cleanupPlan;
    private sealed record CleanupChangeDisplay(string Title,IReadOnlyList<DiffDisplayLine> Lines);
    private sealed record DiffCell(ValueDiffLine? Line,bool IsBefore)
    {
        public string Text=>Line?.Text ?? "";
        public string Prefix=>Line?.Prefix ?? "";
        public int? Number=>IsBefore ? Line?.OldLine : Line?.NewLine;
        public bool IsAdded=>Line?.Kind==ValueDiffKind.Added;
        public bool IsRemoved=>Line?.Kind==ValueDiffKind.Removed;
        public bool IsEmpty=>Line is null;
    }
    private sealed record DiffDisplayLine(DiffCell Before,DiffCell After,string? Hunk=null)
    {public bool IsHunk=>Hunk is not null; public string Title=>Hunk ?? $"{Before.Text} → {After.Text}";}
    private static IReadOnlyList<DiffDisplayLine> DiffLines(string? before,string after)
    {
        var full=EnvironmentValueDiff.Create(before,after);
        var visible=new bool[full.Count];
        for(var i=0;i<full.Count;i++) if(full[i].Kind!=ValueDiffKind.Context)
            for(var j=Math.Max(0,i-3);j<=Math.Min(full.Count-1,i+3);j++) visible[j]=true;
        var result=new List<DiffDisplayLine>();
        for(var index=0;index<full.Count;)
        {
            if(!visible[index]) {index++;continue;}
            var end=index; while(end<full.Count && visible[end]) end++;
            var hunk=full.Skip(index).Take(end-index).ToArray();
            var oldLines=hunk.Where(line=>line.OldLine is not null).ToArray(); var newLines=hunk.Where(line=>line.NewLine is not null).ToArray();
            result.Add(new(new(null,true),new(null,false),$"@@ -{oldLines.FirstOrDefault()?.OldLine ?? 0},{oldLines.Length} +{newLines.FirstOrDefault()?.NewLine ?? 0},{newLines.Length} @@"));
            result.AddRange(EnvironmentValueDiff.Pair(hunk).Select(pair=>new DiffDisplayLine(new(pair.Before,true),new(pair.After,false))));
            index=end;
        }
        return result;
    }
    private async void PreviewCleanup(object sender, RoutedEventArgs e)
    {
        if (busy || !await ConfirmLeaveAsync()) return;
        StatusText.Text="";
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
            DiffLines(change.Action=="create" ? null : change.ExpectedValue,change.Value ?? "")))).ToArray();
        CleanupEmpty.Visibility=cleanupPlan.HasChanges ? Visibility.Collapsed : Visibility.Visible;
        ApplyCleanupButton.IsEnabled=cleanupPlan.HasChanges;
    }
    private void CloseCleanup(object sender, RoutedEventArgs e)
    { if (!busy) { CleanupPanel.Visibility=Visibility.Collapsed; EditorPanel.Visibility=Visibility.Visible; } }
    private void ScrollCleanup(object sender,MouseWheelEventArgs e)
    {
        if(CleanupScroll.ScrollableHeight<=0) return;
        CleanupScroll.ScrollToVerticalOffset(CleanupScroll.VerticalOffset-e.Delta*.5); e.Handled=true;
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
