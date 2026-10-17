namespace IndoorCycling.Core.Models;

/// <summary>Pixel bounds used to safely restore the last normal window rectangle.</summary>
public readonly record struct WindowRectangle(int X, int Y, int Width, int Height)
{
    public long VisibleAreaWithin(WindowRectangle bounds)
    {
        var left = Math.Max(X, bounds.X);
        var top = Math.Max(Y, bounds.Y);
        var right = Math.Min((long)X + Width, (long)bounds.X + bounds.Width);
        var bottom = Math.Min((long)Y + Height, (long)bounds.Y + bounds.Height);
        return Math.Max(0, right - left) * Math.Max(0, bottom - top);
    }
}

public static class WindowPlacementPolicy
{
    public static int SelectScreen(int savedScreen, WindowRectangle savedRectangle,
        IReadOnlyList<WindowRectangle> usableScreens)
    {
        if (usableScreens.Count == 0) return 0;
        if (savedScreen >= 0 && savedScreen < usableScreens.Count) return savedScreen;

        var bestScreen = 0;
        var bestArea = savedRectangle.VisibleAreaWithin(usableScreens[0]);
        for (var i = 1; i < usableScreens.Count; i++)
        {
            var area = savedRectangle.VisibleAreaWithin(usableScreens[i]);
            if (area > bestArea) { bestArea = area; bestScreen = i; }
        }
        return bestScreen;
    }

    public static WindowRectangle Clamp(WindowRectangle requested, WindowRectangle usableScreen,
        int minimumWidth = 960, int minimumHeight = 540)
    {
        var width = Math.Clamp(requested.Width, Math.Min(minimumWidth, usableScreen.Width), usableScreen.Width);
        var height = Math.Clamp(requested.Height, Math.Min(minimumHeight, usableScreen.Height), usableScreen.Height);
        var maxX = usableScreen.X + usableScreen.Width - width;
        var maxY = usableScreen.Y + usableScreen.Height - height;
        return new WindowRectangle(Math.Clamp(requested.X, usableScreen.X, maxX),
            Math.Clamp(requested.Y, usableScreen.Y, maxY), width, height);
    }
}
