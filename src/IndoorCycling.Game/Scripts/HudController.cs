using System;
using Godot;
using IndoorCycling.Core.Models;

public partial class HudController : Control
{
	private Label? _modeLabel;

	private Label? _routeLabel;
	private Label? _segmentLabel;
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
	private PanelContainer? _debugOverlay;
	private Label? _debugSnapshotLabel;
	private bool _debugModeEnabled;
	private bool _debugOverlayVisible = true;

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
		_routeLabel = GetNodeOrNull<Label>("TopHudPanel/MarginContainer/VBoxContainer/RouteLabel");
		_segmentLabel = GetNodeOrNull<Label>("TopHudPanel/MarginContainer/VBoxContainer/SegmentLabel");

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
		_rideSessionNode?.ToggleRidePause();
	}

	public void SetRidePaused(bool paused)
	{
		if (_recordPauseButton is not null)
			_recordPauseButton.Text = paused ? "Resume" : "Pause";
	}

	private void OnRecordStopPressed()
	{
		_rideSessionNode?.StopRideRecording();
	}

	private void OnSaveToStravaPressed()
	{
		_rideSessionNode?.SaveLastRecordingToStrava();
	}

	public void SetDebugMode(bool enabled)
	{
		_debugModeEnabled = enabled;
		if (enabled) EnsureDebugOverlay();
		if (_debugOverlay is not null) _debugOverlay.Visible = enabled && _debugOverlayVisible;
		if (_modeLabel is not null && _modeLabel.Text.Length > 0)
			_modeLabel.Text = _modeLabel.Text.Replace(" [DEBUG]", "") + (enabled ? " [DEBUG]" : "");
	}

	private void EnsureDebugOverlay()
	{
		if (_debugOverlay is not null) return;
		_debugOverlay = new PanelContainer
		{
			Name = "DebugRideOverlay",
			AnchorLeft = 0,
			AnchorRight = 0,
			AnchorTop = 0,
			AnchorBottom = 0,
			OffsetLeft = 12,
			OffsetRight = 365,
			OffsetTop = 12,
			OffsetBottom = 274
		};
		var layout = new VBoxContainer();
		_debugSnapshotLabel = new Label { Text = "DEBUG\nWaiting for ride state…", AutowrapMode = TextServer.AutowrapMode.WordSmart };
		layout.AddChild(_debugSnapshotLabel);
		var actions = new HBoxContainer();
		var markButton = new Button { Text = "MARK" };
		markButton.Pressed += () => _rideSessionNode?.WriteDebugMarker();
		actions.AddChild(markButton);
		actions.AddChild(new Label { Text = "Jump to m" });
		var jumpDistance = new LineEdit { Name = "JumpDistance", PlaceholderText = "5830", CustomMinimumSize = new Vector2(82, 0) };
		actions.AddChild(jumpDistance);
		var jumpButton = new Button { Text = "Go" };
		jumpButton.Pressed += () => _rideSessionNode?.JumpToRouteDistance(jumpDistance.Text);
		actions.AddChild(jumpButton);
		layout.AddChild(actions);
		_debugOverlay.AddChild(layout);
		AddChild(_debugOverlay);
		_debugOverlay.Visible = _debugModeEnabled && _debugOverlayVisible;
	}

	public void UpdateDebugOverlay(RideDebugSnapshot snapshot)
	{
		if (!_debugModeEnabled || _debugSnapshotLabel is null) return;
		_debugSnapshotLabel.Text =
			$"DEBUG\nVirtual: {snapshot.virtualSpeedKph:F1} km/h\n" +
			$"Trainer: {snapshot.rawTrainerSpeedKph?.ToString("F1") ?? "-"} km/h  Power: {snapshot.rawTrainerPowerWatts?.ToString() ?? "-"} W\n" +
			$"Ride: {snapshot.totalRideDistanceMeters:F1} m\n" +
			$"Wrapped: {snapshot.wrappedRouteDistanceMeters:F1} / {snapshot.routeLengthMeters:F0} m  Lap: {snapshot.lapNumber}\n" +
			$"Gradient: {snapshot.routeGradientPercent:+0.0;-0.0;0.0}%\n" +
			$"Sample: {snapshot.visualSampleBefore} -> {snapshot.visualSampleAfter} ({snapshot.visualInterpolation:F2})\n" +
			$"Paused: {(snapshot.ridePaused ? "Yes" : "No")}  Recording: {snapshot.fitRecordingState}\n" +
			$"FTMS cmd: {snapshot.requestedTrainerGradientPercent?.ToString("+0.0;-0.0;0.0") ?? "-"}%";
	}

	public override void _UnhandledKeyInput(InputEvent @event)
	{
		if (!_debugModeEnabled || @event is not InputEventKey key || !key.Pressed || key.Echo) return;
		if (key.Keycode == Key.F8 && _rideSessionNode?.IsRideActive == true)
		{
			_rideSessionNode.WriteDebugMarker();
			GetViewport().SetInputAsHandled();
		}
		else if (key.Keycode == Key.F3 && _rideSessionNode?.IsRideActive == true)
		{
			_debugOverlayVisible = !_debugOverlayVisible;
			if (_debugOverlay is not null) _debugOverlay.Visible = _debugOverlayVisible;
			GetViewport().SetInputAsHandled();
		}
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
			_modeLabel.Text = $"Mode: {mode}" + (_debugModeEnabled ? " [DEBUG]" : "");
	}

	public void SetStatus(string status)
	{
		if (_statusLabel is not null)
			_statusLabel.Text = $"Status: {status}";
	}

	public void ResetHud()
	{
		SetRidePaused(false);
		SetModeText("-");
		SetStatus("Choose a mode to begin.");

		if (_routeLabel is not null) _routeLabel.Text = "Route: -";
		if (_segmentLabel is not null) _segmentLabel.Text = "Segment: -";

		if (_powerLabel is not null) _powerLabel.Text = "Power: -";
		if (_cadenceLabel is not null) _cadenceLabel.Text = "Cadence: -";
		if (_speedLabel is not null) _speedLabel.Text = "Speed: -";
		if (_gradientLabel is not null) _gradientLabel.Text = "Gradient: -";
		if (_distanceLabel is not null) _distanceLabel.Text = "Distance: -";
		if (_elapsedLabel is not null) _elapsedLabel.Text = "Elapsed: 00:00:00";
	}

	public void SetRouteInfo(string routeName, string segmentName, double loopDistanceMeters, double routeDistanceMeters)
	{
		if (_routeLabel is not null)
		{
			_routeLabel.Text = $"Route: {routeName} ({loopDistanceMeters / 1000.0:0.00}/{routeDistanceMeters / 1000.0:0.00} km)";
		}

		if (_segmentLabel is not null)
		{
			_segmentLabel.Text = $"Segment: {segmentName}";
		}
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
