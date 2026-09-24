using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using System.Globalization;

namespace TapQueue.Client.Linux;

/// <summary>Draws the tray icon (the same "TQ" tile as on Windows) so the program needs no image files.</summary>
internal static class TrayIconImage
{
    public static WindowIcon Create(bool connected)
    {
        const int size = 64;
        using var bitmap = new RenderTargetBitmap(new PixelSize(size, size));
        using (var g = bitmap.CreateDrawingContext())
        {
            var fill = new SolidColorBrush(connected ? Color.FromRgb(0x1F, 0x6F, 0xEB) : Color.FromRgb(0x80, 0x80, 0x80));
            g.DrawRectangle(fill, null, new RoundedRect(new Rect(2, 2, size - 4, size - 4), 14));
            var text = new FormattedText("TQ", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.Bold), 26, Brushes.White);
            g.DrawText(text, new Point((size - text.Width) / 2, (size - text.Height) / 2));
        }
        using var png = new MemoryStream();
        bitmap.Save(png, new PngBitmapEncoderOptions());
        png.Position = 0;
        return new WindowIcon(png);
    }
}
