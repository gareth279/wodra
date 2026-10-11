using System;
using System.Threading.Tasks;
using Godot;

public partial class RideMenuPanelController : Control
{
	private enum MenuView { Closed, Main, Settings, Display, Confirmation }

	private RideSessionNode? _rideSessionNode;
	private HudController? _hudController;
	private RideWorldView? _rideWorldView;
	private Control? _startPanel;
	private RouteSelectionPanelController? _routeSelectionPanel;
	private RideHistoryPanelController? _rideHistoryPanel;
	private VBoxContainer? _content;
	private Button? _keepRidingButton;
	private Button? _endRideButton;
	private bool _endingRide;
	private MenuView _view;

	public bool IsMenuVisible => _view != MenuView.Closed;

	public override void _Ready()
	{
		_rideSessionNode = GetNodeOrNull<RideSessionNode>("../RideSessionNode");
		_hudController = GetNodeOrNull<HudController>("../HudController");
		_rideWorldView = GetNodeOrNull<RideWorldView>("../RideWorldView");
		_startPanel = GetNodeOrNull<Control>("../StartPanel");
		_routeSelectionPanel = GetNodeOrNull<RouteSelectionPanelController>("../RouteSelectionPanel");
		_rideHistoryPanel = GetNodeOrNull<RideHistoryPanelController>("../RideHistoryPanel");
		_content = GetNode<VBoxContainer>("PanelContainer/VBoxContainer");
		Hide();
	}

	public void ShowInRideMenu()
	{
		if (_rideSessionNode?.IsRideActive != true || _endingRide) return;
		ShowView(MenuView.Main);
	}

	public void ToggleInRideMenu()
	{
		if (_view == MenuView.Closed) ShowInRideMenu();
		else if (_view == MenuView.Main) CloseMenu();
		else if (_view == MenuView.Confirmation) ShowView(MenuView.Main);
		else ShowView(MenuView.Main);
	}

	public void ShowEndRideConfirmation()
	{
		if (_rideSessionNode?.IsRideActive != true || _endingRide) return;
		ShowView(MenuView.Confirmation);
		GameLogger.Log("end_ride_confirmation_shown", new { wasPaused = _rideSessionNode.IsRidePaused });
	}

	private void CloseMenu()
	{
		_view = MenuView.Closed;
		Hide();
	}

	private void ShowView(MenuView view)
	{
		if (_content is null) return;
		_view = view;
		foreach (var child in _content.GetChildren())
		{
			_content.RemoveChild(child);
			child.QueueFree();
		}
		_content.AddThemeConstantOverride("separation", 10);
		var title = new Label
		{
			Text = view switch
			{
				MenuView.Main => "Ride Menu",
				MenuView.Settings => "Settings",
				MenuView.Display => "HUD & Display",
				_ => "End this ride?"
			},
			HorizontalAlignment = HorizontalAlignment.Center
		};
		title.AddThemeColorOverride("font_color", new Color(0.93f, 0.83f, 0.67f, 1));
		title.AddThemeFontSizeOverride("font_size", 20);
		_content.AddChild(title);

		switch (view)
		{
			case MenuView.Main:
				AddAction("Continue Riding", CloseMenu);
				AddAction("Settings", () => ShowView(MenuView.Settings));
				AddAction("HUD & Display", () => ShowView(MenuView.Display));
				AddAction("End Ride", ShowEndRideConfirmation);
				break;
			case MenuView.Settings:
				#if DEBUG
				_content.AddChild(new Label { Text = "Debug mode", HorizontalAlignment = HorizontalAlignment.Center });
				var debugMode = new CheckButton { Text = "Enable debug mode", ButtonPressed = GameBootstrap.Settings.DebugModeEnabled };
				debugMode.Toggled += enabled =>
				{
					GameBootstrap.Settings.DebugModeEnabled = enabled;
					GameBootstrap.Settings.Save();
					_hudController?.SetDebugMode(enabled);
				};
				_content.AddChild(debugMode);
				#endif
				if (GameBootstrap.TrainerClient is IndoorCycling.Trainer.Implementations.FakeTrainerClient { HasLiveDummyControls: true })
					AddDummyTrainerSettings();
				AddAction("Back", () => ShowView(MenuView.Main));
				break;
			case MenuView.Display:
				var showHud = new CheckButton { Text = "Show riding HUD", ButtonPressed = _hudController?.HudDisplayEnabled ?? true };
				showHud.Toggled += enabled => _hudController?.SetHudDisplayEnabled(enabled);
				_content.AddChild(showHud);
				#if DEBUG
				var showDebug = new CheckButton { Text = "Show debug overlay", ButtonPressed = _hudController?.DebugDisplayEnabled ?? false };
				showDebug.Toggled += enabled => _hudController?.SetDebugDisplayEnabled(enabled);
				_content.AddChild(showDebug);
				#endif
				_content.AddChild(new Label { Text = "Camera view", HorizontalAlignment = HorizontalAlignment.Center });
				var cameraView = new OptionButton();
				foreach (var cameraMode in RiderCameraViewCycle.Views)
					cameraView.AddItem(RideWorldView.FormatCameraView(cameraMode), (int)cameraMode);
				cameraView.Select(cameraView.GetItemIndex((int)(_rideWorldView?.CurrentCameraView ?? RiderCameraView.GameplayRefined)));
				cameraView.ItemSelected += index => _rideWorldView?.SelectCameraInspectionView(
					(RiderCameraView)cameraView.GetItemId((int)index));
				_content.AddChild(cameraView);
				AddAction("Back", () => ShowView(MenuView.Main));
				break;
			case MenuView.Confirmation:
				_content.AddChild(new Label
				{
					Text = "Your ride will be saved automatically as a FIT file.",
					HorizontalAlignment = HorizontalAlignment.Center,
					AutowrapMode = TextServer.AutowrapMode.WordSmart
				});
				_keepRidingButton = AddAction("Keep Riding", () => ShowView(MenuView.Main));
				_endRideButton = AddAction("End Ride", OnEndRideButtonPressed);
				_endRideButton.FocusMode = Control.FocusModeEnum.None;
				break;
		}
		Show();
		MoveToFront();
	}

