using System.Drawing.Drawing2D;

namespace ClassMonitor.Teacher.Presentation;

public enum CommandGlyph { View, Message, Lock, Unlock, Present, Files, Settings, Enroll, Start, Stop, Power, Report }

/// <summary>Original vector-derived raster icons, drawn in code without third-party assets.</summary>
public static class CommandIcons
{
    public static Bitmap Create(CommandGlyph glyph, Color color, int size = 20)
    {
        if (size < 16 || size > 96) throw new ArgumentOutOfRangeException(nameof(size));
        var bitmap = new Bitmap(size, size);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.ScaleTransform(size / 24f, size / 24f);
        using var pen = new Pen(color, 1.7f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        using var brush = new SolidBrush(color);
        switch (glyph)
        {
            case CommandGlyph.View:
                g.DrawBezier(pen, 2, 12, 7, 3, 17, 3, 22, 12);
                g.DrawBezier(pen, 2, 12, 7, 21, 17, 21, 22, 12);
                g.DrawEllipse(pen, 9, 9, 6, 6);
                break;
            case CommandGlyph.Message:
                g.DrawPolygon(pen, new[] { new Point(3, 4), new Point(21, 4), new Point(21, 17), new Point(10, 17), new Point(5, 21), new Point(5, 17), new Point(3, 17) });
                g.DrawLine(pen, 7, 9, 17, 9); g.DrawLine(pen, 7, 13, 14, 13);
                break;
            case CommandGlyph.Lock:
            case CommandGlyph.Unlock:
                g.DrawRectangle(pen, 5, 10, 14, 11);
                g.DrawArc(pen, glyph == CommandGlyph.Lock ? 7 : 10, 2, 10, 13, 180, 180);
                g.FillEllipse(brush, 11, 14, 2, 2); g.DrawLine(pen, 12, 15, 12, 18);
                break;
            case CommandGlyph.Present:
                g.DrawRectangle(pen, 3, 3, 18, 13); g.DrawLine(pen, 12, 16, 12, 21); g.DrawLine(pen, 7, 21, 17, 21);
                g.DrawLines(pen, new[] { new Point(7, 12), new Point(11, 8), new Point(14, 11), new Point(18, 7) });
                break;
            case CommandGlyph.Files:
                g.DrawPolygon(pen, new[] { new Point(2, 6), new Point(9, 6), new Point(11, 9), new Point(22, 9), new Point(20, 20), new Point(3, 20) });
                g.DrawLine(pen, 3, 11, 20, 11);
                break;
            case CommandGlyph.Settings:
                g.DrawEllipse(pen, 7, 7, 10, 10); g.DrawEllipse(pen, 10, 10, 4, 4);
                for (int i = 0; i < 8; i++)
                {
                    double angle = i * Math.PI / 4;
                    g.DrawLine(pen, (float)(12 + 6 * Math.Cos(angle)), (float)(12 + 6 * Math.Sin(angle)), (float)(12 + 9 * Math.Cos(angle)), (float)(12 + 9 * Math.Sin(angle)));
                }
                break;
            case CommandGlyph.Enroll:
                g.DrawRectangle(pen, 2, 3, 15, 15); g.DrawLine(pen, 6, 22, 13, 22); g.DrawLine(pen, 9, 18, 9, 22);
                g.DrawLine(pen, 20, 12, 20, 20); g.DrawLine(pen, 16, 16, 23, 16);
                break;
            case CommandGlyph.Start:
                g.FillPolygon(brush, new[] { new Point(7, 3), new Point(21, 12), new Point(7, 21) });
                break;
            case CommandGlyph.Stop:
                g.FillRectangle(brush, 5, 5, 14, 14);
                break;
            case CommandGlyph.Power:
                g.DrawArc(pen, 4, 5, 16, 16, -60, 300); g.DrawLine(pen, 12, 2, 12, 12);
                break;
            case CommandGlyph.Report:
                g.DrawLine(pen, 3, 2, 3, 21); g.DrawLine(pen, 3, 21, 22, 21);
                g.FillRectangle(brush, 7, 13, 3, 5); g.FillRectangle(brush, 12, 8, 3, 10); g.FillRectangle(brush, 17, 4, 3, 14);
                break;
            default: bitmap.Dispose(); throw new ArgumentOutOfRangeException(nameof(glyph));
        }
        return bitmap;
    }

    public static bool TryResolve(string englishCaption, out CommandGlyph glyph)
    {
        string text = UiText.Normalize(englishCaption);
        CommandGlyph? match = text switch
        {
            "Live view" => CommandGlyph.View,
            "Message" => CommandGlyph.Message,
            "Lock" => CommandGlyph.Lock,
            "Unlock" => CommandGlyph.Unlock,
            "Broadcast my screen" or "Show selected student" => CommandGlyph.Present,
            "Files" => CommandGlyph.Files,
            "Settings" or "Settings center" => CommandGlyph.Settings,
            "Enroll device" or "Connect manually" => CommandGlyph.Enroll,
            "Start lesson" => CommandGlyph.Start,
            "End lesson" => CommandGlyph.Stop,
            "Power" => CommandGlyph.Power,
            "Monitoring" => CommandGlyph.Report,
            _ => null
        };
        glyph = match ?? default;
        return match.HasValue;
    }
}
