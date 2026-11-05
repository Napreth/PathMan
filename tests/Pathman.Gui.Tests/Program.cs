using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Pathman.Core;
using Pathman.Core.EnvironmentVariables;
using Pathman.Core.Models;
using Pathman.Gui;
using Pathman.Gui.Controls;
using Pathman.Gui.Dialogs;
using Pathman.Gui.Views;

internal static class Program
{
    private sealed class FixtureApplication : Application
    {
        // Reuse the production resource dictionary without starting the real
        // application, its monitor or its profile synchronization.
        protected override void OnStartup(StartupEventArgs e) { }
    }
    private static int checks;
    private static readonly string Root=Path.Combine(Path.GetTempPath(),"PathMan.Gui.Tests",Guid.NewGuid().ToString("N"));
    [STAThread]
    private static int Main(string[] args)
    {
        if(args.FirstOrDefault()=="--environment-session") return ElevatedEnvironmentSession.RunWorkerAsync(args).GetAwaiter().GetResult();
        using var watchdog=new System.Threading.Timer(_=> {Console.Error.WriteLine("FAIL: GUI regression timed out.");Environment.Exit(2);},null,TimeSpan.FromSeconds(90),Timeout.InfiniteTimeSpan);
#pragma warning disable WPF0001 // Match the production App.xaml Fluent theme.
        var app=new FixtureApplication {ShutdownMode=ShutdownMode.OnExplicitShutdown,ThemeMode=ThemeMode.System};
#pragma warning restore WPF0001
        foreach(var name in new[]{"Settings","NavigationIcons"})
            app.Resources.MergedDictionaries.Add(new ResourceDictionary {Source=new Uri($"pack://application:,,,/PathMan;component/Styles/{name}.xaml")});
        var exit=1;
        app.Dispatcher.BeginInvoke(async()=>
        {
            try {await Run();if(args.Contains("--test-authorization")) await Authorization();Console.WriteLine($"PASS: {checks} GUI checks: inline editing, new rows, shared discard dialogs, selection races and monitoring destinations.");exit=0;}
            catch(Exception error) {Console.Error.WriteLine(error);}
            finally
            {
                foreach(Window window in app.Windows.Cast<Window>().ToArray()) window.Close();
                if(Directory.Exists(Root) && Path.GetFullPath(Root).StartsWith(Path.Combine(Path.GetTempPath(),"PathMan.Gui.Tests")+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)) Directory.Delete(Root,true);
                Console.WriteLine("GUI fixture cleanup complete.");
                app.Shutdown(exit);
            }
        });
        return app.Run();
    }
    private static void Assert(bool condition,string message)
    {if(!condition) throw new InvalidOperationException(message);checks++;}
    private static T Named<T>(FrameworkElement parent,string name) where T:class => (T)parent.FindName(name);
    private static T Field<T>(object target,string name)=>(T)target.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(target)!;
    private static void Set(object target,string name,object value)=>target.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(target,value);
    private static object? Call(object target,string name,params object?[] args)=>target.GetType().GetMethod(name,BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(target,args);
    private static async Task Until(Func<bool> condition)
    {for(var i=0;i<300;i++) {if(condition()) return;await Task.Delay(20);}throw new TimeoutException("The GUI did not reach the expected state.");}
    private static T? Visual<T>(DependencyObject root) where T:DependencyObject
    {
        if(root is T match) return match;
        for(var i=0;i<VisualTreeHelper.GetChildrenCount(root);i++) if(Visual<T>(VisualTreeHelper.GetChild(root,i)) is {} child) return child;
        return null;
    }
    private static EditableRow Row(ListBox list,int index)
    {list.ScrollIntoView(list.Items[index]);list.UpdateLayout();return Visual<EditableRow>((DependencyObject)list.ItemContainerGenerator.ContainerFromIndex(index))!;}
    private static void Click(Button button)=>button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void Capture(Window window,string name)
    {
        window.UpdateLayout();var bitmap=new RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(window);
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output=File.Create(Path.Combine(AppContext.BaseDirectory,name+".png"));encoder.Save(output);
    }
    private static async Task Run()
    {
        var layout=new PathmanLayout(Path.Combine(Root,"app"),Path.Combine(Root,"data"));
        var store=new EnvironmentWorkspaceStore(layout.DataDirectory);
        store.Update(state=> {state.MonitoringEnabled=true;state.AutoArrangeUserPath=true;state.AutoArrangeMachinePath=false;state.Groups.Add(new() {Id="custom",Name="Team tools",Scope=VariableScope.User});});
        var view=new VariablesView(layout,store);
        var window=new Window {Width=1200,Height=850,Content=view,Background=Brushes.WhiteSmoke,Foreground=Brushes.Black};window.Show();
        await Until(()=>!Field<bool>(view,"busy") && Field<IReadOnlyList<EnvironmentTreeNode>>(view,"userGroups").Count>0);
        // Inject isolated fixtures after loading. No test writes to real environment keys.
        var variables=new[]{new EnvironmentVariableInfo("GUI_TEST_PATH",@"C:\one;D:\two",VariableScope.User,RegistryValueKind.String),new EnvironmentVariableInfo("GUI_SECOND","second",VariableScope.User,RegistryValueKind.String),new EnvironmentVariableInfo("GUI_THIRD","third",VariableScope.User,RegistryValueKind.String)};
        var groups=EnvironmentTreeNode.Build(VariableScope.User,variables,store.Read(),new Dictionary<string,string>());
        Set(view,"allVariables",variables);Set(view,"userGroups",groups);Set(view,"systemGroups",Array.Empty<EnvironmentTreeNode>());
        Named<TreeView>(view,"UserVariablesTree").ItemsSource=groups;Named<TreeView>(view,"SystemVariablesTree").ItemsSource=Array.Empty<EnvironmentTreeNode>();
        var nodes=groups.SelectMany(group=>group.Children).ToArray();
        var root=nodes.Single(node=>node.Title=="GUI_TEST_PATH");Call(view,"ShowSelection",root);await Task.Delay(60);
        var list=Named<ListBox>(view,"ValuesList");var save=Named<Button>(view,"SaveButton");
        var first=Row(list,0);first.BeginEdit();first.EditingValue=@"C:\changed";
        Assert(save.IsEnabled && save.FontWeight==FontWeights.SemiBold,"An active edit must highlight Save.");
        // Actual button event while the original editor still owns keyboard focus.
        Click(Named<Button>(view,"AddEntryButton"));
        await Until(()=>list.Items.Count==3 && Row(list,2).IsEditing);
        Assert(ReferenceEquals(first,Row(list,0)) && !first.IsEditing,"New must commit without replacing the original row control.");
        Assert(((EnvironmentEntry)list.Items[0]).Value==@"C:\changed","New must preserve the previous edit.");
        var draft=Row(list,2);draft.EditingValue=@"E:\new";
        Named<TextBlock>(view,"VariableTitle").RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice,Environment.TickCount,MouseButton.Left){RoutedEvent=Mouse.PreviewMouseDownEvent});
        await Until(()=>!draft.IsEditing);
        Assert(view.HasUnsavedChanges && !view.HasInlineEditor,"Clicking non-focusable space must end editing while keeping the unsaved draft.");
        Capture(window,"variables");

