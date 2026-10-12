using System;
using System.Collections.Generic;
using IndoorCycling.Core.Models;

/// <summary>Small, game-independent state for debug snapshot timing and route jumps.</summary>
public sealed class DebugRideDiagnostics
{
    public const double SnapshotIntervalSeconds = 0.5;
    private double _snapshotAccumulator;
    private int _markerNumber;

    public bool Enabled { get; private set; }
    public string? SessionId { get; private set; }

    public void Begin(bool enabled)
    {
        Enabled = enabled;
        SessionId = enabled ? Guid.NewGuid().ToString("N") : null;
        _snapshotAccumulator = 0;
        _markerNumber = 0;
    }

    public bool SnapshotDue(double deltaSeconds)
    {
        if (!Enabled) return false;
        _snapshotAccumulator += Math.Max(0, deltaSeconds);
        if (_snapshotAccumulator < SnapshotIntervalSeconds) return false;
        _snapshotAccumulator %= SnapshotIntervalSeconds;
        return true;
    }

    public int NextMarkerNumber() => Enabled ? ++_markerNumber : 0;

    public RideDebugSnapshot CreateSnapshot(RideDebugSnapshot state, DateTimeOffset? timestamp = null, int? marker = null)
    {
        if (!Enabled || SessionId is null) throw new InvalidOperationException("Debug diagnostics are disabled.");
        var length = state.routeLengthMeters;
        var wrapped = length > 0 ? ((state.totalRideDistanceMeters % length) + length) % length : 0;
        var lap = length > 0 ? (int)Math.Floor(Math.Max(0, state.totalRideDistanceMeters) / length) + 1 : 0;
        return state with
        {
            timestamp = timestamp ?? DateTimeOffset.UtcNow,
            debugSessionId = SessionId,
            wrappedRouteDistanceMeters = wrapped,
            lapNumber = lap,
            marker = marker
        };
    }

    public bool TryApplyDistanceJump(RideState current, double distanceMeters, out RideState updated)
    {
        updated = current;
        if (!Enabled || !double.IsFinite(distanceMeters) || distanceMeters < 0) return false;
        updated = current with { DistanceMeters = distanceMeters, VirtualSpeedKph = 0 };
        return true;
    }
}

