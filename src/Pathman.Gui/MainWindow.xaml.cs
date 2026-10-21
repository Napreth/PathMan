using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Pathman.Core;
using Pathman.Core.EnvironmentVariables;
using Pathman.Gui.Views;
namespace Pathman.Gui;
public partial class MainWindow : Window
{
    private readonly PathmanLayout layout=PathmanLayout.Discover();
    private readonly EnvironmentWorkspaceStore store;
    private readonly VariablesView variables;
    private readonly AliasesView aliases;
    private readonly MonitorView monitor;
    private readonly AboutView about;
    private readonly DispatcherTimer changes=new() {Interval=TimeSpan.FromSeconds(1)};
    private DateTime lastWrite;
    private bool navigating,checking;
    private ListBoxItem? previousNavigation;
    public MainWindow()
    {
        InitializeComponent(); store=new(layout.DataDirectory);
        variables=new(layout,store); aliases=new(layout); monitor=new(layout,store); about=new(layout);
        PageHost.Content=variables; previousNavigation=Navigation.SelectedItem as ListBoxItem;
        changes.Tick+=CheckWorkspace; changes.Start(); Closing+=OnClosing;
        Closed+=(_,_)=>changes.Stop();
    }
    private void Navigate(object sender,SelectionChangedEventArgs e)
    {
        if(PageHost is null || variables is null || navigating || Navigation.SelectedItem is not ListBoxItem item) return;
        if(PageHost.Content==variables && !variables.ConfirmLeave()) { navigating=true; Navigation.SelectedItem=previousNavigation; navigating=false; return; }
        previousNavigation=item;
        PageHost.Content=(item.Tag as string) switch { "aliases"=>aliases,"monitor"=>monitor,"about"=>about,_=>variables };
    }
    private async void CheckWorkspace(object? sender,EventArgs e)
    {
        if(checking || variables.IsBusy) return;
        var stamp=System.IO.File.Exists(store.FilePath) ? System.IO.File.GetLastWriteTimeUtc(store.FilePath) : DateTime.MinValue;
        if(stamp==lastWrite) return;
        checking=true;
        try
        {
            var state=await Task.Run(store.Read); monitor.Update(state);
            if(!variables.HasUnsavedChanges && !variables.IsBusy)
            {
                if(variables.LoadedRevision!=state.Revision) await variables.ReloadAsync(external:true);
                if(variables.LoadedRevision==state.Revision) lastWrite=stamp;
            }
        }
        catch(Exception error) when(error is System.IO.IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { }
        finally { checking=false; }
    }
    private void OnClosing(object? sender,CancelEventArgs e) { if(!variables.ConfirmLeave()) e.Cancel=true; }
}
