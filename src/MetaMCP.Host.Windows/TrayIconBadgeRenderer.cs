using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace MetaMCP.Host;

internal static class TrayIconBadgeRenderer
{
    private const int IconSize = 32;

    public static Icon CreateIcon(Icon baseIcon, int connectionCount)
    {
        if (connectionCount <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(connectionCount),
                "A badge is only rendered for a positive connection count.");
        }

        var badgeText = connectionCount <= 99
            ? connectionCount.ToString(
                System.Globalization.CultureInfo.InvariantCulture)
            : "99+";
        return CreateIconCore(baseIcon, badgeText);
    }

    private static Icon CreateIconCore(Icon baseIcon, string badgeText)
    {
        using var bitmap = new Bitmap(
            IconSize,
            IconSize,
            PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        ConfigureGraphics(graphics);
        graphics.Clear(Color.Transparent);
        graphics.DrawIcon(baseIcon, new Rectangle(0, 0, IconSize, IconSize));

        var badgeBounds = GetBadgeBounds(badgeText);
        using var badgePath = CreateRoundedRectangle(
            badgeBounds,
            Math.Min(badgeBounds.Width, badgeBounds.Height) / 2f);
        using var badgeBrush = new SolidBrush(Color.FromArgb(230, 211, 47, 47));
        using var outlinePen = new Pen(Color.White, 1.25f);
        graphics.FillPath(badgeBrush, badgePath);
        graphics.DrawPath(outlinePen, badgePath);

        var fontSize = badgeText.Length switch
        {
            1 => 12f,
            2 => 10f,
            _ => 8f,
        };
        var fontFamily = SystemFonts.MessageBoxFont?.FontFamily
            ?? FontFamily.GenericSansSerif;
        using var font = new Font(
            fontFamily,
            fontSize,
            FontStyle.Bold,
            GraphicsUnit.Pixel);
        TextRenderer.DrawText(
            graphics,
            badgeText,
            font,
            Rectangle.Round(badgeBounds),
            Color.White,
            Color.Transparent,
            TextFormatFlags.HorizontalCenter |
            TextFormatFlags.VerticalCenter |
            TextFormatFlags.SingleLine |
            TextFormatFlags.NoPadding);

        var handle = bitmap.GetHicon();
        if (handle == IntPtr.Zero)
        {
            throw new InvalidOperationException("Could not create tray icon handle.");
        }

        try
        {
            return (Icon)Icon.FromHandle(handle).Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    private static void ConfigureGraphics(Graphics graphics)
    {
        graphics.CompositingMode = CompositingMode.SourceOver;
        graphics.CompositingQuality = CompositingQuality.HighQuality;
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
    }

    private static RectangleF GetBadgeBounds(string badgeText) =>
        badgeText.Length switch
        {
            1 => new RectangleF(16f, 0f, 16f, 16f),
            2 => new RectangleF(12f, 0f, 20f, 15f),
            _ => new RectangleF(8f, 0f, 24f, 14f),
        };

    private static GraphicsPath CreateRoundedRectangle(
        RectangleF bounds,
        float radius)
    {
        var diameter = radius * 2f;
        var path = new GraphicsPath();
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180f, 90f);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270f, 90f);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0f, 90f);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90f, 90f);
        path.CloseFigure();
        return path;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr iconHandle);
}