	private void AddDummyTrainerSettings()
	{
		_content!.AddChild(new Label { Text = "Dummy trainer", HorizontalAlignment = HorizontalAlignment.Center });
		var settings = GameBootstrap.Settings.DummyTrainer;
		var power = new Label { HorizontalAlignment = HorizontalAlignment.Center };
		void UpdatePowerLabel() => power.Text = settings.Mode == "W/kg"
			? $"Target: {settings.WattsPerKg:0.0} W/kg · {settings.CalculateWatts(GameBootstrap.RiderProfile.BodyWeightKg)} W"
			: $"Target: {settings.TargetWatts} W";
		var powerRow = new HBoxContainer();
		var powerStep = settings.Mode == "W/kg" ? .1 : 25;
		powerRow.AddChild(new Label { Text = "Power", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
		var powerDown = new Button { Text = "−", CustomMinimumSize = new Vector2(44, 0) };
		powerDown.Pressed += () => { _rideSessionNode?.AdjustDummyPower(-powerStep); UpdatePowerLabel(); };
		powerRow.AddChild(powerDown);
		powerRow.AddChild(power);
		var powerUp = new Button { Text = "+", CustomMinimumSize = new Vector2(44, 0) };
		powerUp.Pressed += () => { _rideSessionNode?.AdjustDummyPower(powerStep); UpdatePowerLabel(); };
		powerRow.AddChild(powerUp);
		_content.AddChild(powerRow);
		UpdatePowerLabel();

		var cadenceRow = new HBoxContainer();
		cadenceRow.AddChild(new Label { Text = "Cadence", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
		var cadence = new Label { Text = $"{settings.CadenceRpm} rpm", HorizontalAlignment = HorizontalAlignment.Center };
		var cadenceDown = new Button { Text = "−5", CustomMinimumSize = new Vector2(44, 0) };
		cadenceDown.Pressed += () => { _rideSessionNode?.AdjustDummyCadence(-5); cadence.Text = $"{GameBootstrap.Settings.DummyTrainer.CadenceRpm} rpm"; };
		cadenceRow.AddChild(cadenceDown);
		cadenceRow.AddChild(cadence);
		var cadenceUp = new Button { Text = "+5", CustomMinimumSize = new Vector2(44, 0) };
		cadenceUp.Pressed += () => { _rideSessionNode?.AdjustDummyCadence(5); cadence.Text = $"{GameBootstrap.Settings.DummyTrainer.CadenceRpm} rpm"; };
		cadenceRow.AddChild(cadenceUp);
		_content.AddChild(cadenceRow);
	}

	private Button AddAction(string text, Action action)
	{
		var button = new Button { Text = text, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
		button.Pressed += action;
		_content!.AddChild(button);
		return button;
	}

	private void OnEndRideButtonPressed()
	{
		_ = EndRideAndShowSummaryAsync();
	}

	public async Task EndRideAndShowSummaryAsync()
	{
		if (_endingRide || _rideSessionNode?.IsRideActive != true) return;
		_endingRide = true;
		if (_keepRidingButton is not null) _keepRidingButton.Disabled = true;
		if (_endRideButton is not null) _endRideButton.Disabled = true;
		try { await _rideSessionNode.ExitToRouteSelectionAsync(); }
		catch (Exception ex)
		{
			_endingRide = false;
			if (_keepRidingButton is not null) _keepRidingButton.Disabled = false;
			if (_endRideButton is not null) _endRideButton.Disabled = false;
			GD.PushWarning($"Could not end ride: {ex.Message}");
			return;
		}
		_endingRide = false;
		CloseMenu();
		_hudController?.Hide();
		if (_rideSessionNode?.LastCompletedActivityDraft is { } draft)
		{
			_rideHistoryPanel?.ShowCompletedSummary(draft, _rideSessionNode.LastCompletedActivityId,
				_rideSessionNode.LastRecordingError);
			return;
		}
		_startPanel?.Show();
		if (GameBootstrap.TrainerClient is not null) _routeSelectionPanel?.ShowRouteSelection();
		else _startPanel?.Show();
	}
}
