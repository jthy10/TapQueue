using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace TapQueue.Client.Windows;

/// <summary>Draws the tray icon so the app ships as a single exe with no resource files.</summary>
internal static class TrayIcon
{
    public static Icon Create(bool connected)
    {
        using var bitmap = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            using var path = RoundedRect(new Rectangle(1, 1, 30, 30), 7);
            using var fill = new SolidBrush(connected ? Color.FromArgb(0x1F, 0x6F, 0xEB) : Color.FromArgb(0x80, 0x80, 0x80));
            g.FillPath(fill, path);
            using var font = new Font("Segoe UI", 12, FontStyle.Bold, GraphicsUnit.Pixel);
            TextRenderer.DrawText(g, "TQ", font, new Rectangle(0, 0, 32, 32), Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
        var handle = bitmap.GetHicon();
        try
        {
            return (Icon)Icon.FromHandle(handle).Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    private static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        var path = new GraphicsPath();
        var d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);
}
