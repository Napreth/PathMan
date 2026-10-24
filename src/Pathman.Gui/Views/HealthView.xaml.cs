using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Pathman.Core.EnvironmentVariables;
using Pathman.Core.Models;
using Pathman.Core.Notifications;

namespace Pathman.Gui.Views;

public partial class HealthView : UserControl
{
    public event EventHandler? ScanRequested;
    private sealed record IssueDisplay(EnvironmentHealthIssue Issue)
    {
        public string Body=>Osc8Text.Link($"{(Issue.Scope==VariableScope.User ? "用户" : "系统")} · {Issue.Name}",new AppPageLink("variables",Issue.Scope,Issue.Name).ToString());
        public string Message=>Issue.Message;
        public string Symbol=>Issue.IsError ? "●" : "!";
        public Brush StatusBrush=>Issue.IsError ? Brushes.Firebrick : Brushes.DarkGoldenrod;
    }
    public HealthView() {InitializeComponent();}
    private void Scan(object sender,RoutedEventArgs e)=>ScanRequested?.Invoke(this,EventArgs.Empty);
    public void SetScanning(bool scanning)=>ScanButton.IsEnabled=!scanning;
    public void ShowError(string error) {Summary.Text="检测未完成"; ScanTime.Text=error;}
    public void Update(EnvironmentHealthReport report)
    {
        Summary.Text=report.Issues.Count==0 ? "所有检查均已通过" : $"发现 {report.Errors} 项错误，{report.Warnings} 项提醒";
        ScanTime.Text=$"上次检测：{DateTime.Now:HH:mm:ss}";
        IssueGroups.ItemsSource=report.Issues.GroupBy(issue=>issue.Kind).Select(group=>new {
            Title=group.Key switch {EnvironmentIssueKind.ReferenceCycle=>"引用回环",EnvironmentIssueKind.UndefinedReference=>"未定义变量",EnvironmentIssueKind.Duplicate=>"重复条目",EnvironmentIssueKind.EmptyPathEntry=>"空路径条目",_=>"引用层级"},
            Issues=group.Select(issue=>new IssueDisplay(issue)).ToArray()}).ToArray();
    }
}
