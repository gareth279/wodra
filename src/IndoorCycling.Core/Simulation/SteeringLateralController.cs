using IndoorCycling.Core.Models;

namespace IndoorCycling.Core.Simulation;

/// <summary>Converts intentional steering input into a bounded route-relative rider offset.</summary>
public sealed class SteeringLateralController
{
    public const double NormalLineMeters = 1.75;
    public const double FallbackRoadWidthMeters = 5.0;
    public const double RiderMarginMeters = 0.45;
    public const double MaximumLateralSpeedMetersPerSecond = 1.25;
    public const double KeyboardLateralSpeedMetersPerSecond = MaximumLateralSpeedMetersPerSecond * 1.2;
    public const double DefaultDeadZoneDegrees = 3.0;
    public const double DefaultSensitivity = SteeringControlSettings.DefaultSensitivity;
    public const double MinimumSensitivity = SteeringControlSettings.MinimumSensitivity;
    public const double MaximumSensitivity = SteeringControlSettings.MaximumSensitivity;
    private const double MaximumCommandAngleDegrees = 30.0;
    private const double VelocitySmoothingSeconds = 0.16;

    private double _velocityMetersPerSecond;
    private bool _keyboardOwnsSteering;

    public double LateralOffsetMeters { get; private set; } = NormalLineMeters;
    public bool KeyboardOwnsSteering => _keyboardOwnsSteering;

    public double Update(double deltaSeconds, bool leftKeyDown, bool rightKeyDown,
        float? centeredControllerAngleDegrees, bool controllerInputFresh, bool paused,
        bool textInputFocused = false, double sensitivity = DefaultSensitivity,
        double deadZoneDegrees = DefaultDeadZoneDegrees, double? roadWidthMeters = null,
        bool controllerInputIsNew = true)
    {
        if (!double.IsFinite(deltaSeconds) || deltaSeconds < 0) throw new ArgumentOutOfRangeException(nameof(deltaSeconds));
        sensitivity = double.IsFinite(sensitivity) ? Math.Clamp(sensitivity, MinimumSensitivity, MaximumSensitivity) : DefaultSensitivity;
        deadZoneDegrees = double.IsFinite(deadZoneDegrees) ? Math.Clamp(deadZoneDegrees, 0, 12) : DefaultDeadZoneDegrees;
        var width = roadWidthMeters is { } supplied && double.IsFinite(supplied) && supplied > RiderMarginMeters * 2
            ? supplied : FallbackRoadWidthMeters;
        var maximumOffset = Math.Max(0, width / 2 - RiderMarginMeters);
        var minimumOffset = -maximumOffset;
        var step = Math.Min(deltaSeconds, 0.1);

        if (paused)
        {
            _velocityMetersPerSecond = 0;
            return LateralOffsetMeters;
        }

        var keyboardInputAllowed = !textInputFocused;
        var keyboardActive = keyboardInputAllowed && (leftKeyDown || rightKeyDown);
        // RiderLaneMath's positive coordinate points route-left.
        var keyboardAxis = keyboardActive ? (rightKeyDown == leftKeyDown ? 0 : leftKeyDown ? 1 : -1) : 0;
        if (keyboardActive) _keyboardOwnsSteering = true;

        var controllerFresh = controllerInputFresh && centeredControllerAngleDegrees is { } angle && float.IsFinite(angle);
        var intentionalController = controllerFresh && Math.Abs(centeredControllerAngleDegrees!.Value) > deadZoneDegrees
            && (!_keyboardOwnsSteering || controllerInputIsNew);
        if (_keyboardOwnsSteering && intentionalController) _keyboardOwnsSteering = false;

        double targetVelocity;
        if (keyboardActive)
        {
            targetVelocity = keyboardAxis * KeyboardLateralSpeedMetersPerSecond * sensitivity;
        }
        else if (_keyboardOwnsSteering)
        {
            // Releasing a key holds position. A new, non-neutral controller report can take over.
            targetVelocity = 0;
        }
        else if (controllerFresh)
        {
            var magnitude = Math.Abs(centeredControllerAngleDegrees!.Value);
            var normalized = magnitude <= deadZoneDegrees ? 0
                : Math.Clamp((magnitude - deadZoneDegrees) / Math.Max(1, MaximumCommandAngleDegrees - deadZoneDegrees), 0, 1)
                    * -Math.Sign(centeredControllerAngleDegrees.Value);
            targetVelocity = normalized * MaximumLateralSpeedMetersPerSecond * sensitivity;
        }
        else
        {
            // No fresh source: return gradually to the normal line.
            var difference = NormalLineMeters - LateralOffsetMeters;
            targetVelocity = Math.Sign(difference) * MaximumLateralSpeedMetersPerSecond
                * Math.Min(1, Math.Abs(difference) / 0.75);
        }

        var smoothing = 1 - Math.Exp(-step / VelocitySmoothingSeconds);
        _velocityMetersPerSecond += (targetVelocity - _velocityMetersPerSecond) * smoothing;
        var next = Math.Clamp(LateralOffsetMeters + _velocityMetersPerSecond * step, minimumOffset, maximumOffset);
        if (next == minimumOffset || next == maximumOffset) _velocityMetersPerSecond = 0;
        if (targetVelocity == 0 && (controllerFresh || _keyboardOwnsSteering))
            next = LateralOffsetMeters; // Neutral steering is a hold command.
        LateralOffsetMeters = next;
        return next;
    }

    public void Reset(double lateralOffsetMeters = NormalLineMeters)
    {
        LateralOffsetMeters = double.IsFinite(lateralOffsetMeters) ? lateralOffsetMeters : NormalLineMeters;
        _velocityMetersPerSecond = 0;
        _keyboardOwnsSteering = false;
    }
}
