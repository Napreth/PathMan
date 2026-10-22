using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Pathman.Core.Models;

namespace Pathman.Gui.Controls;

public enum ScopeAction { Delete, Rename, NewGroup, NewVariable }
public sealed class ScopeActionEventArgs(VariableScope scope, ScopeAction action) : EventArgs
{
    public VariableScope Scope { get; } = scope;
    public ScopeAction Action { get; } = action;
}

public partial class ScopeActions : UserControl
{
    public VariableScope Scope { get; set; }
    public event EventHandler<ScopeActionEventArgs>? ActionRequested;

    public ScopeActions()
    {
        InitializeComponent();
        Unloaded += (_, _) => NewMenu.IsOpen = false;
        IsEnabledChanged += (_, _) => { if (!IsEnabled) NewMenu.IsOpen = false; };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape && NewMenu.IsOpen) { NewMenu.IsOpen = false; e.Handled = true; } };
    }

    public void UpdateSelection(bool canRename, bool canDelete, string deleteTip)
    {
        RenameButton.IsEnabled = canRename;
        DeleteButton.IsEnabled = canDelete;
        DeleteButton.ToolTip = deleteTip;
    }

    private void Raise(ScopeAction action) { NewMenu.IsOpen = false; ActionRequested?.Invoke(this, new(Scope, action)); }
    private void Delete(object sender, RoutedEventArgs e) => Raise(ScopeAction.Delete);
    private void Rename(object sender, RoutedEventArgs e) => Raise(ScopeAction.Rename);
    private void NewGroup(object sender, RoutedEventArgs e) => Raise(ScopeAction.NewGroup);
    private void NewVariable(object sender, RoutedEventArgs e) => Raise(ScopeAction.NewVariable);
    private void OpenMenu(object sender, RoutedEventArgs e) => NewMenu.IsOpen = true;
    private void ShowMenu(object sender, MouseEventArgs e) { if (IsEnabled) NewMenu.IsOpen = true; }
    private async void CloseMenu(object sender, MouseEventArgs e)
    {
        await Task.Delay(180);
        if (!NewButton.IsMouseOver && !(NewMenu.Child is FrameworkElement child && child.IsMouseOver)) NewMenu.IsOpen = false;
    }
}
