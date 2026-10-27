using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using Pathman.Core.EnvironmentVariables;
using Pathman.Core.Models;

namespace Pathman.Gui.Dialogs;

public sealed class TextDialog : Window
{
    private readonly TextBox input;
    public string Value => input.Text;
    public TextDialog(Window owner, string title, string label, string value, bool multiline = false, Func<string, string?>? validate = null)
    {
        Owner = owner; Title = title; Width = multiline ? 580 : 440; Height = multiline ? 350 : 205;
        ResizeMode = ResizeMode.CanResize; MinWidth = 380; MinHeight = multiline ? 280 : 205;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var grid = new Grid { Margin = new Thickness(24) };
        grid.RowDefinitions.Add(new() { Height = GridLength.Auto }); grid.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new() { Height = GridLength.Auto }); grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        grid.Children.Add(new TextBlock { Text = label, Margin = new(0,0,0,8) });
        input = new TextBox { Text = value, AcceptsReturn = multiline, TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalContentAlignment = multiline ? VerticalAlignment.Top : VerticalAlignment.Center };
        AutomationProperties.SetAutomationId(input, "TextInput"); Grid.SetRow(input,1); grid.Children.Add(input);
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new(0,6,0,0) }; Grid.SetRow(error,2); grid.Children.Add(error);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0,16,0,0) };
        var ok = new Button { Content = L.Get("确定"), IsDefault = !multiline, MinWidth = 84, Margin = new(0,0,8,0) };
        ok.Click += (_,_) => { var issue = validate?.Invoke(input.Text); if (issue is not null) { error.Text = issue; return; } DialogResult = true; };
        buttons.Children.Add(ok); buttons.Children.Add(new Button { Content = L.Get("取消"), IsCancel = true, MinWidth = 84 }); Grid.SetRow(buttons,3); grid.Children.Add(buttons);
        Content = grid; Loaded += (_,_) => { input.Focus(); if (!multiline) input.SelectAll(); };
    }
}

public sealed class VariableDialog : Window
{
    private readonly TextBox name;
    private readonly TextBox value;
    private readonly ComboBox group;
    private readonly CheckBox lines;
    public string VariableName => name.Text;
    public VariableScope Scope { get; }
    public string GroupId => ((VariableGroupDefinition)group.SelectedItem).Id;
    public string RawValue => lines.IsChecked == true ? string.Join(';', value.Text.Replace("\r\n", "\n").Replace('\r','\n').Split('\n')) : value.Text;
    public VariableDialog(Window owner, VariableScope initialScope, IReadOnlyList<VariableGroupDefinition> groups, string initialName = "", string initialValue = "", bool list = false)
    {
        Scope=initialScope;
        Owner = owner; Title = initialScope==VariableScope.User ? L.Get("新建用户环境变量") : L.Get("新建系统环境变量"); Width = 560; Height = 430; MinWidth = 460; MinHeight = 360; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var grid = new Grid { Margin = new(24) };
        foreach (var height in new[] { GridLength.Auto, GridLength.Auto, GridLength.Auto, new GridLength(1,GridUnitType.Star), GridLength.Auto, GridLength.Auto }) grid.RowDefinitions.Add(new() { Height=height });
        var headings=new Grid(); headings.ColumnDefinitions.Add(new()); headings.ColumnDefinitions.Add(new() {Width=new(140)});
        headings.Children.Add(new TextBlock {Text=L.Get("变量名"),Margin=new(0,0,0,6)});
        var groupHeading=new TextBlock {Text=L.Get("分组"),Margin=new(0,0,0,6)}; Grid.SetColumn(groupHeading,1); headings.Children.Add(groupHeading); grid.Children.Add(headings);
        var row = new Grid { Margin=new(0,0,0,16) }; row.ColumnDefinitions.Add(new()); row.ColumnDefinitions.Add(new() { Width=new(140) });
        name = new TextBox { Text=initialName, Margin=new(0,0,12,0) }; AutomationProperties.SetAutomationId(name,"VariableNameInput"); row.Children.Add(name);
        group = new ComboBox { ItemsSource=groups, DisplayMemberPath=nameof(VariableGroupDefinition.DisplayName), SelectedItem=groups.First(item=>item.Rule=="fallback") };
        AutomationProperties.SetAutomationId(group,"VariableGroupInput"); AutomationProperties.SetName(group,L.Get("分组")); Grid.SetColumn(group,1); row.Children.Add(group); Grid.SetRow(row,1); grid.Children.Add(row);
        grid.Children.Add(new TextBlock { Text=L.Get("变量值") }); Grid.SetRow(grid.Children[^1],2);
        value = new Pathman.Gui.Controls.ReferenceTextBox { Text=list ? initialValue.Replace(";", Environment.NewLine) : initialValue, AcceptsReturn=true, TextWrapping=TextWrapping.Wrap, VerticalScrollBarVisibility=ScrollBarVisibility.Auto, Margin=new(0,6,0,0) }; AutomationProperties.SetAutomationId(value,"VariableValueInput"); Grid.SetRow(value,3); grid.Children.Add(value);
        lines = new CheckBox { Content=L.Get("每行一个条目（保存为分号分隔）"), IsChecked=list, Margin=new(0,10,0,0) }; Grid.SetRow(lines,4); grid.Children.Add(lines);
        var buttons = new StackPanel { Orientation=Orientation.Horizontal, HorizontalAlignment=HorizontalAlignment.Right, Margin=new(0,18,0,0) };
        var ok = new Button { Content=L.Get("确定"), MinWidth=84, Margin=new(0,0,8,0) };
        ok.Click += (_,_) => { try { WindowsEnvironmentVariableEditor.ValidateName(name.Text); DialogResult=true; } catch (ArgumentException e) { MessageBox.Show(this,e.Message,L.Get("变量名无效")); } };
        buttons.Children.Add(ok); buttons.Children.Add(new Button { Content=L.Get("取消"), MinWidth=84, IsCancel=true }); Grid.SetRow(buttons,5); grid.Children.Add(buttons); Content=grid;
        Loaded += (_,_) => name.Focus();
    }
}
