using System;
using System.IO;
using System.Text.Json;

public enum ScreenshotCaptureMode
{
    Clean,
    Hud,
    DebugMarker
}

public static class ScreenshotCapturePolicy
{
    public static ScreenshotCaptureMode ForF12(bool shiftPressed) =>
        shiftPressed ? ScreenshotCaptureMode.Hud : ScreenshotCaptureMode.Clean;
    public static ScreenshotCaptureMode ForDebugMarker() => ScreenshotCaptureMode.DebugMarker;
}

public static class ScreenshotCaptureFailurePolicy
{
    public static bool TryCapture(Action capture, Action<Exception> reportFailure)
    {
        try { capture(); return true; }
        catch (Exception ex) { reportFailure(ex); return false; }
    }
}

public sealed record CapturedScreenshot(string ImagePath, string? SidecarPath,
    DateTimeOffset CapturedAt, ScreenshotCaptureMode Mode, int? MarkerNumber, Guid? ActivityId = null);

/// <summary>Compact screenshot metadata; deliberately excludes bulky diagnostic payloads.</summary>
public sealed record ScreenshotMetadata
{
    public DateTimeOffset timestamp { get; init; }
    public int? marker { get; init; }
    public string? routeId { get; init; }
    public string? routeName { get; init; }
    public double totalRideDistanceMeters { get; init; }
    public double wrappedRouteDistanceMeters { get; init; }
    public int lapNumber { get; init; }
    public double virtualSpeedKph { get; init; }
    public double? trainerSpeedKph { get; init; }
    public int? powerWatts { get; init; }
    public int? cadenceRpm { get; init; }
    public double gradientPercent { get; init; }
    public double? elevationMeters { get; init; }
    public int sampleBefore { get; init; }
    public int sampleAfter { get; init; }
    public double interpolation { get; init; }
    public DebugVector3 riderWorldPosition { get; init; }
    public DebugVector3? cameraWorldPosition { get; init; }
    public bool paused { get; init; }
    public string recording { get; init; } = "inactive";
    public string? nearestLandmark { get; init; }
    public double? nearestLandmarkDistanceMeters { get; init; }
    public int nearbyLandmarks { get; init; }
    public int loadedBuildings { get; init; }
    public int visibleBuildingChunks { get; init; }
    public int nearbyVegetationAreas { get; init; }
    public int loadedTrees { get; init; }
    public int visibleVegetationChunks { get; init; }
    public int nearTerrainChunks { get; init; }
    public int farTerrainChunks { get; init; }
    public int visibleTerrainChunks { get; init; }
    public int terrainNearSpacingMeters { get; init; }
    public int terrainFarSpacingMeters { get; init; }
    public int loadedStreetFurniture { get; init; }
    public int nearbyStreetFurniture { get; init; }
    public int loadedBackgroundStreets { get; init; }
    public int visibleStreetChunks { get; init; }

    public static ScreenshotMetadata FromSnapshot(RideDebugSnapshot s) => new()
    {
        timestamp = s.timestamp, marker = s.marker, routeId = s.routeId, routeName = s.routeName,
        totalRideDistanceMeters = s.totalRideDistanceMeters, wrappedRouteDistanceMeters = s.wrappedRouteDistanceMeters,
        lapNumber = s.lapNumber, virtualSpeedKph = s.virtualSpeedKph, trainerSpeedKph = s.rawTrainerSpeedKph,
        powerWatts = s.rawTrainerPowerWatts, cadenceRpm = s.rawTrainerCadenceRpm,
        gradientPercent = s.routeGradientPercent, elevationMeters = s.routeElevationMeters,
        sampleBefore = s.visualSampleBefore, sampleAfter = s.visualSampleAfter, interpolation = s.visualInterpolation,
        riderWorldPosition = s.riderWorldPosition, cameraWorldPosition = s.cameraWorldPosition,
        paused = s.ridePaused, recording = s.fitRecordingState, nearestLandmark = s.nearestLandmarkName,
        nearestLandmarkDistanceMeters = s.nearestLandmarkDistanceMeters, nearbyLandmarks = s.nearbyLandmarkCount,
        loadedBuildings = s.buildingsLoaded, visibleBuildingChunks = s.visibleBuildingChunks,
        nearbyVegetationAreas = s.vegetationAreas, loadedTrees = s.treesLoaded,
        visibleVegetationChunks = s.visibleVegetationChunks, nearTerrainChunks = s.terrainNearChunks,
        farTerrainChunks = s.terrainFarChunks, visibleTerrainChunks = s.terrainVisibleChunks,
        terrainNearSpacingMeters = (int)Math.Round(s.terrainNearSpacingMeters),
        terrainFarSpacingMeters = (int)Math.Round(s.terrainFarSpacingMeters),
        loadedStreetFurniture = s.streetFurnitureLoaded, nearbyStreetFurniture = s.nearbyStreetFurniture,
        loadedBackgroundStreets = s.backgroundStreetsLoaded, visibleStreetChunks = s.visibleStreetChunks
    };
}

/// <summary>Pure naming, directory, and metadata helpers for screenshot capture.</summary>
public static class ScreenshotCaptureFileStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = null };

    public static string CreateScreenshotDirectory(string picturesDirectory, string userDataDirectory)
    {
        if (!string.IsNullOrWhiteSpace(picturesDirectory))
        {
            try
            {
                var primary = Path.Combine(picturesDirectory, "Wodra");
                Directory.CreateDirectory(primary);
                return primary;
            }
            catch (Exception) { /* Try the Godot user-data location below. */ }
        }
        var fallback = Path.Combine(userDataDirectory, "screenshots");
        Directory.CreateDirectory(fallback);
        return fallback;
    }

    public static string CreateUniqueStem(DateTimeOffset timestamp, ScreenshotCaptureMode mode,
        int? markerNumber, Func<string, bool> fileExists)
    {
        var suffix = mode switch
        {
            ScreenshotCaptureMode.DebugMarker => $"-marker-{markerNumber ?? throw new ArgumentException("Marker number is required.", nameof(markerNumber)):000}",
            ScreenshotCaptureMode.Hud => "-hud",
            _ => "-clean"
        };
        var baseName = $"wodra-{timestamp.ToLocalTime():yyyyMMdd-HHmmss}{suffix}";
        for (var collision = 0; ; collision++)
        {
            var candidate = collision == 0 ? baseName : $"{baseName}-{collision:000}";
            if (!fileExists(candidate + ".png") && (mode != ScreenshotCaptureMode.DebugMarker || !fileExists(candidate + ".json")))
                return candidate;
        }
    }

    public static string SerializeSidecar(RideDebugSnapshot snapshot) =>
        JsonSerializer.Serialize(ScreenshotMetadata.FromSnapshot(snapshot), JsonOptions);
}