        Task<bool>? nested=null;Exception? dialogError=null;
        var timer=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(50)};
        timer.Tick+=(_,_)=>
        {
            timer.Stop();var dialog=Application.Current.Windows.OfType<ConfirmDialog>().SingleOrDefault();
            try
            {
                Assert(dialog is not null,"Unsaved edits must show the discard dialog.");
                nested=view.ConfirmLeaveAsync();
                Assert(Application.Current.Windows.OfType<ConfirmDialog>().Count()==1,"A repeated request must reuse the existing dialog.");
                Assert(ReferenceEquals(nested,Field<Task<bool>>(view,"leavePending")),"A repeated request must share the pending decision.");
            }
            catch(Exception error) {dialogError=error;}
            finally {if(dialog is not null) dialog.DialogResult=false;}
        };
        timer.Start();var decision=view.ConfirmLeaveAsync();
        Assert(!await decision && nested is not null && !await nested,"Cancel must finish every caller without discarding edits.");
        if(dialogError is not null) throw dialogError;
        Assert(!view.IsBusy && view.HasUnsavedChanges,"Cancel must release the busy state.");

        var second=nodes.Single(node=>node.Title=="GUI_SECOND");var third=nodes.Single(node=>node.Title=="GUI_THIRD");
        var race=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(50)};
        race.Tick+=(_,_)=> {race.Stop();Call(view,"SelectVariable",view,new RoutedPropertyChangedEventArgs<object>(root,third));Application.Current.Windows.OfType<ConfirmDialog>().Single().DialogResult=true;};
        race.Start();Call(view,"SelectVariable",view,new RoutedPropertyChangedEventArgs<object>(root,second));
        await Until(()=>Field<EnvironmentTreeNode>(view,"selected")==third);
        Assert(!view.HasUnsavedChanges && !view.IsBusy,"The latest selection must win after a shared discard decision.");

        Call(view,"ShowSelection",root);await Task.Delay(40);first=Row(list,0);first.BeginEdit();first.EditingValue=@"C:\new-variable";
        var stage=0;var create=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(50)};
        create.Tick+=(_,_)=>
        {
            if(Application.Current.Windows.OfType<ConfirmDialog>().SingleOrDefault(item=>item.IsVisible) is {} discard) {stage++;discard.DialogResult=true;}
            else if(Application.Current.Windows.OfType<VariableDialog>().SingleOrDefault(item=>item.IsVisible) is {} prompt) {stage++;prompt.Close();create.Stop();}
        };
        create.Start();await (Task)Call(view,"NewVariable",VariableScope.User)!;
        Assert(stage==2 && !view.HasInlineEditor && !view.IsBusy,"New variable during an edit must resolve discard first and allow the new dialog to close.");

        var monitor=new MonitorView(layout,store);window.Content=monitor;await Task.Delay(100);
        var category=Named<ComboBox>(monitor,"UserExternalGroup");category.SelectedValue="custom";
        Assert(store.Read().ExternalUserGroupId=="custom","The category dropdown must persist its selection.");
        Assert(Named<CheckBox>(monitor,"GroupExternalUser").IsChecked==true,"Selecting a category must preserve its parent toggle.");
        var target=Named<ComboBox>(monitor,"UserPathTarget");target.Text="TEAM_TOOLS_PATH";
        var input=(TextBox)target.Template.FindName("PART_EditableTextBox",target);input.Focus();
        Named<CheckBox>(monitor,"GroupExternalUser").Focus();await Task.Delay(60);
        Assert(store.Read().ExternalUserPathVariable=="TEAM_TOOLS_PATH","Leaving a typed destination must persist its full name, including underscores.");
        var move=Named<CheckBox>(monitor,"MoveUserEntries");target.IsDropDownOpen=true;await Task.Delay(30);target.IsDropDownOpen=false;
        Assert(move.IsChecked==true,"Opening a destination dropdown must preserve the migration toggle.");
        var label=(TextBlock)category.ItemTemplate.LoadContent();label.DataContext=category.SelectedItem;await Task.Delay(20);
        Assert(label.Text=="“Team tools”","Category labels must display Chinese double quotes.");
        target.Text="Path";Call(monitor,"PathTargetChanged",target,EventArgs.Empty);
        Assert(store.Read().ExternalUserPathVariable=="TEAM_TOOLS_PATH" && Named<Border>(monitor,"MonitorNotice").Visibility==Visibility.Visible,"An invalid destination must show feedback and preserve the saved target.");
        target.Text="TEAM_TOOLS_PATH";Call(monitor,"PathTargetChanged",target,EventArgs.Empty);
        Assert(!Directory.EnumerateFiles(Root,"*.bak",SearchOption.AllDirectories).Any(),"GUI tests must not write environment backups.");
        Capture(window,"monitor");
        window.Close();
    }
    private static async Task Authorization()
    {
        using var session=await ElevatedEnvironmentSession.StartAsync();
        for(var attempt=0;attempt<2;attempt++)
        {
            try
            {
                // An impossible precondition must fail before any mutation. Exercise
                // the real authenticated worker twice to verify conflict propagation
                // and connection reuse without modifying environment keys.
                await session.ApplyAsync([new("create",VariableScope.Machine,"PATHMAN_GUI_"+Guid.NewGuid().ToString("N"),"unused")],
                    [new(VariableScope.Machine,"Path","impossible-"+Guid.NewGuid().ToString("N"),(int)RegistryValueKind.String)]);
                throw new InvalidOperationException("A stale precondition was accepted.");
            }
            catch(EnvironmentConflictException) {Assert(session.IsAlive,"A registry conflict must retain the authorized session.");}
        }
    }
}
