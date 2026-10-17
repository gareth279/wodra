using System;
using System.Linq;
using System.Threading.Tasks;
using System.IO;
using Godot;
using IndoorCycling.Core.Models;
using IndoorCycling.Trainer.Models;

public partial class RideMenuPanelController : Control
{
	private enum MenuView { Closed, Main, Settings, Devices, AntSetup, Display, Multiplayer, CatchUpConfirmation, Confirmation }

	private RideSessionNode? _rideSessionNode;
	private HudController? _hudController;
	private RideWorldView? _rideWorldView;
	private Control? _startPanel;
	private RouteSelectionPanelController? _routeSelectionPanel;
	private RideHistoryPanelController? _rideHistoryPanel;
	private DeviceConnectionPanelController? _deviceConnectionPanel;
	private VBoxContainer? _content;
	private PanelContainer? _menuPanel;
	private Button? _keepRidingButton;
	private Button? _endRideButton;
	private bool _endingRide;
	private bool _settingsReturnToConnection;
	private MenuView _view;
	private int _layoutRevision;
	private int _viewRevision;
	private Label? _multiplayerStatusLabel;
	private Label? _multiplayerRidersLabel;
	private Label? _routePresenceLabel;
	private Button? _multiplayerJoinButton;
	private Button? _multiplayerLeaveButton;
	private Button? _onlineToggleButton;
	private CheckButton? _advancedMultiplayerToggle;
	private VBoxContainer? _advancedMultiplayerContainer;
	private LineEdit? _multiplayerAddressInput;
	private LineEdit? _multiplayerNameInput;
	private LineEdit? _multiplayerRoomInput;
	private OptionButton? _multiplayerRiderSelect;
	private Button? _catchUpButton;
	private ScrollContainer? _menuScroll;
	private string? _catchUpRiderId;
	private bool _catchUpReturnsToMultiplayer;
	private string _riderOptionIds = "";

	public bool IsMenuVisible => _view != MenuView.Closed;

