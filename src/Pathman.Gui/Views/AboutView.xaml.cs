using System.Windows;
using System.Windows.Controls;
using Pathman.Core.Updates;
using Pathman.Gui.Dialogs;

namespace Pathman.Gui.Views;

public partial class AboutView : UserControl
{
    private readonly ApplicationUpdateService updates;
    private readonly Func<string,Task<bool>> install;
    private UpdateDialog? releaseDialog;
    public AboutView(ApplicationUpdateService updates,Func<string,Task<bool>> install)
    {
        this.updates=updates;this.install=install;InitializeComponent();VersionText.Text=AppInfo.DisplayVersion;
        AppIcon.Source=System.Windows.Media.Imaging.BitmapDecoder.Create(new Uri("pack://application:,,,/Assets/PathMan.ico"),System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat,System.Windows.Media.Imaging.BitmapCacheOption.OnLoad).Frames.MaxBy(frame=>frame.PixelWidth);
        updates.Changed+=(_,_)=>Update();L.Current.PropertyChanged+=(_,_)=>Update();Loaded+=(_,_)=>Update();
    }
    private void Update()
    {
        CheckUpdatesButton.IsEnabled=!updates.IsChecking;UpdateStatus.Text=updates.Status;
        UpdateStatus.Foreground=TryFindResource(updates.HasResult ? "SystemFillColorSuccessBrush" : "TextFillColorSecondaryBrush") as System.Windows.Media.Brush
            ?? (updates.HasResult ? System.Windows.Media.Brushes.ForestGreen : System.Windows.Media.Brushes.Gray);
        ReleaseLink.Visibility=updates.Available is null ? Visibility.Collapsed : Visibility.Visible;
    }
    private void OpenSource(object sender,RoutedEventArgs e)=>OpenPage(GitHubReleaseClient.RepositoryPage);
    private async void CheckUpdates(object sender,RoutedEventArgs e)=>await updates.CheckAsync();
    private void OpenRelease(object sender,RoutedEventArgs e)=>ShowReleaseDetails();
    public bool IsUpdateOpen=>releaseDialog?.IsVisible==true;
    public void ShowReleaseDetails(bool fromNotification=false)
    {
        if(releaseDialog?.IsVisible==true) {if(releaseDialog.WindowState==WindowState.Minimized) releaseDialog.WindowState=WindowState.Normal;releaseDialog.Activate();return;}
        if((fromNotification ? updates.Latest : updates.Available) is not {} release) return;
        try {releaseDialog=new(Window.GetWindow(this),release,updates,install);releaseDialog.ShowDialog();}
        finally {releaseDialog=null;}
    }
    private void OpenPage(Uri page)
    {
        try {if(page.Scheme==Uri.UriSchemeHttps) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(page.AbsoluteUri) {UseShellExecute=true});}
        catch(System.ComponentModel.Win32Exception error) {UpdateStatus.Text=error.Message;}
    }
    private void ShowLicense(object sender,RoutedEventArgs e)=>new LicenseDialog(Window.GetWindow(this)).ShowDialog();
}
