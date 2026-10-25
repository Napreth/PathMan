using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Input;
using Pathman.Core.EnvironmentVariables;
using Pathman.Core.Models;
using Pathman.Core.Notifications;

namespace Pathman.Gui.Views;

public partial class HealthView : UserControl
{
    public event EventHandler? ScanRequested;
    public event EventHandler<HealthFixEventArgs>? FixRequested;
    public event EventHandler<HealthIgnoreEventArgs>? IgnoreRequested;
    private bool working,scanning;
    private EnvironmentHealthReport? latest;
    private IReadOnlySet<string> ignored=new HashSet<string>();
    private string? detailId;
    private sealed record DetailEntry(string Text,int Line,bool IsProblem)
    {public string AutomationName=>$"第 {Line} 行"+(IsProblem ? "（问题）" : "");}
    private sealed record RelatedVariable(EnvironmentHealthDetailVariable Variable)
    {
        private VariableSnapshot Snapshot=>Variable.Snapshot;
        public string Title=>$"{(Snapshot.Scope==VariableScope.User ? "用户" : "系统")} · {Snapshot.Name}";
        public string Body=>Osc8Text.Link(Title,new AppPageLink("variables",Snapshot.Scope,Snapshot.Name).ToString());
        public IReadOnlyList<DetailEntry> Entries=>Snapshot.Value.Split(';',StringSplitOptions.None).Select((value,index)=>new DetailEntry(value.Length==0 ? "（空条目）" : value,index+1,Variable.ProblemLines.Contains(index+1))).ToArray();
    }
    private sealed record LocationDisplay(EnvironmentHealthLocation Location)
    {
        public string AutomationName=>$"{(Location.Scope==VariableScope.User ? "用户" : "系统")} · {Location.Name}，第 {Location.Line} 行";
        public string Body=>Osc8Text.Link(AutomationName,new AppPageLink("variables",Location.Scope,Location.Name).ToString());
        private static string Value(string? text,string boundary)=>text is null ? boundary : text.Length==0 ? "（空条目）" : text;
        public string Previous=>"上一行  "+Value(Location.Previous,"（列表开头）");
        public string Current=>$"第 {Location.Line} 行  "+Value(Location.Value,"");
        public string Next=>"下一行  "+Value(Location.Next,"（列表结尾）");
    }
    private sealed record IssueDisplay(EnvironmentHealthIssue Issue,bool IsIgnored)
    {
        public string AutomationName=>$"{Issue.Kind}:{Issue.Scope}:{Issue.Name}:{Issue.Locations.FirstOrDefault()?.Line ?? 0}";
        public string Body=>Osc8Text.Link($"{(Issue.Scope==VariableScope.User ? "用户" : "系统")} · {Issue.Name}",new AppPageLink("variables",Issue.Scope,Issue.Name).ToString());
        public string Message=>Issue.Message;
        public string Symbol=>Issue.IsError ? "●" : "!";
        public Brush StatusBrush=>Issue.IsError ? Brushes.Firebrick : Brushes.DarkGoldenrod;
        public string Suggestion=>Issue.Suggestion;
        public string IgnoreLabel=>IsIgnored ? "恢复提醒" : "忽略";
        public IReadOnlyList<EnvironmentHealthFix> Fixes=>IsIgnored ? [] : Issue.Fixes;
        public IReadOnlyList<LocationDisplay> Locations=>Issue.Locations.Select(location=>new LocationDisplay(location)).ToArray();
    }
    public HealthView() {InitializeComponent();}
    private void Scan(object sender,RoutedEventArgs e) {ActionStatus.Text="";ScanRequested?.Invoke(this,EventArgs.Empty);}
    private void ShowIgnoredChanged(object sender,RoutedEventArgs e) {if(latest is not null) Render();}
    private void Ignore(object sender,RoutedEventArgs e)
    {if(sender is Button {DataContext:IssueDisplay display}) IgnoreRequested?.Invoke(this,new(display.Issue,display.IsIgnored));}
    private void Fix(object sender,RoutedEventArgs e)
    {if(sender is Button {DataContext:EnvironmentHealthFix fix}) FixRequested?.Invoke(this,new(fix));}
    private void ShowDetails(object sender,RoutedEventArgs e)
    {if(sender is Button {DataContext:IssueDisplay display}) {detailId=display.Issue.Id;RenderDetails(display);}}
    private void CloseDetails(object sender,RoutedEventArgs e)
    {detailId=null;DetailsPanel.Visibility=Visibility.Collapsed;SummarySurface.Visibility=HealthScroll.Visibility=Visibility.Visible;}
    private void RenderDetails(IssueDisplay display)
    {
        SummarySurface.Visibility=HealthScroll.Visibility=Visibility.Collapsed;DetailsPanel.Visibility=Visibility.Visible;
        DetailMessage.Text=display.Message;DetailSuggestion.Text=display.Suggestion;DetailActions.DataContext=display;
        RelatedVariables.ItemsSource=EnvironmentHealthDetails.For(display.Issue).Select(variable=>new RelatedVariable(variable)).ToArray();
        DetailScroll.ScrollToTop();
        Dispatcher.BeginInvoke(()=>FirstProblem(RelatedVariables)?.BringIntoView(),System.Windows.Threading.DispatcherPriority.Loaded);
    }
    private static FrameworkElement? FirstProblem(DependencyObject parent)
    {
        if(parent is Border {DataContext:DetailEntry {IsProblem:true}} row) return row;
        for(var index=0;index<VisualTreeHelper.GetChildrenCount(parent);index++) if(FirstProblem(VisualTreeHelper.GetChild(parent,index)) is {} found) return found;
        return null;
    }
    private void ScrollHealth(object sender,MouseWheelEventArgs e)
    {if(sender is ScrollViewer scroll && scroll.ScrollableHeight>0) {scroll.ScrollToVerticalOffset(scroll.VerticalOffset-e.Delta*.5);e.Handled=true;}}
    public void SetScanning(bool value) {scanning=value;ScanButton.IsEnabled=!working && !scanning;}
    public void SetWorking(bool value) {working=value;DetailBody.IsEnabled=IssueGroups.IsEnabled=ShowIgnored.IsEnabled=!value;ScanButton.IsEnabled=!value && !scanning;}
    public void ShowActionStatus(string message,bool error=false) {ActionStatus.Text=message;ActionStatus.Foreground=error ? Brushes.Firebrick : (Brush)FindResource("TextFillColorSecondaryBrush");}
    public void ShowError(string error) {Summary.Text="检测未完成"; ScanTime.Text=error;}
    public void Update(EnvironmentHealthReport report,IReadOnlySet<string> ignoredIssues)
    {
        latest=report;ignored=ignoredIssues;Render();
    }
    private void Render()
    {
        if(detailId is {} selectedId)
        {
            var issue=latest!.Issues.FirstOrDefault(item=>item.Id==selectedId);
            if(issue is null) CloseDetails(this,new()); else RenderDetails(new(issue,ignored.Contains(issue.Id)));
        }
        var report=new EnvironmentHealthReport(latest!.Issues.Where(issue=>!ignored.Contains(issue.Id)).ToArray());
        var ignoredCount=latest.Issues.Count-report.Issues.Count;
        ShowIgnored.Visibility=ignoredCount>0 ? Visibility.Visible : Visibility.Collapsed;
        ShowIgnored.Content=$"显示已忽略（{ignoredCount}）";
        Summary.Text=report.Issues.Count==0 ? ignoredCount>0 ? "没有未忽略的问题" : "所有检查均已通过" : $"发现 {report.Errors} 项错误，{report.Warnings} 项提醒";
        ScanTime.Text=$"上次检测：{DateTime.Now:HH:mm:ss}";
        IssueGroups.ItemsSource=latest.Issues.Where(issue=>ShowIgnored.IsChecked==true || !ignored.Contains(issue.Id)).GroupBy(issue=>issue.Category).Select(group=>new {
            Title=group.Key,
            Issues=group.Select(issue=>new IssueDisplay(issue,ignored.Contains(issue.Id))).ToArray()}).ToArray();
    }
}
public sealed class HealthFixEventArgs(EnvironmentHealthFix fix) : EventArgs {public EnvironmentHealthFix Fix {get;}=fix;}
public sealed class HealthIgnoreEventArgs(EnvironmentHealthIssue issue,bool restore) : EventArgs {public EnvironmentHealthIssue Issue {get;}=issue;public bool Restore {get;}=restore;}