	public override void _Ready()
	{
		_rideSessionNode = GetNodeOrNull<RideSessionNode>("../RideSessionNode");
		_hudController = GetNodeOrNull<HudController>("../HudController");
		_rideWorldView = GetNodeOrNull<RideWorldView>("../RideWorldView");
		_startPanel = GetNodeOrNull<Control>("../StartPanel");
		_routeSelectionPanel = GetNodeOrNull<RouteSelectionPanelController>("../RouteSelectionPanel");
		_rideHistoryPanel = GetNodeOrNull<RideHistoryPanelController>("../RideHistoryPanel");
		_deviceConnectionPanel = GetNodeOrNull<DeviceConnectionPanelController>("../DeviceConnectionPanel");
		_menuPanel = GetNode<PanelContainer>("PanelContainer");
		// VBoxContainer is nested under MarginContainer in RideScreen.tscn. Looking it up
		// as a direct child left the content outside the PanelContainer's layout slot.
		_content = GetNode<VBoxContainer>("PanelContainer/MarginContainer/VBoxContainer");
		var menuMargin = GetNode<MarginContainer>("PanelContainer/MarginContainer");
		menuMargin.RemoveChild(_content);
		_menuScroll = new ScrollContainer { Name = "MenuScroll", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
		menuMargin.AddChild(_menuScroll);
		_content.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
		_content.SizeFlagsVertical = Control.SizeFlags.ShrinkBegin;
		_menuScroll.AddChild(_content);
		_menuPanel.CustomMinimumSize = Vector2.Zero;
		Hide();
	}

	public override void _Input(InputEvent @event)
	{
		var cancel = @event is InputEventKey key && key.Pressed && !key.Echo && key.Keycode == Key.Escape ||
			@event is InputEventAction action && action.Pressed && action.Action == "ui_cancel";
		if (!IsMenuVisible || !cancel) return;
		if (_view == MenuView.CatchUpConfirmation) CancelCatchUpConfirmation();
		else if (_view == MenuView.Main) CloseMenu();
		else if (_view == MenuView.AntSetup) ShowView(MenuView.Settings);
		else if (_view == MenuView.Devices) ReturnToSettings();
		else if (_view == MenuView.Settings) ReturnFromSettings();
		else ShowView(MenuView.Main);
		GetViewport().SetInputAsHandled();
	}

	public override void _Process(double delta)
	{
		if (_view == MenuView.Multiplayer) UpdateMultiplayerView();
	}

	public override void _Notification(int what)
	{
		if (what == NotificationResized && _view != MenuView.Closed && IsInsideTree())
			ScheduleMenuPanelResize(_view);
	}

	public void ShowInRideMenu()
	{
		if (_rideSessionNode?.IsRideActive != true || _endingRide) return;
		ShowView(MenuView.Main);
	}

	public void ShowDeviceSettings()
	{
		_settingsReturnToConnection = _rideSessionNode?.IsRideActive != true;
		ShowView(MenuView.Devices);
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
		_hudController?.FocusRideMenuAction();
	}

	private void ReturnToSettings() => ShowView(MenuView.Settings);

	private void ReturnFromSettings()
	{
		if (SettingsReturnPolicy.ShouldReturnToConnection(_settingsReturnToConnection, _rideSessionNode?.IsRideActive == true))
		{
			_settingsReturnToConnection = false;
			_view = MenuView.Closed;
			Hide();
			_deviceConnectionPanel?.OnSettingsReturned();
			return;
		}
		ShowView(MenuView.Main);
	}

	private void ShowView(MenuView view)
	{
		if (_content is null) return;
		_view = view;
		var viewRevision = ++_viewRevision;
		_menuPanel!.CustomMinimumSize = Vector2.Zero;
		_menuScroll!.CustomMinimumSize = Vector2.Zero;
		_content.CustomMinimumSize = Vector2.Zero;
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
				MenuView.Devices => "Devices",
				MenuView.AntSetup => "Devices · Advanced ANT+ setup",
				MenuView.Display => "HUD & Display",
				MenuView.Multiplayer => "Multiplayer",
				MenuView.CatchUpConfirmation => CatchUpTitle(),
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
				AddAction("Settings", () => { _settingsReturnToConnection = false; ShowView(MenuView.Settings); });
				AddAction("HUD & Display", () => ShowView(MenuView.Display));
				AddAction("Multiplayer", () => ShowView(MenuView.Multiplayer));
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
				AddTrainerDifficultyControl();
				_content.AddChild(new Label { Text = "Steering controls", HorizontalAlignment = HorizontalAlignment.Center });
				AddSteeringSensitivityControl();
				AddAction("Devices", () => ShowView(MenuView.Devices));
				if (GameBootstrap.TrainerClient is IndoorCycling.Trainer.Implementations.FakeTrainerClient { HasLiveDummyControls: true })
					AddDummyTrainerSettings();
				AddAction("Back", ReturnFromSettings);
				break;
			case MenuView.Multiplayer:
				BuildMultiplayerView();
				AddAction("Back", () => ShowView(MenuView.Main));
				break;
			case MenuView.AntSetup:
				BuildAntSetupView();
				AddAction("Back to Settings", ReturnToSettings);
				break;
			case MenuView.Devices:
				AddAction("Advanced ANT+ setup", () => ShowView(MenuView.AntSetup));
				AddAction("Back to Settings", ReturnToSettings);
				break;
			case MenuView.CatchUpConfirmation:
				BuildCatchUpConfirmation();
				break;
			case MenuView.Display:
				_content.AddChild(new Label { Text = "F11 or Alt+Enter toggles fullscreen. Window size is remembered.", AutowrapMode = TextServer.AutowrapMode.WordSmart, HorizontalAlignment = HorizontalAlignment.Center });
				_content.AddChild(new Label { Text = "During a ride, hold ← or → to steer across the road. Release to hold position.", AutowrapMode = TextServer.AutowrapMode.WordSmart, HorizontalAlignment = HorizontalAlignment.Center });
				_content.AddChild(new Label { Text = "Sterzo dead zone (degrees)", HorizontalAlignment = HorizontalAlignment.Center });
				var steeringDeadZone = new HSlider { MinValue = 0, MaxValue = 12, Step = 0.5,
					Value = GameBootstrap.Settings.Steering.DeadZoneDegrees, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
				steeringDeadZone.ValueChanged += value =>
				{
					GameBootstrap.Settings.Steering.DeadZoneDegrees = value;
					GameBootstrap.Settings.Save();
				};
				_content.AddChild(steeringDeadZone);
				AddAction(GameBootstrap.Settings.Fullscreen ? "Return to windowed mode" : "Enter fullscreen", GameBootstrap.ToggleFullscreen);
				var showHud = new CheckButton { Text = "Show riding HUD", ButtonPressed = _hudController?.HudDisplayEnabled ?? true };
				showHud.Toggled += enabled => _hudController?.SetHudDisplayEnabled(enabled);
				_content.AddChild(showHud);
				var showInset = new CheckButton { Text = "Show route and gradient inset", ButtonPressed = GameBootstrap.Settings.Display.RouteGradientInsetVisible };
				showInset.Toggled += enabled => _hudController?.SetRouteGradientInsetVisible(enabled);
				_content.AddChild(showInset);
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
					Text = EndRideExplanation(),
					HorizontalAlignment = HorizontalAlignment.Center,
					AutowrapMode = TextServer.AutowrapMode.WordSmart,
					CustomMinimumSize = new Vector2(344, 0)
				});
				var actions = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
				actions.AddThemeConstantOverride("separation", 12);
				_keepRidingButton = new Button { Text = "Continue Riding", CustomMinimumSize = new Vector2(136, 40), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
				_keepRidingButton.Pressed += () => ShowView(MenuView.Main);
				_endRideButton = new Button { Text = "End Ride", CustomMinimumSize = new Vector2(136, 40), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
				_endRideButton.Pressed += OnEndRideButtonPressed;
				actions.AddChild(_keepRidingButton);
				actions.AddChild(_endRideButton);
				_content.AddChild(actions);
				break;
		}
		Show();
		MoveToFront();
		ScheduleMenuPanelResize(view);
		var firstAction = _content.GetChildren().OfType<Control>().FirstOrDefault(child => child is Button or CheckButton or OptionButton or LineEdit);
		FocusAfterViewAsync(view == MenuView.Confirmation ? _keepRidingButton : firstAction, viewRevision);
		if (view == MenuView.CatchUpConfirmation)
		{
			GameLogger.Log("multiplayer_catch_up_confirmation_shown", new { riderIdentityHash = EosIdentityDiagnostics.HashProductUserId(_catchUpRiderId) });
			if (_content.GetChild(_content.GetChildCount() - 1).GetChild(0) is Control cancelButton)
				cancelButton.CallDeferred(Control.MethodName.GrabFocus);
		}
	}

	private async void FocusAfterViewAsync(Control? control, int viewRevision)
	{
		if (control is null) return;
		await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
		if (!IsInsideTree() || viewRevision != _viewRevision || !GodotObject.IsInstanceValid(control) ||
			!control.IsInsideTree() || !control.IsVisibleInTree()) return;
		control.GrabFocus();
	}

	private void ResizeMenuPanel(MenuView view)
	{
		if (_menuPanel is null) return;
		var viewport = GetViewportRect().Size;
		var minimum = _content?.GetCombinedMinimumSize() ?? new Vector2(320, 180);
		var preferredWidth = view == MenuView.Multiplayer ? 540 : view == MenuView.CatchUpConfirmation ? 400 : 420;
		var desired = RideMenuLayoutPolicy.PanelSize(viewport.X, viewport.Y,
			Math.Max(minimum.X, preferredWidth - 40), minimum.Y);
		var width = desired.Width;
		var height = desired.Height;
		_menuPanel.CustomMinimumSize = new Vector2(width, height);
		_menuPanel.OffsetLeft = -width / 2;
		_menuPanel.OffsetRight = width / 2;
		_menuPanel.OffsetTop = -height / 2;
		_menuPanel.OffsetBottom = height / 2;
	}

	private void ScheduleMenuPanelResize(MenuView view)
	{
		var revision = ++_layoutRevision;
		if (_content is null || _menuScroll is null) return;
		_content.QueueSort();
		_menuScroll.QueueSort();
		ResizeMenuPanelAfterLayoutAsync(view, revision);
	}

	private async void ResizeMenuPanelAfterLayoutAsync(MenuView view, int revision)
	{
		await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
		await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
		if (!IsInsideTree() || revision != _layoutRevision || view != _view) return;
		ResizeMenuPanel(view);
		_menuPanel?.QueueSort();
	}

	private string EndRideExplanation()
	{
		return RideMenuLayoutPolicy.EndRideConfirmationText(
			_rideSessionNode?.HasRideRecording == true,
			_rideSessionNode?.SavedRideSegmentCount ?? 0);
	}

#if DEBUG
	public void ShowMainMenuForLayoutTest() => ShowView(MenuView.Main);
	public void ShowMainSettingsForLayoutTest() { _settingsReturnToConnection = false; ShowView(MenuView.Main); ShowView(MenuView.Settings); }
	public void ShowEndRideConfirmationForLayoutTest() => ShowView(MenuView.Confirmation);
#endif

	private string CatchUpTitle()
	{
		if (_catchUpRiderId is not null && _rideSessionNode?.TryGetCatchUpTarget(_catchUpRiderId, out var rider, out _) == true)
			return $"Catch Up to {rider.DisplayName}?";
		return "Catch Up?";
	}

	private void BuildCatchUpConfirmation()
	{
		var targetIsFresh = _catchUpRiderId is not null &&
			_rideSessionNode?.TryGetCatchUpTarget(_catchUpRiderId, out _, out _) == true;
		var body = new Label
		{
			Text = "The selected rider's position is no longer available. Close this dialog and select a connected rider.",
			HorizontalAlignment = HorizontalAlignment.Center,
			AutowrapMode = TextServer.AutowrapMode.WordSmart,
			SizeFlagsVertical = Control.SizeFlags.ExpandFill
		};
		if (targetIsFresh && _catchUpRiderId is not null &&
			_rideSessionNode?.TryGetCatchUpTarget(_catchUpRiderId, out var rider, out _) == true)
		{
			var current = _rideSessionNode.CurrentRideState.DistanceMeters;
			var target = rider.DistanceMeters ?? current;
			var destination = Math.Max(0, target - 4);
			var fitText = _rideSessionNode.HasRideRecording
				? "Your current FIT recording will be saved, and a new recording will begin at the new location."
				: "FIT recording is currently off and will remain off.";
			body.Text = $"Move from {current:0} m to approximately {destination:0} m.\n\n{fitText}";
		}
		body.AddThemeColorOverride("font_color", new Color(0.9f, 0.88f, 0.8f, 1));
		_content!.AddChild(body);
		var buttons = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
		buttons.AddThemeConstantOverride("separation", 14);
		var cancel = new Button { Text = "Cancel", CustomMinimumSize = new Vector2(112, 36) };
		cancel.Pressed += CancelCatchUpConfirmation;
		var confirm = new Button { Text = "Catch Up", CustomMinimumSize = new Vector2(112, 36), Disabled = !targetIsFresh };
		confirm.Pressed += () => OnCatchUpConfirmed(confirm);
		buttons.AddChild(cancel);
		buttons.AddChild(confirm);
		_content.AddChild(buttons);
	}

	private void CancelCatchUpConfirmation()
	{
		if (_view != MenuView.CatchUpConfirmation || _endingRide) return;
		GameLogger.Log("multiplayer_catch_up_cancelled", new { riderIdentityHash = EosIdentityDiagnostics.HashProductUserId(_catchUpRiderId) });
		if (_catchUpReturnsToMultiplayer) ShowView(MenuView.Multiplayer);
		else CloseMenu();
		if (_catchUpReturnsToMultiplayer) _catchUpButton?.CallDeferred(Control.MethodName.GrabFocus);
	}

	private void BuildMultiplayerView()
	{
		_content!.AddChild(new Label
		{
			Text = "Ride with others on the same route. Your position is shared only while Online Riding is on.",
			HorizontalAlignment = HorizontalAlignment.Center,
			AutowrapMode = TextServer.AutowrapMode.WordSmart
		});
		_onlineToggleButton = AddAction("", OnOnlineTogglePressed);
		_multiplayerStatusLabel = new Label { HorizontalAlignment = HorizontalAlignment.Center, AutowrapMode = TextServer.AutowrapMode.WordSmart };
		_content.AddChild(_multiplayerStatusLabel);
		_content.AddChild(new Label { Text = $"Public route session · {_rideSessionNode?.CurrentRoute?.Name ?? "No route selected"}", HorizontalAlignment = HorizontalAlignment.Center });
		AddAction("Check riders on this route", OnCheckRoutePresencePressed);
		_routePresenceLabel = new Label { HorizontalAlignment = HorizontalAlignment.Center, AutowrapMode = TextServer.AutowrapMode.WordSmart };
		_content.AddChild(_routePresenceLabel);
		_multiplayerRidersLabel = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
		_content.AddChild(_multiplayerRidersLabel);
		_multiplayerRiderSelect = new OptionButton();
		_content.AddChild(_multiplayerRiderSelect);
		_catchUpButton = AddAction("Catch Up to selected rider", OnCatchUpSelected);
		_advancedMultiplayerToggle = new CheckButton { Text = "Advanced / Development Multiplayer" };
		_content.AddChild(_advancedMultiplayerToggle);
		_advancedMultiplayerContainer = new VBoxContainer { Visible = false };
		_content.AddChild(_advancedMultiplayerContainer);
		_multiplayerAddressInput = AddAdvancedInputRow("Server", GameBootstrap.Settings.MultiplayerServerAddress);
		_multiplayerNameInput = AddAdvancedInputRow("Display name", _rideSessionNode?.ActiveRiderDisplayName ?? GameBootstrap.RiderProfile.DisplayName);
		_multiplayerRoomInput = AddAdvancedInputRow("Room code", "development");
		_multiplayerJoinButton = AddAdvancedAction("Join development room", OnJoinMultiplayerPressed);
		_multiplayerLeaveButton = AddAdvancedAction("Go Offline (leave session)", OnLeaveMultiplayerPressed);
		_advancedMultiplayerToggle.Toggled += expanded =>
		{
			_advancedMultiplayerContainer.Visible = expanded;
			ScheduleMenuPanelResize(MenuView.Multiplayer);
		};
		UpdateMultiplayerView();
	}

	private LineEdit AddAdvancedInputRow(string label, string initialValue)
	{
		var row = new HBoxContainer();
		row.AddChild(new Label { Text = label, CustomMinimumSize = new Vector2(94, 0), VerticalAlignment = VerticalAlignment.Center });
		var input = new LineEdit { Text = initialValue, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, ClearButtonEnabled = true };
		row.AddChild(input);
		_advancedMultiplayerContainer!.AddChild(row);
		return input;
	}

	private Button AddAdvancedAction(string text, Action action)
	{
		var button = new Button { Text = text, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
		button.Pressed += action;
		_advancedMultiplayerContainer!.AddChild(button);
		return button;
	}

	private void UpdateMultiplayerView()
	{
		if (_multiplayerStatusLabel is null || _multiplayerRidersLabel is null || _rideSessionNode is null) return;
		_multiplayerStatusLabel.Text = _rideSessionNode.OnlineRidingEnabled
			? $"Online Riding · {_rideSessionNode.OnlineStatus}"
			: "Online Riding is off · your ride stays private";
		_onlineToggleButton!.Text = _rideSessionNode.OnlineRidingEnabled ? "Go Offline" : "Go Online";
		_multiplayerJoinButton!.Disabled = !_rideSessionNode.OnlineRidingEnabled || _rideSessionNode.IsMultiplayerConnected;
		_multiplayerLeaveButton!.Disabled = !_rideSessionNode.HasMultiplayerClient;
		var riders = _rideSessionNode.MultiplayerRiders;
		_multiplayerRidersLabel.Text = riders.Count == 0 ? "No other riders in this session." : "Connected riders (absolute route distance):";
		if (_multiplayerRiderSelect is not null)
		{
			var currentDistance = _rideSessionNode.CurrentRideState.DistanceMeters;
			var routeLength = _rideSessionNode.CurrentRoute?.DistanceMeters ?? 0;
			var ids = string.Join("|", riders.Select(r => r.RiderId));
			if (ids != _riderOptionIds)
			{
				var oldSelection = _multiplayerRiderSelect.Selected;
				_multiplayerRiderSelect.Clear();
				foreach (var rider in riders)
				{
					_multiplayerRiderSelect.AddItem(rider.DisplayName);
					_multiplayerRiderSelect.SetItemMetadata(_multiplayerRiderSelect.ItemCount - 1, rider.RiderId);
				}
				if (_multiplayerRiderSelect.ItemCount > 0) _multiplayerRiderSelect.Select(Math.Clamp(oldSelection, 0, _multiplayerRiderSelect.ItemCount - 1));
				_riderOptionIds = ids;
			}
			for (var i = 0; i < riders.Count && i < _multiplayerRiderSelect.ItemCount; i++)
			{
				var rider = riders[i];
				var label = rider.HasState && rider.DistanceMeters is { } d
					? $"{rider.DisplayName} · lap {Math.Max(1, (int)Math.Floor(d / Math.Max(1, routeLength)) + 1)} · {d / 1000:0.00} km · {Math.Abs(d-currentDistance)/1000:0.00} km {(d >= currentDistance ? "ahead" : "behind")}"
					: $"{rider.DisplayName} · waiting for rider data";
				_multiplayerRiderSelect.SetItemText(i, label);
			}
			_catchUpButton!.Disabled = riders.Count == 0 || !_rideSessionNode.IsMultiplayerConnected;
		}
	}

	private async void OnOnlineTogglePressed()
	{
		if (_rideSessionNode is null || _onlineToggleButton is null) return;
		_onlineToggleButton.Disabled = true;
		try { await _rideSessionNode.SetOnlineRidingEnabledAsync(!_rideSessionNode.OnlineRidingEnabled); }
		catch (Exception ex)
		{
			GameLogger.Log("multiplayer_online_toggle_failed", new { message = ex.Message });
			_hudController?.SetStatus($"Online Riding could not be changed: {ex.Message}");
		}
		finally { _onlineToggleButton.Disabled = false; UpdateMultiplayerView(); }
	}

	private async void OnCheckRoutePresencePressed()
	{
		if (_rideSessionNode is null || _routePresenceLabel is null) return;
		_routePresenceLabel.Text = "Checking route session…";
		try
		{
			var presence = await _rideSessionNode.GetCurrentRoutePresenceAsync();
			_routePresenceLabel.Text = presence.IsAvailable
				? $"{presence.ActiveRiders ?? 0} active rider(s) on this route."
				: presence.Message ?? "Route session is unavailable.";
		}
		catch (Exception ex)
		{
			GameLogger.Log("multiplayer_route_presence_failed", new { message = ex.Message });
			_routePresenceLabel.Text = $"Could not check route session: {ex.Message}";
		}
	}

	private void OnCatchUpSelected()
	{
		if (_multiplayerRiderSelect is null || _multiplayerRiderSelect.Selected < 0) return;
		_catchUpRiderId = _multiplayerRiderSelect.GetItemMetadata(_multiplayerRiderSelect.Selected).AsString();
		_catchUpReturnsToMultiplayer = true;
		GameLogger.Log("multiplayer_catch_up_requested", new { riderIdentityHash = EosIdentityDiagnostics.HashProductUserId(_catchUpRiderId), source = "menu" });
		ShowView(MenuView.CatchUpConfirmation);
	}

	public void ShowCatchUpConfirmation(string riderId)
	{
		var error = "";
		if (_rideSessionNode is null || !_rideSessionNode.TryGetCatchUpTarget(riderId, out _, out error))
		{
			_hudController?.SetStatus(string.IsNullOrEmpty(error) ? "That rider is unavailable." : error);
			return;
		}
		_catchUpRiderId = riderId;
		_catchUpReturnsToMultiplayer = false;
		GameLogger.Log("multiplayer_catch_up_requested", new { riderIdentityHash = EosIdentityDiagnostics.HashProductUserId(riderId), source = "join_offer" });
		ShowView(MenuView.CatchUpConfirmation);
	}

	private async void OnCatchUpConfirmed(Button confirmButton)
	{
		if (_endingRide || _catchUpRiderId is null || _rideSessionNode is null) return;
		_endingRide = true;
		confirmButton.Disabled = true;
		GameLogger.Log("multiplayer_catch_up_confirmed", new { riderIdentityHash = EosIdentityDiagnostics.HashProductUserId(_catchUpRiderId) });
		try
		{
			var moved = await _rideSessionNode.CatchUpToRiderAsync(_catchUpRiderId);
			if (moved) CloseMenu();
			else
			{
				_hudController?.SetStatus(_rideSessionNode.LastRecordingError is { Length: > 0 } message
					? $"Catch Up failed: {message}" : "Catch Up failed. The ride position was not changed.");
				if (_catchUpReturnsToMultiplayer) ShowView(MenuView.Multiplayer);
				else CloseMenu();
			}
		}
		catch (Exception ex)
		{
			GameLogger.Log("multiplayer_catch_up_failed", new { riderIdentityHash = EosIdentityDiagnostics.HashProductUserId(_catchUpRiderId), message = ex.Message });
			_hudController?.SetStatus($"Catch Up failed: {ex.Message}");
			if (_catchUpReturnsToMultiplayer) ShowView(MenuView.Multiplayer);
			else CloseMenu();
		}
		finally { _endingRide = false; }
	}

	private async void OnJoinMultiplayerPressed()
	{
		if (_rideSessionNode is null || _multiplayerAddressInput is null || _multiplayerNameInput is null || _multiplayerRoomInput is null) return;
		if (_multiplayerJoinButton is not null) _multiplayerJoinButton.Disabled = true;
		if (_multiplayerStatusLabel is not null) _multiplayerStatusLabel.Text = "Connecting…";
		try
		{
			await _rideSessionNode.JoinMultiplayerAsync(_multiplayerAddressInput.Text,
				_multiplayerNameInput.Text, _multiplayerRoomInput.Text);
		}
		catch (Exception ex)
		{
			if (_multiplayerStatusLabel is not null) _multiplayerStatusLabel.Text = $"Could not join: {ex.Message}";
		}
		UpdateMultiplayerView();
	}

	private async void OnLeaveMultiplayerPressed()
	{
		if (_rideSessionNode is null) return;
		if (_multiplayerLeaveButton is not null) _multiplayerLeaveButton.Disabled = true;
		await _rideSessionNode.SetOnlineRidingEnabledAsync(false);
		UpdateMultiplayerView();
	}

	private void AddDummyTrainerSettings()
	{
		_content!.AddChild(new Label { Text = "Dummy trainer", HorizontalAlignment = HorizontalAlignment.Center });
		var settings = GameBootstrap.Settings.DummyTrainer;
		var power = new Label { HorizontalAlignment = HorizontalAlignment.Center };
		void UpdatePowerLabel() => power.Text = settings.Mode == "W/kg"
			? $"Target: {settings.WattsPerKg:0.0} W/kg · {settings.CalculateWatts(_rideSessionNode?.ActiveRiderBodyWeightKg ?? GameBootstrap.RiderProfile.BodyWeightKg)} W"
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

	private void AddTrainerDifficultyControl()
	{
		_content!.AddChild(new Label { Text = "Trainer Difficulty", HorizontalAlignment = HorizontalAlignment.Center });
		_content.AddChild(new Label
		{
			Text = "Scales simulated resistance sent to the trainer. Ride physics keeps the full route gradient.",
			AutowrapMode = TextServer.AutowrapMode.WordSmart,
			HorizontalAlignment = HorizontalAlignment.Center
		});
		var row = new HBoxContainer();
		var slider = new HSlider
		{
			MinValue = 0, MaxValue = 100, Step = 5,
			Value = GameBootstrap.TrainerDifficultyPercent,
			SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
		};
		var valueLabel = new Label { Text = $"{GameBootstrap.TrainerDifficultyPercent}%", CustomMinimumSize = new Vector2(48, 0) };
		slider.ValueChanged += value =>
		{
			valueLabel.Text = $"{value:0}%";
			_ = ApplyTrainerDifficultyAsync((int)value);
		};
		row.AddChild(slider); row.AddChild(valueLabel); _content.AddChild(row);
	}

	private void AddSteeringSensitivityControl()
	{
		var steering = GameBootstrap.Settings.Steering;
		_content!.AddChild(new Label
		{
			Text = "Steering sensitivity (keyboard + Sterzo)",
			HorizontalAlignment = HorizontalAlignment.Center
		});
		_content.AddChild(new Label
		{
			Text = "1.0× is the alpha.36 baseline. Changes apply immediately to both inputs.",
			AutowrapMode = TextServer.AutowrapMode.WordSmart,
			HorizontalAlignment = HorizontalAlignment.Center
		});
		var row = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
		var slider = new HSlider
		{
			MinValue = 0.5,
			MaxValue = 3.0,
			Step = 0.1,
			Value = steering.Sensitivity,
			SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
		};
		var valueLabel = new Label { CustomMinimumSize = new Vector2(48, 0), HorizontalAlignment = HorizontalAlignment.Center };
		void UpdateValueLabel(double value) => valueLabel.Text = $"{value:0.0}×";
		UpdateValueLabel(steering.Sensitivity);
		slider.ValueChanged += value =>
		{
			var normalized = Math.Round(value, 1);
			steering.Sensitivity = normalized;
			UpdateValueLabel(normalized);
			GameBootstrap.Settings.Save();
		};
		var reset = new Button { Text = "Reset", CustomMinimumSize = new Vector2(64, 0) };
		reset.Pressed += () => slider.Value = SteeringControlSettings.DefaultSensitivity;
		row.AddChild(slider);
		row.AddChild(valueLabel);
		row.AddChild(reset);
		_content.AddChild(row);
	}

	private void BuildAntSetupView()
	{
		_content!.AddChild(new Label
		{
			Text = "Enter the ANT+ network key as 16 hexadecimal digits or eight byte values. It stays in this Windows account's private app-data storage.",
			AutowrapMode = TextServer.AutowrapMode.WordSmart,
			HorizontalAlignment = HorizontalAlignment.Center
		});
		var status = new Label { Text = AntNetworkKeyMaterial.GetStatus(), AutowrapMode = TextServer.AutowrapMode.WordSmart };
		_content.AddChild(status);
		var keyInput = new LineEdit
		{
			PlaceholderText = "16 hexadecimal digits or eight byte values",
			ClearButtonEnabled = true,
			SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
			Secret = true
		};
		_content.AddChild(keyInput);
		var actions = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
		var save = new Button { Text = "Save key", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
		var clear = new Button { Text = "Clear key", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
		var help = new Button { Text = "Garmin key page", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
		bool InUse() => _deviceConnectionPanel?.IsAntConfigurationInUse() == true;
		save.Pressed += () =>
		{
			if (InUse()) { status.Text = "Disconnect ANT+ before changing its setup."; return; }
			try
			{
				AntNetworkKeyMaterial.Save(keyInput.Text);
				keyInput.Clear();
				status.Text = "ANT+ key saved for this Windows account.";
			}
			catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or System.Security.SecurityException)
			{ status.Text = ex.Message; }
		};
		clear.Pressed += () =>
		{
			if (InUse()) { status.Text = "Disconnect ANT+ before clearing its setup."; return; }
			try { AntNetworkKeyMaterial.Clear(); keyInput.Clear(); status.Text = AntNetworkKeyMaterial.GetStatus(); }
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Security.SecurityException)
			{ status.Text = ex.Message; }
		};
		help.Pressed += () => OS.ShellOpen(AntNetworkKeyMaterial.GarminNetworkKeysUrl);
		actions.AddChild(save); actions.AddChild(clear); actions.AddChild(help); _content.AddChild(actions);
	}

	private async Task ApplyTrainerDifficultyAsync(int percent)
	{
		if (_rideSessionNode is null) return;
		try { await _rideSessionNode.SetTrainerDifficultyAsync(percent); }
		catch (Exception ex)
		{
			GameLogger.Log("trainer_difficulty_save_failed", new { percent, message = ex.Message });
			_hudController?.SetStatus($"Trainer difficulty could not be saved: {ex.Message}");
		}
	}

	private Button AddAction(string text, Action action)
	{
		var button = new Button { Text = text, CustomMinimumSize = new Vector2(300, 40), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
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
