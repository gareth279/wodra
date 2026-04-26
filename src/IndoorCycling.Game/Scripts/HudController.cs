using Godot;
using System;
using IndoorCycling.Core.Models;

public partial class HudController : Control
{
	private Label? _modeLabel;
	private Label? _statusLabel;
	private Label? _powerLabel;
	private Label? _cadenceLabel;
	private Label? _speedLabel;
	private Label? _gradientLabel;
	private Label? _distanceLabel;
	private Label? _elapsedLabel;

	public override void _Ready()
	{
		_modeLabel = GetNodeOrNull<Label>("VBoxContainer/ModeLabel");
		_statusLabel = GetNodeOrNull<Label>("VBoxContainer/StatusLabel");
		_powerLabel = GetNodeOrNull<Label>("VBoxContainer/PowerLabel");
		_cadenceLabel = GetNodeOrNull<Label>("VBoxContainer/CadenceLabel");
		_speedLabel = GetNodeOrNull<Label>("VBoxContainer/SpeedLabel");
		_gradientLabel = GetNodeOrNull<Label>("VBoxContainer/GradientLabel");
		_distanceLabel = GetNodeOrNull<Label>("VBoxContainer/DistanceLabel");
		_elapsedLabel = GetNodeOrNull<Label>("VBoxContainer/ElapsedLabel");
	}

	public void SetModeText(string mode)
	{
		if (_modeLabel is not null)
			_modeLabel.Text = $"Mode: {mode}";
	}

	public void SetStatus(string status)
	{
		if (_statusLabel is not null)
			_statusLabel.Text = $"Status: {status}";
	}

	public void UpdateFromRideState(RideState rideState, TrainerTelemetry telemetry, double elapsedSeconds)
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

		if (_elapsedLabel is not null)
			_elapsedLabel.Text = $"Elapsed: {TimeSpan.FromSeconds(elapsedSeconds):hh\\:mm\\:ss}";
	}
}
