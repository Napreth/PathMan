using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Pathman.Core.Models;

namespace Pathman.Gui.Controls;

public enum ScopeAction { NewGroup, NewVariable }
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
        NewMenu.CustomPopupPlacementCallback=(popup,target,_)=>[
            new(new Point(target.Width-popup.Width,target.Height),PopupPrimaryAxis.Horizontal),
            new(new Point(target.Width-popup.Width,-popup.Height),PopupPrimaryAxis.Horizontal)];
        Unloaded += (_, _) => NewMenu.IsOpen = false;
        IsEnabledChanged += (_, _) => { if (!IsEnabled) NewMenu.IsOpen = false; };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape && NewMenu.IsOpen) { NewMenu.IsOpen = false; e.Handled = true; } };
    }

    private void Raise(ScopeAction action) { NewMenu.IsOpen = false; ActionRequested?.Invoke(this, new(Scope, action)); }
    private void NewGroup(object sender, RoutedEventArgs e) => Raise(ScopeAction.NewGroup);
    private void NewVariable(object sender, RoutedEventArgs e) => Raise(ScopeAction.NewVariable);
    private void OpenMenu(object sender, RoutedEventArgs e)
    {
        NewMenu.PlacementTarget=NewButton;
        NewMenu.IsOpen=true;
    }
}
