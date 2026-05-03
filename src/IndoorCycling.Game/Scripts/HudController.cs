using System;
using Godot;
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

	private Button? _menuButton;
	private Button? _gradientUpButton;
	private Button? _gradientDownButton;

	private Button? _recordStartButton;
	private Button? _recordPauseButton;
	private Button? _recordStopButton;
	
	private Button? _saveToStravaButton;
	private RideMenuPanelController? _rideMenuPanel;
	private RideSessionNode? _rideSessionNode;

	public override void _Ready()
	{
		_modeLabel = GetNodeOrNull<Label>("TopHudPanel/MarginContainer/VBoxContainer/ModeLabel");
		_statusLabel = GetNodeOrNull<Label>("TopHudPanel/MarginContainer/VBoxContainer/StatusLabel");
		_powerLabel = GetNodeOrNull<Label>("TopHudPanel/MarginContainer/VBoxContainer/PowerLabel");
		_cadenceLabel = GetNodeOrNull<Label>("TopHudPanel/MarginContainer/VBoxContainer/CadenceLabel");
		_speedLabel = GetNodeOrNull<Label>("TopHudPanel/MarginContainer/VBoxContainer/SpeedLabel");
		_gradientLabel = GetNodeOrNull<Label>("TopHudPanel/MarginContainer/VBoxContainer/GradientLabel");
		_distanceLabel = GetNodeOrNull<Label>("TopHudPanel/MarginContainer/VBoxContainer/DistanceLabel");
		_elapsedLabel = GetNodeOrNull<Label>("TopHudPanel/MarginContainer/VBoxContainer/ElapsedLabel");

		_menuButton = GetNodeOrNull<Button>("BottomBar/MarginContainer/HBoxContainer/MenuButton");
		_gradientDownButton = GetNodeOrNull<Button>("BottomBar/MarginContainer/HBoxContainer/GradientDownButton");
		_gradientUpButton = GetNodeOrNull<Button>("BottomBar/MarginContainer/HBoxContainer/GradientUpButton");
		_recordStartButton = GetNodeOrNull<Button>("BottomBar/MarginContainer/HBoxContainer/RecordStartButton");
		_recordPauseButton = GetNodeOrNull<Button>("BottomBar/MarginContainer/HBoxContainer/RecordPauseButton");
		_recordStopButton = GetNodeOrNull<Button>("BottomBar/MarginContainer/HBoxContainer/RecordStopButton");
		_saveToStravaButton = GetNodeOrNull<Button>("BottomBar/MarginContainer/HBoxContainer/SaveToStravaButton");
		_rideMenuPanel = GetNodeOrNull<RideMenuPanelController>("../RideMenuPanel");
		_rideSessionNode = GetNodeOrNull<RideSessionNode>("../RideSessionNode");

		if (_menuButton is not null)
		{
			_menuButton.Pressed += OnMenuPressed;
		}

		if (_gradientDownButton is not null)
		{
			_gradientDownButton.Pressed += OnGradientDownPressed;
		}

		if (_gradientUpButton is not null)
		{
			_gradientUpButton.Pressed += OnGradientUpPressed;
		}

		if (_recordStartButton is not null)
		{
			_recordStartButton.Pressed += OnRecordStartPressed;
		}

		if (_recordPauseButton is not null)
		{
			_recordPauseButton.Pressed += OnRecordPausePressed;
		}

		if (_recordStopButton is not null)
		{
			_recordStopButton.Pressed += OnRecordStopPressed;
		}
		if (_saveToStravaButton is not null)
		{
			_saveToStravaButton.Pressed += OnSaveToStravaPressed;
		}
	}

	private void OnGradientDownPressed()
	{
		_rideSessionNode?.DecreaseManualGradient();
	}

	private void OnGradientUpPressed()
	{
		_rideSessionNode?.IncreaseManualGradient();
	}

	private void OnRecordStartPressed()
	{
		_rideSessionNode?.StartRideRecording();
	}

	private void OnRecordPausePressed()
	{
		_rideSessionNode?.PauseRideRecording();
	}

	private void OnRecordStopPressed()
	{
		_rideSessionNode?.StopRideRecording();
	}

	private void OnSaveToStravaPressed()
	{
		_rideSessionNode?.SaveLastRecordingToStrava();
	}

	private void OnMenuPressed()
	{
		GameLogger.Log("hud_menu_pressed", new
		{
			rideMenuPanelFound = _rideMenuPanel is not null
		});

		if (_rideMenuPanel is null)
		{
			SetStatus("Ride menu panel not found.");
			return;
		}

		_rideMenuPanel.ShowMenu();
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

	public void ResetHud()
	{
		SetModeText("-");
		SetStatus("Choose a mode to begin.");

		if (_powerLabel is not null) _powerLabel.Text = "Power: -";
		if (_cadenceLabel is not null) _cadenceLabel.Text = "Cadence: -";
		if (_speedLabel is not null) _speedLabel.Text = "Speed: -";
		if (_gradientLabel is not null) _gradientLabel.Text = "Gradient: -";
		if (_distanceLabel is not null) _distanceLabel.Text = "Distance: -";
		if (_elapsedLabel is not null) _elapsedLabel.Text = "Elapsed: 00:00:00";
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
