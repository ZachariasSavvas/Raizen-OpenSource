using System.Reflection;

namespace Raizen.Endpoint.Tray;

/// <summary>
/// Loads the Raizen brand icon from the embedded resource.
/// Works reliably in single-file publish where loose files are not available.
/// </summary>
internal static class BrandIcon
{
    private static Icon? _cached;

    /// <summary>Returns the Raizen icon, or null if unavailable.</summary>
    public static Icon? Get()
    {
        if (_cached is not null) return _cached;

        try
        {
            var stream = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream("Raizen.ico");
            if (stream is not null)
                _cached = new Icon(stream);
        }
        catch { }

        return _cached;
    }

    /// <summary>Draws the 32x32 brand icon centered in a 48x48 badge area.</summary>
    public static void DrawHeaderBadge(Graphics g, int badgeX, int badgeY)
        => DrawHeaderBadge(g, badgeX, badgeY, 1.0f);

    /// <summary>Draws the brand icon badge scaled by the given DPI factor.</summary>
    public static void DrawHeaderBadge(Graphics g, int badgeX, int badgeY, float scaleFactor)
    {
        int badgeSize = (int)(48 * scaleFactor);
        int iconSize  = (int)(32 * scaleFactor);
        int offset    = (int)(8  * scaleFactor);

        using var badgeBrush = new SolidBrush(Color.FromArgb(60, 255, 255, 255));
        g.FillEllipse(badgeBrush, badgeX, badgeY, badgeSize, badgeSize);

        var icon = Get();
        if (icon is not null)
        {
            using var sized = new Icon(icon, iconSize, iconSize);
            using var bmp = sized.ToBitmap();
            g.DrawImage(bmp, new RectangleF(badgeX + offset, badgeY + offset, iconSize, iconSize));
        }
    }
}
