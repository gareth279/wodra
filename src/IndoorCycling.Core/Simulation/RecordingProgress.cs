namespace IndoorCycling.Core.Simulation;

/// <summary>Accumulates only distance and elevation observed while recording is active.</summary>
public sealed class RecordingProgress
{
    private double? _lastRawDistance;
    private bool _hasRouteElevation;
    public double DistanceMeters { get; private set; }
    public double AltitudeMeters { get; private set; }
    public double AscentMeters { get; private set; }
    public double DescentMeters { get; private set; }

    public void Rebase(double rawDistanceMeters, double? routeElevationMeters)
    {
        _lastRawDistance = rawDistanceMeters;
        AltitudeMeters = routeElevationMeters ?? AltitudeMeters;
        _hasRouteElevation = routeElevationMeters.HasValue;
    }

    public double Advance(double rawDistanceMeters, double gradientPercent, double? routeElevationMeters)
    {
        if (_lastRawDistance is null)
        {
            Rebase(rawDistanceMeters, routeElevationMeters);
            return 0;
        }

        var distanceDelta = Math.Max(0, rawDistanceMeters - _lastRawDistance.Value);
        _lastRawDistance = rawDistanceMeters;
        DistanceMeters += distanceDelta;
        var elevationDelta = routeElevationMeters is { } elevation
            ? (_hasRouteElevation ? elevation - AltitudeMeters : 0)
            : distanceDelta * gradientPercent / 100.0;
        AltitudeMeters = routeElevationMeters ?? (AltitudeMeters + elevationDelta);
        _hasRouteElevation = routeElevationMeters.HasValue;
        AscentMeters += Math.Max(0, elevationDelta);
        DescentMeters += Math.Max(0, -elevationDelta);
        return elevationDelta;
    }
}
