using System;

/// <summary>Viewport-relative bounds for debug details, route inset, and dummy controls.</summary>
public readonly record struct RideHudOverlayBounds(float X, float Y, float Width, float Height)
{
	public float Right => X + Width;
	public float Bottom => Y + Height;
}

public readonly record struct RideHudOverlayLayout(
	RideHudOverlayBounds DebugPanel,
	RideHudOverlayBounds RouteInset,
	RideHudOverlayBounds DummyPanel);

public static class RideHudOverlayLayoutPolicy
{
	public const float ViewportMargin = 12;
	public const float DebugPanelWidth = 300;
	public const float DebugBottomClearance = 108;
	public const float RouteInsetWidth = 280;
	public const float RouteInsetHeight = 230;
	public const float RouteInsetTop = 222;
	public const float PanelGap = 12;
	public const float DummyPanelWidth = RouteInsetWidth;
	public const float DummyPanelHeight = 144;

	public static RideHudOverlayLayout ForViewport(float width, float height, bool showRouteInset)
	{
		width = MathF.Max(width, ViewportMargin * 2 + 1);
		height = MathF.Max(height, ViewportMargin * 2 + 1);
		var debug = new RideHudOverlayBounds(
			ViewportMargin, ViewportMargin, DebugPanelWidth,
			MathF.Max(200, height - DebugBottomClearance - ViewportMargin));
		var insetWidth = MathF.Min(RouteInsetWidth, width - ViewportMargin * 2);
		var inset = new RideHudOverlayBounds(
			width - ViewportMargin - insetWidth, RouteInsetTop, insetWidth, RouteInsetHeight);
		// Reserve the inset's complete rectangle even when it is hidden so the dummy controls
		// never drift into the riding view when the inset visibility changes.
		var dummyWidth = MathF.Min(DummyPanelWidth, width - ViewportMargin * 2);
		var dummyTop = inset.Bottom + PanelGap;
		var dummyHeight = MathF.Min(DummyPanelHeight, MathF.Max(0, height - ViewportMargin - dummyTop));
		var dummy = new RideHudOverlayBounds(width - ViewportMargin - dummyWidth,
			dummyTop, dummyWidth, dummyHeight);
		return new(debug, inset, dummy);
	}
}
