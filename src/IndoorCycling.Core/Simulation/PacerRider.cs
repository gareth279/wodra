using IndoorCycling.Core.Models;

namespace IndoorCycling.Core.Simulation;

/// <summary>A named fixed-effort offline pacer preset, independent of the player's sensor data.</summary>
public sealed record PacerEffortPreset(string Id, string Name, double TargetWattsPerKg)
{
    public const double AssumedBodyWeightKg = 75;
    public const double AssumedBikeWeightKg = 8;
    public const string DisabledId = "none";

    public int TargetPowerWatts => (int)Math.Round(TargetWattsPerKg * AssumedBodyWeightKg,
        MidpointRounding.AwayFromZero);
    public double TotalMassKg => AssumedBodyWeightKg + AssumedBikeWeightKg;
    public string RiderDisplayName => $"Pacer · {Name}";
    public static string CatTexturePath(string? id) => id?.ToLowerInvariant() switch
    {
        "easy" => "res://Data/Cats/blue-cat.png",
        "moderate" => "res://Data/Cats/gold-cat.png",
        "strong" => "res://Data/Cats/gold-cat.png",
        _ => "res://Data/Cats/blue-cat.png"
    };
}

public static class PacerEffortPresets
{
    private static readonly PacerEffortPreset[] Presets =
    [
        new("easy", "Easy", 1.5),
        new("moderate", "Moderate", 2.2),
        new("strong", "Strong", 3.0)
    ];

    public static IReadOnlyList<PacerEffortPreset> All => Presets;

    public static PacerEffortPreset? Find(string? id) => Presets.FirstOrDefault(preset =>
        string.Equals(preset.Id, id, StringComparison.OrdinalIgnoreCase));

    public static string NormalizeSelection(string? id) => Find(id)?.Id ?? PacerEffortPreset.DisabledId;

    public static string CatTexturePath(string? id) => PacerEffortPreset.CatTexturePath(id);
}

/// <summary>
/// Persistent absolute route progress for a selected offline pacer. It uses the shared cycling
/// road-load model and never reads or writes player physics or telemetry.
/// </summary>
public sealed class PacerRiderController
{
    private string _routeId = string.Empty;
    private double _routeDistanceMeters;
    private double _wrappedDistanceMeters;
    private double _speedMetersPerSecond;
    private double _simulationTimeSeconds;
    private double _lateralOffsetMeters;
    private double _targetLateralOffsetMeters;
    private bool _paused;

    public PacerEffortPreset Preset { get; }
    public string RiderId => $"ai-pacer-{Preset.Id}";
    public bool IsInitialized { get; private set; }
    public RiderState State => new(RiderId, Preset.RiderDisplayName, _routeId,
        _routeDistanceMeters, _wrappedDistanceMeters, LapNumber(_routeDistanceMeters, _routeLengthMeters),
        _lateralOffsetMeters, _speedMetersPerSecond, Preset.TargetPowerWatts, 85,
        _paused, _simulationTimeSeconds);

    private double _routeLengthMeters;

    public PacerRiderController(PacerEffortPreset preset) => Preset = preset;

    public void Start(RiderState player, double routeLengthMeters,
        double initialGapMeters = 25, double? initialLateralOffsetMeters = null)
    {
        if (!double.IsFinite(routeLengthMeters) || routeLengthMeters <= 0)
            throw new ArgumentOutOfRangeException(nameof(routeLengthMeters));
        _routeLengthMeters = routeLengthMeters;
        _routeId = player.RouteId;
        _routeDistanceMeters = Math.Max(0, player.RouteDistanceMeters) + initialGapMeters;
        _speedMetersPerSecond = player.Paused ? 0 : Math.Max(0, player.VirtualSpeedMps);
        _simulationTimeSeconds = Math.Max(0, player.SimulationTimeSeconds);
        _lateralOffsetMeters = initialLateralOffsetMeters ?? player.LateralOffsetMeters;
        _targetLateralOffsetMeters = _lateralOffsetMeters;
        _paused = player.Paused;
        IsInitialized = true;
        UpdateRouteCoordinates();
    }

    public void Reset()
    {
        IsInitialized = false;
        _routeId = string.Empty;
        _routeDistanceMeters = 0;
        _wrappedDistanceMeters = 0;
        _speedMetersPerSecond = 0;
        _simulationTimeSeconds = 0;
        _paused = false;
    }

