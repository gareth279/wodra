using System.Linq;

/// <summary>Viewport-bounded ride-summary panel measurements; screenshot count never changes the frame.</summary>
public static class RideSummaryLayoutPolicy
{
    public const int PreferredWidth = 820;
    public const int PreferredHeight = 580;
    public const int MinimumWidth = 320;
    public const int MinimumHeight = 280;
    public const int ViewportMargin = 32;
    public const int DetailsChromeHeight = 190;

    public static (int Width, int Height) PanelSize(int viewportWidth, int viewportHeight)
    {
        var width = System.Math.Min(PreferredWidth, System.Math.Max(MinimumWidth, viewportWidth - ViewportMargin));
        var height = System.Math.Min(PreferredHeight, System.Math.Max(MinimumHeight, viewportHeight - ViewportMargin));
        return (width, height);
    }

    public static int DetailsViewportHeight(int panelHeight) => System.Math.Max(0, panelHeight - DetailsChromeHeight);

    public static string ScreenshotList(System.Collections.Generic.IEnumerable<string> paths)
    {
        var items = paths.ToArray();
        return $"Screenshots: {items.Length}" + (items.Length == 0 ? "" : "\n" + string.Join("\n", items.Select((path, index) => $"{index + 1}. {path}")));
    }
}
