using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Relay;

internal static class NativeTheme
{
    public static void Apply(Window window)
    {
        window.SourceInitialized += (_, _) =>
        {
            var enabled = 1;
            DwmSetWindowAttribute(new WindowInteropHelper(window).Handle, 20, ref enabled, sizeof(int));
        };
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr handle, int attribute, ref int value, int size);
}