/// <summary>Compact JSONL payload. Lowercase property names intentionally serialize as-is.</summary>
public sealed record RideDebugSnapshot
{
    public DateTimeOffset timestamp { get; init; }
    public string? debugSessionId { get; init; }
    public int? marker { get; init; }
    public string trainerMode { get; init; } = "unknown";
    public string rideState { get; init; } = "running";
    public bool ridePaused { get; init; }
    public string fitRecordingState { get; init; } = "inactive";
    public double elapsedRidingSeconds { get; init; }
    public Guid profileId { get; init; }
    public string? profileName { get; init; }
    public double bodyWeightKg { get; init; }
    public double bikeWeightKg { get; init; }
    public string? trainerKind { get; init; }
    public string? dummyMode { get; init; }
    public double? dummyWattsPerKg { get; init; }
    public int? dummyTargetWatts { get; init; }
    public Guid? ghostActivityId { get; init; }
    public double? ghostElapsedSeconds { get; init; }
    public double? ghostTotalDistanceMeters { get; init; }
    public double? ghostWrappedDistanceMeters { get; init; }
    public int? ghostLap { get; init; }
    public double? ghostRelativeDistanceMeters { get; init; }
    public double? ghostSpeedKph { get; init; }
    public int? ghostPowerWatts { get; init; }
    public int? ghostCadenceRpm { get; init; }
    public bool? ghostFinished { get; init; }
    public bool? trainerConnected { get; init; }
    public int? rawTrainerPowerWatts { get; init; }
    public int? rawTrainerCadenceRpm { get; init; }
    public double? rawTrainerSpeedKph { get; init; }
    public DateTimeOffset? telemetryTimestamp { get; init; }
    public double? telemetryAgeSeconds { get; init; }
    public double virtualSpeedKph { get; init; }
    public double totalRideDistanceMeters { get; init; }
    public int currentPowerWatts { get; init; }
    public double totalSystemMassKg { get; init; }
    public double routeGradientPercent { get; init; }
    public double manualGradientOffsetPercent { get; init; }
    public string? routeId { get; init; }
    public string? routeName { get; init; }
    public double routeLengthMeters { get; init; }
    public double wrappedRouteDistanceMeters { get; init; }
    public int lapNumber { get; init; }
    public string? routeSegment { get; init; }
    public double? routeLatitude { get; init; }
    public double? routeLongitude { get; init; }
    public double? routeElevationMeters { get; init; }
    public double? rawRouteElevationMeters { get; init; }
    public double? visualRelativeElevationMeters { get; init; }
    public int nearbyLandmarkCount { get; init; }
    public string? nearestLandmarkName { get; init; }
    public double? nearestLandmarkRouteDistanceMeters { get; init; }
    public DebugVector3? nearestLandmarkWorldPosition { get; init; }
    public double? nearestLandmarkDistanceMeters { get; init; }
    public int buildingsLoaded { get; init; }
    public int visibleBuildingChunks { get; init; }
    public int vegetationAreas { get; init; }
    public int treesLoaded { get; init; }
    public int visibleVegetationChunks { get; init; }
    public int streetFurnitureLoaded { get; init; }
    public int nearbyStreetFurniture { get; init; }
    public int streetFurnitureInsideRideRoad { get; init; }
    public int streetFurnitureProxiesSuppressed { get; init; }
    public int streetFurnitureProxiesRelocatedRoadside { get; init; }
    public int streetFurnitureIntentionallyRetainedInRoad { get; init; }
    public double riderLateralOffsetMeters { get; init; }
    public long? nearestStreetFurnitureOsmId { get; init; }
    public string? nearestStreetFurnitureCategory { get; init; }
    public DebugVector3? nearestStreetFurnitureSourcePosition { get; init; }
    public DebugVector3? nearestStreetFurnitureVisualPosition { get; init; }
    public bool? nearestStreetFurnitureRelocated { get; init; }
    public int backgroundStreetsLoaded { get; init; }
    public int visibleStreetChunks { get; init; }
    public int ambientCars { get; init; }
    public int ambientCarTarget { get; init; }
    public int ambientPedestrians { get; init; }
    public int ambientPedestrianTarget { get; init; }
    public int ambientEligibleCarPaths { get; init; }
    public int ambientEligiblePedestrianPaths { get; init; }
    public long ambientCarSpawnAttempts { get; init; }
    public long ambientPedestrianSpawnAttempts { get; init; }
    public long ambientCarOutsideSpawnRadius { get; init; }
    public long ambientPedestrianOutsideSpawnRadius { get; init; }
    public long ambientCarTooCloseToRider { get; init; }
    public long ambientPedestrianTooCloseToRider { get; init; }
    public long ambientCarNoEligiblePath { get; init; }
    public long ambientPedestrianNoValidPath { get; init; }
    public long ambientCarInvalidTransform { get; init; }
    public long ambientPedestrianInvalidTransform { get; init; }
    public long ambientCarIllegalOnewayDirections { get; init; }
    public int ambientTotalStreetPolylines { get; init; }
    public int ambientEligibleStreetPolylines { get; init; }
    public int ambientCarInvalidOrShortPaths { get; init; }
    public int ambientPedestrianInvalidOrShortPaths { get; init; }
    public int ambientPedestrianSeamOrEndSamplesExcluded { get; init; }
    public DebugVector3? firstAmbientCarPosition { get; init; }
    public double? firstAmbientCarDistanceMeters { get; init; }
    public string? firstAmbientCarRoadName { get; init; }
    public string? firstAmbientCarRoadClass { get; init; }
    public double? firstAmbientCarSpeedMetersPerSecond { get; init; }
    public bool? firstAmbientCarVisible { get; init; }
    public DebugVector3? firstAmbientPedestrianPosition { get; init; }
    public double? firstAmbientPedestrianDistanceMeters { get; init; }
    public double? firstAmbientPedestrianSpeedMetersPerSecond { get; init; }
    public bool? firstAmbientPedestrianVisible { get; init; }
    public int aiRiderCount { get; init; }
    public string? nearestAiRiderName { get; init; }
    public double? nearestAiRiderRelativeDistanceMeters { get; init; }
    public double? nearestAiRiderSpeedKph { get; init; }
    public IReadOnlyList<AiRiderDebugSnapshot> nearestAiRiders { get; init; } = Array.Empty<AiRiderDebugSnapshot>();
	public IReadOnlyList<RemoteHumanRiderDebugSnapshot> remoteHumanRiders { get; init; } = Array.Empty<RemoteHumanRiderDebugSnapshot>();
    public int terrainNearChunks { get; init; }
    public int terrainFarChunks { get; init; }
    public int terrainVisibleChunks { get; init; }
    public double terrainNearSpacingMeters { get; init; }
    public double terrainFarSpacingMeters { get; init; }
    public int visualSampleBefore { get; init; }
    public int visualSampleAfter { get; init; }
    public double visualInterpolation { get; init; }
    public DebugVector3 visualRoutePosition { get; init; }
    public DebugVector3 visualTangent { get; init; }
    public DebugVector3 riderWorldPosition { get; init; }
    public DebugVector3 riderForward { get; init; }
    public DebugVector3 riderRotation { get; init; }
    public DebugVector3? cameraWorldPosition { get; init; }
    public double? requestedTrainerGradientPercent { get; init; }
    public double? successfulTrainerGradientPercent { get; init; }
    public double? secondsSinceTrainerCommand { get; init; }
    public bool trainerCommandInFlight { get; init; }
}

public sealed record AiRiderDebugSnapshot(string name, double relativeDistanceMeters,
    double routeDistanceMeters, double speedKph, int powerWatts, int cadenceRpm);

public sealed record RemoteHumanRiderDebugSnapshot(string riderId, string name, bool hasState,
	double? relativeDistanceMeters, double? stateAgeSeconds, bool visualExists, bool visualVisible,
	bool withinVisualRange, bool? behindCamera, bool? insideCameraFrustum);

public readonly record struct DebugVector3(float x, float y, float z);
