namespace IndoorCycling.Core.Models;

public enum RideActivityStatus { InProgress, Completed, Abandoned }

public sealed record RiderProfileRecord(Guid Id, string Name, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc);

public sealed record RideActivity(
    Guid Id, Guid RiderProfileId, string RouteId, string RouteName,
    DateTimeOffset StartedAtUtc, DateTimeOffset? CompletedAtUtc, RideActivityStatus Status,
    double DurationSeconds, double? MovingSeconds, double DistanceMeters, int CompletedLaps,
    double? AverageSpeedKph, double? MaxSpeedKph,
    double? AveragePowerWatts, double? MaxPowerWatts,
    double? AverageCadenceRpm, double? MaxCadenceRpm,
    double ElevationGainMeters, string? FitFilePath, DateTimeOffset CreatedAtUtc);

public sealed record RideActivityMedia(
    Guid Id, Guid ActivityId, string ImagePath, string? SidecarPath,
    DateTimeOffset CapturedAtUtc, string CaptureMode, int? MarkerNumber, string MediaType = "image/png");

public sealed record RideActivityDraft(
    Guid Id, Guid RiderProfileId, string RouteId, string RouteName,
    DateTimeOffset StartedAtUtc, RideActivityStatus Status,
    double DurationSeconds, double DistanceMeters, int CompletedLaps,
    double? AverageSpeedKph, double? MaxSpeedKph,
    double? AveragePowerWatts, double? MaxPowerWatts,
    double? AverageCadenceRpm, double? MaxCadenceRpm,
    double ElevationGainMeters, string? FitFilePath);

/// <summary>Accumulates time weighted metrics from live ride frames without changing simulation state.</summary>
public sealed class RideActivityAccumulator
{
    private double _activeSeconds, _speedSeconds, _speedTime, _powerSeconds, _powerTime, _cadenceSeconds, _cadenceTime;
    private double? _maxSpeed, _maxPower, _maxCadence;
    private double? _elevationGainMeters;

    public void Reset()
    {
        _activeSeconds = _speedSeconds = _speedTime = _powerSeconds = _powerTime = _cadenceSeconds = _cadenceTime = 0;
        _maxSpeed = _maxPower = _maxCadence = null;
        _elevationGainMeters = null;
    }

    public void AddFrame(double activeDeltaSeconds, double speedKph, int? powerWatts, int? cadenceRpm, double? cumulativeElevationGainMeters = null)
    {
        if (!double.IsFinite(activeDeltaSeconds) || activeDeltaSeconds <= 0) return;
        if (cumulativeElevationGainMeters is { } elevation && double.IsFinite(elevation) && elevation >= 0)
            _elevationGainMeters = Math.Max(_elevationGainMeters ?? 0, elevation);
        _activeSeconds += activeDeltaSeconds;
        if (double.IsFinite(speedKph) && speedKph is >= 0 and <= 150)
        {
            _speedTime += speedKph * activeDeltaSeconds;
            _speedSeconds += activeDeltaSeconds;
            _maxSpeed = Math.Max(_maxSpeed ?? 0, speedKph);
        }
        if (powerWatts is >= 1 and <= 2500)
        {
            _powerTime += powerWatts.Value * activeDeltaSeconds;
            _powerSeconds += activeDeltaSeconds;
            _maxPower = Math.Max(_maxPower ?? 0, powerWatts.Value);
        }
        if (cadenceRpm is >= 1 and <= 250)
        {
            _cadenceTime += cadenceRpm.Value * activeDeltaSeconds;
            _cadenceSeconds += activeDeltaSeconds;
            _maxCadence = Math.Max(_maxCadence ?? 0, cadenceRpm.Value);
        }
    }

    public RideActivityDraft CreateDraft(Guid id, Guid riderProfileId, string routeId, string routeName,
        DateTimeOffset startedAtUtc, double distanceMeters, double routeLengthMeters,
        double elevationGainMeters, string? fitFilePath)
    {
        static double? Mean(double total, double seconds) => seconds > 0 ? total / seconds : null;
        return new RideActivityDraft(id, riderProfileId, routeId, routeName, startedAtUtc,
            RideActivityStatus.Completed, _activeSeconds, Math.Max(0, distanceMeters),
            routeLengthMeters > 0 ? (int)Math.Floor(Math.Max(0, distanceMeters) / routeLengthMeters) : 0,
            Mean(_speedTime, _speedSeconds), _maxSpeed, Mean(_powerTime, _powerSeconds), _maxPower,
            Mean(_cadenceTime, _cadenceSeconds), _maxCadence,
            Math.Max(0, _elevationGainMeters ?? (double.IsFinite(elevationGainMeters) ? elevationGainMeters : 0)), fitFilePath);
    }
}
