using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace Pathman.Gui;

internal static class WindowFrame
{
    private static readonly ConditionalWeakTable<Window, object> installed = new();

    public static void Install(Window window)
    {
        if (installed.TryGetValue(window, out _)) return;
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;
        installed.Add(window, new());
        Apply(window, handle);
        HwndSource.FromHwnd(handle)?.AddHook((IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled) =>
        {
            if (message is 0x001A or 0x031A)
                window.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => Apply(window, hwnd)));
            return IntPtr.Zero;
        });
    }

    private static void Apply(Window window, IntPtr handle)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621))
        {
            window.SetResourceReference(Window.BackgroundProperty, "ApplicationBackgroundBrush");
            return;
        }
        // COLOR_NONE removes the accent-colored caption; DWM draws Mica across the client area.
        var caption = 0xFFFFFFFEu;
        var backdrop = 2u; // DWMSBT_MAINWINDOW
        var corners = 2u; // DWMWCP_ROUND
        DwmSetWindowAttribute(handle, 35, ref caption, sizeof(uint));
        DwmSetWindowAttribute(handle, 33, ref corners, sizeof(uint));
        if (DwmSetWindowAttribute(handle, 38, ref backdrop, sizeof(uint)) == 0)
        {
            window.Background = Brushes.Transparent;
            if (HwndSource.FromHwnd(handle)?.CompositionTarget is { } target)
                target.BackgroundColor = Colors.Transparent;
        }
        else window.SetResourceReference(Window.BackgroundProperty, "ApplicationBackgroundBrush");
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref uint value, int size);
}
