using System;

/// <summary>Persisted preferences for optional ride HUD panels.</summary>
public sealed class RideDisplayPreferences
{
	public bool RouteGradientInsetVisible { get; set; } = true;
}

/// <summary>Pure layout bounds shared by ride dialogs and regression checks.</summary>
public static class RideMenuLayoutPolicy
{
	public const float ViewportMargin = 24;
	public static string EndRideConfirmationText(bool hasActiveRecording, int savedSegmentCount)
	{
		if (hasActiveRecording)
			return "The active FIT recording will be finalized when you end this ride.";
		if (savedSegmentCount > 0)
			return $"No FIT recording is active. Your {savedSegmentCount} saved FIT {(savedSegmentCount == 1 ? "segment" : "segments")} will be kept.";
		return "FIT recording is off. Ending this ride will not create a FIT file.";
	}

	public static (float Width, float Height) PanelSize(float viewportWidth, float viewportHeight,
		float contentWidth, float contentHeight)
	{
		var width = Math.Min(Math.Max(300, contentWidth + 40), Math.Max(260, viewportWidth - ViewportMargin));
		var height = Math.Min(Math.Max(150, contentHeight + 36), Math.Max(130, viewportHeight - ViewportMargin));
		return (width, height);
	}
}

