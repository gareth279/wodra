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
	public const float DummyPanelWidth = 330;
	public const float DummyPanelHeight = 144;
	public const float DummyBottomOffset = 108;

	public static RideHudOverlayLayout ForViewport(float width, float height, bool showRouteInset)
	{
		width = MathF.Max(width, DebugPanelWidth + RouteInsetWidth + DummyPanelWidth + PanelGap * 2 + ViewportMargin * 2);
		height = MathF.Max(height, 360);
		var debug = new RideHudOverlayBounds(
			ViewportMargin, ViewportMargin, DebugPanelWidth,
			MathF.Max(200, height - DebugBottomClearance - ViewportMargin));
		var inset = new RideHudOverlayBounds(
			width - ViewportMargin - RouteInsetWidth, RouteInsetTop, RouteInsetWidth, RouteInsetHeight);
		var dummy = showRouteInset
			? new RideHudOverlayBounds(inset.X - PanelGap - DummyPanelWidth, RouteInsetTop, DummyPanelWidth, DummyPanelHeight)
			: new RideHudOverlayBounds(width - ViewportMargin - DummyPanelWidth,
				height - DummyBottomOffset - DummyPanelHeight, DummyPanelWidth, DummyPanelHeight);
		return new(debug, inset, dummy);
	}
}
