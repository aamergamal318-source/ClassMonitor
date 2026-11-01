using System.Drawing;
using ClassMonitor.Shared;

namespace ClassMonitor.Agent;

/// <summary>Applies a remote-input command from the teacher to the local desktop.</summary>
internal static class RemoteInput
{
    public static void Apply(RemoteInputCmd c)
    {
        Rectangle b = ScreenCapture.VirtualBounds;

        switch (c.Kind)
        {
            case "mousemove":
                MoveTo(c, b);
                break;

            case "mousedown":
                MoveTo(c, b);
                Button(c.Button, down: true);
                break;

            case "mouseup":
                Button(c.Button, down: false);
                break;

            case "scroll":
                Native.mouse_event(Native.MOUSEEVENTF_WHEEL, 0, 0, c.Delta, 0);
                break;

            case "keydown":
                Native.keybd_event((byte)c.VKey, 0, 0, 0);
                break;

            case "keyup":
                Native.keybd_event((byte)c.VKey, 0, Native.KEYEVENTF_KEYUP, 0);
                break;
        }
    }

    private static void MoveTo(RemoteInputCmd c, Rectangle b)
    {
        int x = b.Left + (int)(Math.Clamp(c.X, 0, 1) * b.Width);
        int y = b.Top + (int)(Math.Clamp(c.Y, 0, 1) * b.Height);
        Native.SetCursorPos(x, y);
    }

    private static void Button(int button, bool down)
    {
        uint flag = button switch
        {
            1 => down ? Native.MOUSEEVENTF_RIGHTDOWN : Native.MOUSEEVENTF_RIGHTUP,
            2 => down ? Native.MOUSEEVENTF_MIDDLEDOWN : Native.MOUSEEVENTF_MIDDLEUP,
            _ => down ? Native.MOUSEEVENTF_LEFTDOWN : Native.MOUSEEVENTF_LEFTUP,
        };
        Native.mouse_event(flag, 0, 0, 0, 0);
    }
}
