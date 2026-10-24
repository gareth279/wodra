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
	RideHudOverlayBounds DummyPanel,
	RideHudOverlayBounds PacerPanel,
	RideHudOverlayBounds BottomBar,
	RideHudOverlayBounds MediaPanel);

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
	public const float BottomPanelMargin = 12;
	public const float MediaPanelHeight = 58;
	public const float MediaPanelMinimumWidth = 246;

	public static RideHudOverlayLayout ForViewport(float width, float height, bool showRouteInset,
		bool showPacerPanel = false, float pacerWidth = 0, float pacerHeight = 0,
		bool showDummyPanel = false, float bottomBarWidth = 0, float bottomBarHeight = 0,
		bool showMediaPanel = false, float mediaWidth = 0, float mediaHeight = 0, bool showDebugPanel = false,
		float topHudBottom = 0)
	{
		width = MathF.Max(width, ViewportMargin * 2 + 1);
		height = MathF.Max(height, ViewportMargin * 2 + 1);
		var debug = new RideHudOverlayBounds(
			ViewportMargin, ViewportMargin, DebugPanelWidth,
			MathF.Max(200, height - DebugBottomClearance - ViewportMargin));
		var insetWidth = MathF.Min(RouteInsetWidth, width - ViewportMargin * 2);
		var insetHeight = showPacerPanel ? RouteInsetHeight - PanelGap : RouteInsetHeight;
		var insetTop = MathF.Max(RouteInsetTop, topHudBottom + PanelGap);
		var inset = new RideHudOverlayBounds(
			width - ViewportMargin - insetWidth, insetTop, insetWidth, insetHeight);
		// Reserve the inset's complete rectangle even when it is hidden so the dummy controls
		// never drift into the riding view when the inset visibility changes.
		var dummyWidth = MathF.Min(DummyPanelWidth, width - ViewportMargin * 2);
		var dummyTop = inset.Bottom + PanelGap;
		var dummyHeight = MathF.Min(DummyPanelHeight, MathF.Max(0, height - ViewportMargin - dummyTop));
		var dummy = new RideHudOverlayBounds(width - ViewportMargin - dummyWidth,
			dummyTop, dummyWidth, dummyHeight);

		var bar = new RideHudOverlayBounds(ViewportMargin, height - BottomPanelMargin - bottomBarHeight,
			bottomBarWidth, bottomBarHeight);
		var pacer = new RideHudOverlayBounds(width - ViewportMargin - pacerWidth,
			height - BottomPanelMargin - pacerHeight, pacerWidth, pacerHeight);
		if (showPacerPanel && showDummyPanel)
		{
			// The bottom-right pacer gets the right edge. Place dummy controls in the
			// remaining bottom lane, clear of the debug panel and the content-sized
			// bottom-left action bar. Their own scroll container handles short viewports.
			var dummyLeft = MathF.Max(debug.Right + PanelGap, bar.Right + PanelGap);
			var dummyRight = pacer.X - PanelGap;
			var availableWidth = MathF.Max(0, dummyRight - dummyLeft);
			dummyWidth = MathF.Min(DummyPanelWidth, availableWidth);
			dummyHeight = MathF.Min(DummyPanelHeight, height - BottomPanelMargin * 2);
			dummy = new RideHudOverlayBounds(dummyLeft, height - BottomPanelMargin - dummyHeight,
				dummyWidth, dummyHeight);
		}
		var media = new RideHudOverlayBounds(ViewportMargin, bar.Y - PanelGap - mediaHeight, 0, mediaHeight);
		if (showMediaPanel)
		{
			var mediaLeft = showDebugPanel ? debug.Right + PanelGap : ViewportMargin;
			var mediaRight = showPacerPanel ? pacer.X - PanelGap : width - ViewportMargin;
			var mediaTop = bar.Y - PanelGap - mediaHeight;
			if (showDummyPanel && mediaTop + mediaHeight > dummy.Y - PanelGap)
				mediaTop = dummy.Y - PanelGap - mediaHeight;

			var availableRight = mediaRight;
			if (showRouteInset && mediaTop < inset.Bottom && mediaTop + mediaHeight > inset.Y &&
				mediaLeft < inset.Right && availableRight > inset.X)
				availableRight = MathF.Min(availableRight, inset.X - PanelGap);

			var availableWidth = MathF.Max(0, availableRight - mediaLeft);
			var fittedWidth = MathF.Min(mediaWidth, availableWidth);
			media = new RideHudOverlayBounds(mediaLeft, mediaTop, fittedWidth, mediaHeight);
		}
		return new(debug, inset, dummy, pacer, bar, media);
	}
}
