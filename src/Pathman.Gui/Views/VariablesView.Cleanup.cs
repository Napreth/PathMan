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
            $"{(scope.Scope==VariableScope.User ? L.Get("用户") : L.Get("系统"))} · {change.Name}{(change.Action=="create" ? L.Get("（新建）") : "")}",
            DiffLines(change.Action=="create" ? null : change.ExpectedValue,change.Value ?? "")))).ToArray();
        CleanupEmpty.Visibility=cleanupPlan.HasChanges ? Visibility.Collapsed : Visibility.Visible;
        var reordered=cleanupPlan.Scopes.Where(scope=>scope.ChangesPathOrder).Select(scope=>scope.Scope==VariableScope.User ? L.Get("用户 PATH") : L.Get("系统 PATH")).ToArray();
        var unknown=cleanupPlan.Scopes.Any(scope=>scope.Mutations.Count>0 && !scope.PathOrderResolved);
        CleanupOrderWarning.Visibility=reordered.Length>0 || unknown ? Visibility.Visible : Visibility.Collapsed;
        CleanupOrderWarningText.Text=reordered.Length>0 ? L.Format("清理会改变{0}的路径搜索顺序，可能导致系统或程序调用到不同的命令或版本。请确认预览后再应用。",string.Join("、",reordered)) : L.Get("部分引用无法完整展开，无法确认清理后 PATH 的搜索顺序是否保持不变。请检查预览后再应用。");
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
        busy=true; CleanupButton.IsEnabled=ApplyCleanupButton.IsEnabled=false;
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
                applied.Add(scope.Scope==VariableScope.Machine ? L.Get("系统 Path") : L.Get("用户 Path"));
            }
            status=L.Get("清理完成。原始值已备份。");
        }
        catch(Exception error) when(Expected(error) || error is OperationCanceledException)
        { status=(applied.Count>0 ? L.Format("已清理{0}；其余未应用。",string.Join("、",applied)) : L.Get("未应用清理。")) + error.Message; }
        finally { busy=false; CleanupButton.IsEnabled=true; }
        await ReloadAsync();
        cleanupPlan=EnvironmentPathCleanup.Analyze(allVariables,RuntimeValues()); ShowCleanupPlan(); CleanupStatus.Text=status;
    }
}
