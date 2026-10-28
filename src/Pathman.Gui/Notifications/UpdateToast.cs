using System.IO;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using Microsoft.Win32;
using Pathman.Core;
using Pathman.Core.Updates;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace Pathman.Gui.Notifications;

internal static class UpdateToast
{
#if PATHMAN_UPDATE_TEST
    private const string AppId="PathMan.UpdateTest";
#else
    private const string AppId="PathMan.PathMan";
#endif
    public static bool Show(AvailableRelease release,PathmanLayout layout)
    {
        try
        {
            UpdateActivation.RegisterProtocol(layout);
            Directory.CreateDirectory(layout.DataDirectory);
            var icon=Path.Combine(layout.DataDirectory,"notification-icon.png");
            if(!File.Exists(icon))
            {
                var decoder=BitmapDecoder.Create(new Uri("pack://application:,,,/Assets/PathMan.ico"),BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnLoad);
                var encoder=new PngBitmapEncoder();encoder.Frames.Add(decoder.Frames.MaxBy(frame=>frame.PixelWidth)!);
                using var file=File.Create(icon);encoder.Save(file);
            }
            using(var identity=Registry.CurrentUser.CreateSubKey(@"Software\Classes\AppUserModelId\"+AppId))
            {
                identity.SetValue("DisplayName","PathMan");identity.SetValue("IconUri",icon);
                identity.SetValue("CustomActivator","{F89A03AF-34BD-4B08-92BD-BA7E3C2719BA}");
                identity.SetValue("ExecutablePath",Path.Combine(layout.InstallationDirectory,"PathMan.exe"));
            }
            var xml=new XmlDocument();
            xml.LoadXml(new XElement("toast",new XAttribute("activationType","protocol"),new XAttribute("launch",UpdateActivation.Link(release.Tag)),
                new XElement("visual",new XElement("binding",new XAttribute("template","ToastGeneric"),
                    new XElement("text",L.Format("PathMan v{0} 已发布！",release.Manifest is {} manifest ? $"{manifest.VersionName} ({manifest.VersionNumber})" : ReleaseIdentity.Normalize(release.Tag))),new XElement("text",L.Get("点击查看新版本内容"))))).ToString(SaveOptions.DisableFormatting));
            var notifier=ToastNotificationManager.CreateToastNotifier(AppId);
            try {if(notifier.Setting!=NotificationSetting.Enabled) return false;}
            // Windows has no per-app notification settings until the first toast is sent.
            catch(System.Runtime.InteropServices.COMException error) when(error.HResult==unchecked((int)0x80070490)) {}
            notifier.Show(new ToastNotification(xml) {Tag="update",Group="releases",ExpirationTime=DateTimeOffset.Now.AddDays(7)});
#if PATHMAN_UPDATE_TEST
            File.WriteAllText(Path.Combine(layout.DataDirectory,"update-test-toast.txt"),"Shown\n"+xml.GetXml());
#endif
            return true;
        }
        catch(Exception error) when(error is System.Runtime.InteropServices.COMException or IOException or UnauthorizedAccessException or ArgumentException)
        {
#if PATHMAN_UPDATE_TEST
            try {File.WriteAllText(Path.Combine(layout.DataDirectory,"update-test-toast.txt"),error.ToString());}catch(IOException) {}catch(UnauthorizedAccessException) {}
#endif
            return false;
        }
    }
}
