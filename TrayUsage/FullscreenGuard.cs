using System.Runtime.InteropServices;
using System.Text;

namespace TrayUsage;

/// <summary>Detects a borderless full-screen app (game, video, presentation) in the foreground.</summary>
internal static class FullscreenGuard
{
    private const int GwlStyle = -16;
    private const long WsCaption = 0x00C00000;

    public static bool IsForegroundFullscreen()
    {
        var window = GetForegroundWindow();
        if (window == IntPtr.Zero || !GetWindowRect(window, out var rect)) return false;
        var name = new StringBuilder(64);
        GetClassName(window, name, name.Capacity);
        if (name.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd") return false; // desktop and taskbar
        // A maximized normal window keeps its title bar; full-screen apps do not.
        if ((GetWindowLongPtr(window, GwlStyle).ToInt64() & WsCaption) == WsCaption) return false;
        var bounds = Screen.FromHandle(window).Bounds;
        return rect.Left <= bounds.Left && rect.Top <= bounds.Top && rect.Right >= bounds.Right && rect.Bottom >= bounds.Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder text, int count);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
}
