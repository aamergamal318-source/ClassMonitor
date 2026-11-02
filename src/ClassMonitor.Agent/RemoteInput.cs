using System.ComponentModel;
using System.Drawing;
using System.Runtime.InteropServices;
using ClassMonitor.Shared;
namespace ClassMonitor.Agent;

internal static class RemoteInput
{
    private static readonly object Gate = new();
    private static readonly HashSet<int> InjectedKeys = new(), InjectedButtons = new();
    public static void Apply(RemoteInputCmd command, int monitorIndex = -1)
    {
        Validate(command);
        lock (Gate)
        {
            switch (command.Kind)
            {
                case "mousemove": Move(command, monitorIndex); break;
                case "mousedown": Move(command, monitorIndex); Native.mouse_event(ButtonFlag(command.Button, true), 0, 0, 0, 0); InjectedButtons.Add(command.Button); break;
                case "mouseup": if (InjectedButtons.Remove(command.Button)) Native.mouse_event(ButtonFlag(command.Button, false), 0, 0, 0, 0); break;
                case "scroll": Native.mouse_event(Native.MOUSEEVENTF_WHEEL, 0, 0, command.Delta, 0); break;
                case "keydown": Native.keybd_event((byte)command.VKey, 0, 0, 0); InjectedKeys.Add(command.VKey); break;
                case "keyup": if (InjectedKeys.Remove(command.VKey)) Native.keybd_event((byte)command.VKey, 0, Native.KEYEVENTF_KEYUP, 0); break;
            }
        }
    }
    public static void ReleaseAll()
    {
        lock (Gate)
        {
            foreach (int key in InjectedKeys) Native.keybd_event((byte)key, 0, Native.KEYEVENTF_KEYUP, 0);
            foreach (int button in InjectedButtons) Native.mouse_event(ButtonFlag(button, false), 0, 0, 0, 0);
            InjectedKeys.Clear(); InjectedButtons.Clear();
        }
    }
    public static void Validate(RemoteInputCmd command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!double.IsFinite(command.X) || !double.IsFinite(command.Y) || command.X is < 0 or > 1 || command.Y is < 0 or > 1)
            throw new InvalidDataException("Remote input coordinates must be finite normalized values.");
        if (command.Kind is not ("mousemove" or "mousedown" or "mouseup" or "scroll" or "keydown" or "keyup")) throw new InvalidDataException("Unsupported remote input command.");
        if ((command.Kind is "mousedown" or "mouseup") && (command.Button is < 0 or > 2)) throw new InvalidDataException("Unsupported pointer button.");
        if ((command.Kind is "keydown" or "keyup") && (command.VKey is < 1 or > 254)) throw new InvalidDataException("Invalid virtual key.");
        if (command.Kind == "scroll" && (command.Delta is < -12000 or > 12000)) throw new InvalidDataException("Scroll delta exceeds the permitted bound.");
    }
    public static Point MapPoint(Rectangle bounds, double x, double y)
    {
        if (bounds.Width < 1 || bounds.Height < 1 || !double.IsFinite(x) || !double.IsFinite(y) || x is < 0 or > 1 || y is < 0 or > 1) throw new InvalidDataException("Invalid input bounds or coordinates.");
        return new Point(checked(bounds.Left + (int)Math.Round(x * (bounds.Width - 1), MidpointRounding.AwayFromZero)), checked(bounds.Top + (int)Math.Round(y * (bounds.Height - 1), MidpointRounding.AwayFromZero)));
    }
    public static Rectangle SelectBounds(IReadOnlyList<Rectangle> monitors, Rectangle virtualBounds, int monitorIndex)
    {
        if (monitorIndex < -1 || monitorIndex >= monitors.Count) throw new InvalidDataException("Selected monitor is unavailable.");
        var bounds = monitorIndex < 0 ? virtualBounds : monitors[monitorIndex];
        if (bounds.Width < 1 || bounds.Height < 1) throw new InvalidDataException("Selected monitor has no desktop bounds.");
        return bounds;
    }
    private static void Move(RemoteInputCmd command, int monitorIndex)
    {
        var point = MapPoint(SelectBounds(Screen.AllScreens.Select(screen => screen.Bounds).ToArray(), ScreenCapture.VirtualBounds, monitorIndex), command.X, command.Y);
        if (!Native.SetCursorPos(point.X, point.Y)) throw new Win32Exception(Marshal.GetLastPInvokeError());
    }
    private static uint ButtonFlag(int button, bool down) => button switch
    {
        0 => down ? Native.MOUSEEVENTF_LEFTDOWN : Native.MOUSEEVENTF_LEFTUP,
        1 => down ? Native.MOUSEEVENTF_RIGHTDOWN : Native.MOUSEEVENTF_RIGHTUP,
        2 => down ? Native.MOUSEEVENTF_MIDDLEDOWN : Native.MOUSEEVENTF_MIDDLEUP,
        _ => throw new InvalidDataException("Invalid pointer button.")
    };
}
