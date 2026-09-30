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
        using var badgeBrush = new SolidBrush(Color.FromArgb(255, 255, 24, 24));
        using var outlinePen = new Pen(Color.White, 2.25f);
        graphics.FillPath(badgeBrush, badgePath);
        graphics.DrawPath(outlinePen, badgePath);

        var fontSize = badgeText.Length switch
        {
            1 => 21f,
            2 => 16.5f,
            _ => 11f,
        };
        var fontFamily = SystemFonts.MessageBoxFont?.FontFamily
            ?? FontFamily.GenericSansSerif;
        using var font = new Font(
            fontFamily,
            fontSize,
            FontStyle.Bold,
            GraphicsUnit.Pixel);
        using var textBrush = new SolidBrush(Color.White);
        using var textFormat = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            FormatFlags = StringFormatFlags.NoWrap,
        };
        var textBounds = new RectangleF(
            badgeBounds.X,
            badgeBounds.Y - 0.5f,
            badgeBounds.Width,
            badgeBounds.Height + 0.5f);
        graphics.DrawString(badgeText, font, textBrush, textBounds, textFormat);

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
            1 => new RectangleF(6f, 1f, 25f, 25f),
            2 => new RectangleF(2f, 1f, 29f, 23f),
            _ => new RectangleF(0f, 1f, 31f, 21f),
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
