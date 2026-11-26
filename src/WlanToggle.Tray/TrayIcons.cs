using System.Drawing.Drawing2D;

namespace WlanToggle.Tray;

/// <summary>Draws the notification-area icons (a Wi-Fi glyph: green = connected, red + slash = disconnected).</summary>
internal static class TrayIcons
{
    public static Icon Connected { get; } = Draw(Color.FromArgb(46, 160, 67), slash: false);
    public static Icon Disconnected { get; } = Draw(Color.FromArgb(214, 48, 49), slash: true);

    private static Icon Draw(Color color, bool slash)
    {
        const int size = 32;
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var pen = new Pen(color, 3.2f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            const float cx = size / 2f, cy = 27f;
            foreach (var r in new[] { 7f, 13f, 19f })
                g.DrawArc(pen, cx - r, cy - r, 2 * r, 2 * r, 225, 90);
            using var brush = new SolidBrush(color);
            g.FillEllipse(brush, cx - 2.8f, cy - 2.8f, 5.6f, 5.6f);
            if (slash)
            {
                using var white = new Pen(Color.White, 6f);
                using var red = new Pen(color, 3.2f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                g.DrawLine(white, 4, 4, 28, 28);
                g.DrawLine(red, 4, 4, 28, 28);
            }
        }
        // Icon.FromHandle does not own the handle; clone it so the native handle can be released.
        var hIcon = bmp.GetHicon();
        try { return (Icon)Icon.FromHandle(hIcon).Clone(); }
        finally { DestroyIcon(hIcon); }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);
}
