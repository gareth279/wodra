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
	RideHudOverlayBounds WorkoutPanel);

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
	public const float WorkoutPanelWidth = 350;
	public const float WorkoutPanelHeight = 204;

	public static RideHudOverlayLayout ForViewport(float width, float height, bool showRouteInset,
		bool showPacerPanel = false, float pacerWidth = 0, float pacerHeight = 0,
		bool showDummyPanel = false, float bottomBarWidth = 0, float bottomBarHeight = 0,
		bool showMediaPanel = false, float mediaWidth = 0, float mediaHeight = 0, bool showDebugPanel = false,
		float topHudBottom = 0, bool showWorkoutPanel = false, float workoutWidth = 0, float workoutHeight = 0)
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
		var workoutPanelWidth = MathF.Min(workoutWidth > 0 ? workoutWidth : WorkoutPanelWidth,
			MathF.Max(1, width - ViewportMargin * 2));
		var workoutPanelHeight = workoutHeight > 0 ? workoutHeight : WorkoutPanelHeight;
		var workout = showWorkoutPanel
			? new RideHudOverlayBounds(width - ViewportMargin - workoutPanelWidth,
				topHudBottom + PanelGap, workoutPanelWidth, workoutPanelHeight)
			: default;
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
		if (showDummyPanel && showMediaPanel)
		{
			// The media panel stays beside the action bar in the bottom lane. Move
			// optional dummy controls above that lane when their current placement
			// would intersect it; their own ScrollContainer handles reduced height.
			var laneRight = bar.Right + PanelGap + mediaWidth;
			var laneTop = height - BottomPanelMargin - MathF.Max(bottomBarHeight, mediaHeight);
			if (dummy.X < laneRight && dummy.Right > bar.X && dummy.Bottom > laneTop - PanelGap)
			{
				var dummyLeft = showDebugPanel ? debug.Right + PanelGap : ViewportMargin;
				var dummyRight = showPacerPanel ? pacer.X - PanelGap : width - ViewportMargin;
				if (showRouteInset) dummyRight = MathF.Min(dummyRight, inset.X - PanelGap);
				var availableWidth = MathF.Max(0, dummyRight - dummyLeft);
				dummyWidth = MathF.Min(DummyPanelWidth, availableWidth);
				var minTop = MathF.Max(ViewportMargin, topHudBottom + PanelGap);
				dummyHeight = MathF.Min(DummyPanelHeight, MathF.Max(0, laneTop - PanelGap - minTop));
				dummy = new RideHudOverlayBounds(dummyLeft, laneTop - PanelGap - dummyHeight,
					dummyWidth, dummyHeight);
			}
		}
		return new(debug, inset, dummy, pacer, bar, workout);
	}
}
