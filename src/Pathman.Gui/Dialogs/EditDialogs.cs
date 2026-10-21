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
        var ok = new Button { Content = "确定", IsDefault = !multiline, MinWidth = 84, Margin = new(0,0,8,0) };
        ok.Click += (_,_) => { var issue = validate?.Invoke(input.Text); if (issue is not null) { error.Text = issue; return; } DialogResult = true; };
        buttons.Children.Add(ok); buttons.Children.Add(new Button { Content = "取消", IsCancel = true, MinWidth = 84 }); Grid.SetRow(buttons,3); grid.Children.Add(buttons);
        Content = grid; Loaded += (_,_) => { input.Focus(); if (!multiline) input.SelectAll(); };
    }
}

public sealed class VariableDialog : Window
{
    private readonly TextBox name;
    private readonly TextBox value;
    private readonly ComboBox scope;
    private readonly CheckBox lines;
    public string VariableName => name.Text;
    public VariableScope Scope => scope.SelectedIndex == 0 ? VariableScope.User : VariableScope.Machine;
    public string RawValue => lines.IsChecked == true ? string.Join(';', value.Text.Replace("\r\n", "\n").Replace('\r','\n').Split('\n')) : value.Text;
    public VariableDialog(Window owner, VariableScope initialScope, string initialName = "", string initialValue = "", bool list = false)
    {
        Owner = owner; Title = "新建环境变量"; Width = 560; Height = 430; MinWidth = 460; MinHeight = 360; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var grid = new Grid { Margin = new(24) };
        foreach (var height in new[] { GridLength.Auto, GridLength.Auto, GridLength.Auto, new GridLength(1,GridUnitType.Star), GridLength.Auto, GridLength.Auto }) grid.RowDefinitions.Add(new() { Height=height });
        grid.Children.Add(new TextBlock { Text="变量名", Margin=new(0,0,0,6) });
        var row = new Grid { Margin=new(0,0,0,16) }; row.ColumnDefinitions.Add(new()); row.ColumnDefinitions.Add(new() { Width=new(140) });
        name = new TextBox { Text=initialName, Margin=new(0,0,12,0) }; AutomationProperties.SetAutomationId(name,"VariableNameInput"); row.Children.Add(name);
        scope = new ComboBox { ItemsSource=new[] { "用户变量", "系统变量" }, SelectedIndex=initialScope == VariableScope.User ? 0 : 1 }; AutomationProperties.SetAutomationId(scope,"VariableScopeInput"); Grid.SetColumn(scope,1); row.Children.Add(scope); Grid.SetRow(row,1); grid.Children.Add(row);
        grid.Children.Add(new TextBlock { Text="变量值" }); Grid.SetRow(grid.Children[^1],2);
        value = new TextBox { Text=list ? initialValue.Replace(";", Environment.NewLine) : initialValue, AcceptsReturn=true, TextWrapping=TextWrapping.Wrap, VerticalScrollBarVisibility=ScrollBarVisibility.Auto, Margin=new(0,6,0,0) }; AutomationProperties.SetAutomationId(value,"VariableValueInput"); Grid.SetRow(value,3); grid.Children.Add(value);
        lines = new CheckBox { Content="每行一个条目（保存为分号分隔）", IsChecked=list, Margin=new(0,10,0,0) }; Grid.SetRow(lines,4); grid.Children.Add(lines);
        var buttons = new StackPanel { Orientation=Orientation.Horizontal, HorizontalAlignment=HorizontalAlignment.Right, Margin=new(0,18,0,0) };
        var ok = new Button { Content="确定", MinWidth=84, Margin=new(0,0,8,0) };
        ok.Click += (_,_) => { try { WindowsEnvironmentVariableEditor.ValidateName(name.Text); DialogResult=true; } catch (ArgumentException e) { MessageBox.Show(this,e.Message,"变量名无效"); } };
        buttons.Children.Add(ok); buttons.Children.Add(new Button { Content="取消", MinWidth=84, IsCancel=true }); Grid.SetRow(buttons,5); grid.Children.Add(buttons); Content=grid;
        Loaded += (_,_) => name.Focus();
    }
}
