using Godot;
using IndoorCycling.Core.Models;

//namespace IndoorCycling.Game.Scripts;

public partial class HudController : Control
{
	private Label? _powerLabel;
	private Label? _cadenceLabel;
	private Label? _speedLabel;
	private Label? _gradientLabel;
	private Label? _distanceLabel;

	public override void _Ready()
	{
		_powerLabel = GetNodeOrNull<Label>("VBoxContainer/PowerLabel");
		_cadenceLabel = GetNodeOrNull<Label>("VBoxContainer/CadenceLabel");
		_speedLabel = GetNodeOrNull<Label>("VBoxContainer/SpeedLabel");
		_gradientLabel = GetNodeOrNull<Label>("VBoxContainer/GradientLabel");
		_distanceLabel = GetNodeOrNull<Label>("VBoxContainer/DistanceLabel");
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
