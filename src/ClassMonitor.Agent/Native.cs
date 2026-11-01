using System.Runtime.InteropServices;

namespace ClassMonitor.Agent;

/// <summary>
/// Thin Win32 interop for injecting mouse/keyboard input during a remote-control session.
/// Only used on machines the operator administers, with the agent tray icon visible.
/// </summary>
internal static partial class Native
{
    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetCursorPos(int x, int y);

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial void mouse_event(uint dwFlags, int dx, int dy, int dwData, nuint dwExtraInfo);

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial void keybd_event(byte bVk, byte bScan, uint dwFlags, nuint dwExtraInfo);

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial IntPtr GetForegroundWindow();

    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial int GetWindowText(IntPtr hWnd, [Out] char[] text, int maxCount);

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    // mouse_event flags
    public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    public const uint MOUSEEVENTF_LEFTUP = 0x0004;
    public const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    public const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    public const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
    public const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
    public const uint MOUSEEVENTF_WHEEL = 0x0800;

    // keybd_event flags
    public const uint KEYEVENTF_KEYUP = 0x0002;
}