    public void SetLateralOffset(double targetMeters)
    {
        if (double.IsFinite(targetMeters))
            _targetLateralOffsetMeters = Math.Clamp(targetMeters,
                -RiderTrafficSeparation.MaximumRoadCentreOffsetMeters,
                RiderTrafficSeparation.MaximumRoadCentreOffsetMeters);
    }

    /// <summary>One-time local rendezvous. The route direction and lap count remain continuous.</summary>
    public void BringTo(double playerDistanceMeters, double playerLateralMeters, double aheadMeters = 12)
    {
        if (!IsInitialized || !double.IsFinite(playerDistanceMeters) || !double.IsFinite(playerLateralMeters)) return;
        var target = playerDistanceMeters + Math.Clamp(aheadMeters, 8, 20);
        if (Math.Abs(_routeDistanceMeters - target) <= 2) return;
        var minimumSeparation = RiderTrafficSeparation.MinimumLaneCentreSeparationMeters;
        _routeDistanceMeters = Math.Max(0, target);
        _wrappedDistanceMeters = ((_routeDistanceMeters % _routeLengthMeters) + _routeLengthMeters) % _routeLengthMeters;
        var maximumOffset = RiderTrafficSeparation.MaximumRoadCentreOffsetMeters;
        var safeLanes = new[] { playerLateralMeters + minimumSeparation, playerLateralMeters - minimumSeparation,
            -2.55, -1.53, -0.51, 0.51, 1.53, 2.55 }
            .Where(offset => Math.Abs(offset) <= maximumOffset
                && Math.Abs(offset - playerLateralMeters) >= minimumSeparation - 0.01)
            .OrderBy(offset => Math.Abs(offset - playerLateralMeters))
            .ThenBy(offset => offset < playerLateralMeters ? 1 : 0).ToArray();
        _lateralOffsetMeters = safeLanes.Length > 0 ? safeLanes[0]
            : Math.Clamp(playerLateralMeters, -maximumOffset, maximumOffset);
        _targetLateralOffsetMeters = _lateralOffsetMeters;
    }

    public void Update(double routeLengthMeters, Func<double, double> gradientAt,
        double deltaSeconds, bool paused)
    {
        if (!IsInitialized) return;
        if (!double.IsFinite(routeLengthMeters) || routeLengthMeters <= 0 || gradientAt is null)
            throw new ArgumentOutOfRangeException(nameof(routeLengthMeters));
        _routeLengthMeters = routeLengthMeters;
        _paused = paused;
        if (paused || !double.IsFinite(deltaSeconds) || deltaSeconds <= 0)
        {
            UpdateRouteCoordinates();
            return;
        }

        var remaining = deltaSeconds;
        while (remaining > 0)
        {
            var stepSeconds = Math.Min(remaining, 0.25);
            var gradient = gradientAt(_wrappedDistanceMeters);
            var step = CyclingPhysicsMath.Advance(_speedMetersPerSecond, Preset.TargetPowerWatts,
                gradient, Preset.TotalMassKg, .005, .32, stepSeconds);
            _speedMetersPerSecond = step.SpeedMetersPerSecond;
            _routeDistanceMeters += step.DistanceMeters;
            _simulationTimeSeconds += stepSeconds;
            var lateralStep = RiderTrafficSeparation.MaximumLateralSpeedMetersPerSecond * stepSeconds;
            _lateralOffsetMeters += Math.Clamp(_targetLateralOffsetMeters - _lateralOffsetMeters,
                -lateralStep, lateralStep);
            remaining -= stepSeconds;
            UpdateRouteCoordinates();
        }
    }

    public static double SignedGapMeters(RiderState pacer, RiderState player) =>
        pacer.RouteDistanceMeters - player.RouteDistanceMeters;

    private static int LapNumber(double distanceMeters, double routeLengthMeters) =>
        routeLengthMeters > 0 ? (int)Math.Floor(Math.Max(0, distanceMeters) / routeLengthMeters) + 1 : 1;

    private void UpdateRouteCoordinates()
    {
        _wrappedDistanceMeters = ((_routeDistanceMeters % _routeLengthMeters) + _routeLengthMeters) % _routeLengthMeters;
    }
}

