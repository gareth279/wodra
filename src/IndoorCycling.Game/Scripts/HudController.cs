using Godot;
using IndoorCycling.Core.Models;

namespace IndoorCycling.Game.Scripts;

public partial class HudController : Control
{
    [Export] public NodePath? PowerLabelPath { get; set; }
    [Export] public NodePath? CadenceLabelPath { get; set; }
    [Export] public NodePath? SpeedLabelPath { get; set; }
    [Export] public NodePath? GradientLabelPath { get; set; }
    [Export] public NodePath? DistanceLabelPath { get; set; }

    private Label? _powerLabel;
    private Label? _cadenceLabel;
    private Label? _speedLabel;
    private Label? _gradientLabel;
    private Label? _distanceLabel;

    public override void _Ready()
    {
        _powerLabel = GetNodeOrNull<Label>(PowerLabelPath);
        _cadenceLabel = GetNodeOrNull<Label>(CadenceLabelPath);
        _speedLabel = GetNodeOrNull<Label>(SpeedLabelPath);
        _gradientLabel = GetNodeOrNull<Label>(GradientLabelPath);
        _distanceLabel = GetNodeOrNull<Label>(DistanceLabelPath);
    }

    public void UpdateFromRideState(RideState rideState, TrainerTelemetry telemetry)
    {
        if (_powerLabel is not null)
            _powerLabel.Text = $"Power: {telemetry.PowerWatts} W";

        if (_cadenceLabel is not null)
            _cadenceLabel.Text = $"Cadence: {telemetry.CadenceRpm} rpm";

        if (_speedLabel is not null)
            _speedLabel.Text = $"Speed: {rideState.VirtualSpeedKph:0.0} km/h";

        if (_gradientLabel is not null)
            _gradientLabel.Text = $"Gradient: {rideState.CurrentGradientPercent:0.0} %";

        if (_distanceLabel is not null)
            _distanceLabel.Text = $"Distance: {(rideState.DistanceMeters / 1000.0):0.00} km";
    }
}