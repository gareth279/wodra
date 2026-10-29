using Godot;
using System.Collections.Generic;
using System.Linq;
using System;
using System.Threading.Tasks;
using System.Threading;
using System.IO;
using IndoorCycling.Core.Models;
using IndoorCycling.Trainer.Models;




public partial class StartPanelController : Control
{
	private RideSessionNode? _rideSessionNode;
	private FileDialog? _fileDialog;
	private Label? _statusLabel;

	private Control? _hudController;

	private DeviceConnectionPanelController? _deviceConnectionPanel;

	private RouteSelectionPanelController? _routeSelectionPanel;
	private WorkoutSelectionPanelController? _workoutSelectionPanel;
	private CheckButton? _debugModeCheckButton;
	private Label? _onlineStatusLabel;
	private HudController? _typedHudController;
	private RideHistoryPanelController? _rideHistoryPanel;
	private OptionButton? _profilePicker;
	private OptionButton? _dummyMode;
	private SpinBox? _dummyPower;
	private SpinBox? _dummyCadence;
	private Window? _profileWindow;
	private LineEdit? _profileName;
	private SpinBox? _profileBody;
	private SpinBox? _profileBike;
	private HSlider? _profileTrainerDifficulty;
	private Label? _profileTrainerDifficultyValue;
	private CheckButton? _profileTest;
	private OptionButton? _profileEditPicker;
	private Label? _stravaProfileStatus;
	private CheckButton? _stravaAutoUpload;
	private Button? _stravaLinkButton;
	private Button? _stravaDisconnectButton;
	private Button? _stravaCancelButton;
	private CancellationTokenSource? _stravaLinkCancellation;
	private List<RiderProfileRecord> _profiles = [];
	private Guid? _editingProfileId;
	private VelopackUpdatePanel? _updatePanel;
	private PanelContainer? _startupPanel;
	private DiagnosticPanelController? _diagnosticPanel;
	private Window? _optionsWindow;
	private CheckButton? _optionsOnlineToggle;
	private CheckButton? _optionsGameFeaturesToggle;
	private CheckButton? _optionsMediaControlsToggle;
	private CheckButton? _optionsCompanionToggle;
	private Button? _optionsCompanionAdvanced;
	private LineEdit? _optionsCompanionUrl;
	private Label? _optionsCompanionStatus;
	private Label? _optionsCompanionCode;
	private TextureRect? _optionsCompanionQr;
	private Button? _optionsCompanionApprove;
	private Button? _optionsCompanionReject;
	private Button? _optionsCompanionStart;
	private Button? _optionsCompanionCancel;
	private VBoxContainer? _optionsCompanionControls;
	private VBoxContainer? _optionsCompanionDevices;
	private Button? _optionsCompanionRevokeAll;
	private string _lastCompanionDeviceSignature = "";
	private CompanionRelayClient? _companionClient;
	private Label? _aboutBuildInformation;
	private Label? _aboutCopyStatus;
	private bool _optionsReturnToRoutes;
	private bool _optionsReturnToWorkouts;
	private bool _optionsReturnToRide;
	private HBoxContainer? _profileRow;
	private GridContainer? _dummySettingsRow;
	private Label? _trainerHomeName, _trainerHomeStatus, _heartRateHomeName, _heartRateHomeStatus, _steeringHomeName, _steeringHomeStatus;
	private Label? _dummyModeNotice;
	private Button? _advancedOptionsButton;
	private VBoxContainer? _advancedOptionsContent;
	private Button? _dummyStartButton, _fitReplayButton, _diagnosticsButton, _quitButton;

	public override void _Ready()
	{
		_rideSessionNode = GetNodeOrNull<RideSessionNode>("../RideSessionNode");
		_companionClient = new CompanionRelayClient { Name = "CompanionRelayClient" };
		AddChild(_companionClient);
		_companionClient.Configure(_rideSessionNode, GameBootstrap.Settings.CompanionEnabled, GameBootstrap.Settings.CompanionRelayBaseUrl);
		_fileDialog = GetNodeOrNull<FileDialog>("FileDialog");
		_statusLabel = GetNodeOrNull<Label>("PanelContainer/MarginContainer/StartupScroll/VBoxContainer/StatusLabel");
		_deviceConnectionPanel = GetNodeOrNull<DeviceConnectionPanelController>("../DeviceConnectionPanel");
		_routeSelectionPanel = GetNodeOrNull<RouteSelectionPanelController>("../RouteSelectionPanel");
		_workoutSelectionPanel = GetNodeOrNull<WorkoutSelectionPanelController>("../WorkoutSelectionPanel");
		_hudController = GetNodeOrNull<Control>("../HudController");
		_typedHudController = _hudController as HudController;
		_rideHistoryPanel = GetNodeOrNull<RideHistoryPanelController>("../RideHistoryPanel");
		BuildLocalSettingsUi();
		_startupPanel = GetNodeOrNull<PanelContainer>("PanelContainer");
		Resized += FitStartupPanel;
		Resized += ResizeOptionsWindow;
		FitStartupPanel();
		_ = RefreshProfilesAsync(openCreateProfileWhenEmpty: true);
		_debugModeCheckButton = GetNode<CheckButton>("PanelContainer/MarginContainer/StartupScroll/VBoxContainer/DebugModeCheckButton");
		_debugModeCheckButton.ButtonPressed = GameBootstrap.DebugModeEnabled;
		_debugModeCheckButton.Toggled += OnDebugModeToggled;
		_typedHudController?.SetDebugMode(GameBootstrap.DebugModeEnabled);
		ConfigureStartupStatusLabel(_statusLabel);
		var startupColumn = GetNodeOrNull<VBoxContainer>("PanelContainer/MarginContainer/StartupScroll/VBoxContainer");
		if (startupColumn is not null)
		{
			_diagnosticPanel = new DiagnosticPanelController { Name = "DiagnosticPanel" };
			GetParent().CallDeferred(Node.MethodName.AddChild, _diagnosticPanel);
			var diagnosticsButton = new Button { Name = "LocalDiagnosticsButton", Text = "Diagnostics" };
			diagnosticsButton.Pressed += () => { _optionsWindow?.Hide(); _diagnosticPanel.OpenFor(this); };
			startupColumn.AddChild(diagnosticsButton);
			startupColumn.MoveChild(diagnosticsButton, startupColumn.GetNode<Button>("QuitButton").GetIndex());
			_updatePanel = new VelopackUpdatePanel();
			startupColumn.AddChild(_updatePanel);
			// Keep update status/actions with the startup choices, before the terminal
			// Quit action, rather than extending the menu below it.
			var quitButton = startupColumn.GetNodeOrNull<Button>("QuitButton");
			if (quitButton is not null)
				startupColumn.MoveChild(_updatePanel, quitButton.GetIndex());
			_updatePanel.Attach();
		}
		if (GameBootstrap.Settings.OnlineRidingEnabled && _rideSessionNode is not null)
			_ = _rideSessionNode.SetOnlineRidingEnabledAsync(true);
		_hudController?.Hide();
		GetNode<Button>("PanelContainer/MarginContainer/StartupScroll/VBoxContainer/ConnectTrainerButton").Pressed += OnConnectTrainerPressed;
		GetNode<Button>("PanelContainer/MarginContainer/StartupScroll/VBoxContainer/OptionsButton").Pressed += () => ShowOptions(false);
		GetNode<Button>("PanelContainer/MarginContainer/StartupScroll/VBoxContainer/Test with Fake Trainer").Pressed += OnQuickFakeTrainerPressed;
		GetNode<Button>("PanelContainer/MarginContainer/StartupScroll/VBoxContainer/LoadFitButton").Pressed += OnLoadFitPressed;
		GetNode<Button>("PanelContainer/MarginContainer/StartupScroll/VBoxContainer/HistoryButton").Pressed += () => _rideHistoryPanel?.ShowHistory();
		GetNode<Button>("PanelContainer/MarginContainer/StartupScroll/VBoxContainer/QuitButton").Pressed += OnQuitPressed;
		BuildPreRideHomeUi(startupColumn);

		if (_fileDialog is not null)
		{
			_fileDialog.FileSelected += OnFitFileSelected;
		}
	}



	public void ShowOptions(bool returnToRoutes, bool returnToWorkouts = false, bool returnToRide = false)
	{
		_optionsReturnToRoutes = returnToRoutes;
		_optionsReturnToWorkouts = returnToWorkouts;
		_optionsReturnToRide = returnToRide;
		if (_optionsWindow is null)
		{
			_optionsWindow = new Window { Name = "OptionsWindow", Title = "Options", MinSize = new Vector2I(440, 400), Exclusive = true };
			var margin = new MarginContainer { Name = "OptionsMargin" };
			foreach (var pair in new[] { ("margin_left", 20), ("margin_right", 20), ("margin_top", 12), ("margin_bottom", 12) }) margin.AddThemeConstantOverride(pair.Item1, pair.Item2);
			margin.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
			_optionsWindow.AddChild(margin);
			var column = new VBoxContainer { Name = "OptionsLayout", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
			column.AddThemeConstantOverride("separation", 8); margin.AddChild(column);
			var title = new Label { Text = "Options", HorizontalAlignment = HorizontalAlignment.Center }; title.AddThemeFontSizeOverride("font_size", 22); column.AddChild(title);
			var optionsScroll = new ScrollContainer
			{
				Name = "OptionsScroll",
				SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
				SizeFlagsVertical = Control.SizeFlags.ExpandFill,
				HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled
			};
			column.AddChild(optionsScroll);
			var content = new VBoxContainer { Name = "OptionsContent", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
			content.AddThemeConstantOverride("separation", 6);
			optionsScroll.AddChild(content);
			_optionsGameFeaturesToggle = AddOptionsSetting(content, "GameFeaturesToggle", "Game features", "Enabled", GameFeaturesPreference.Enabled);
			_optionsGameFeaturesToggle.Toggled += enabled => GameFeaturesPreference.Set(enabled);
			content.AddChild(new Label { Text = CollectibleUiText.HelperText, AutowrapMode = TextServer.AutowrapMode.WordSmart, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
			_optionsOnlineToggle = AddOptionsSetting(content, "OnlineRidingToggle", "Online Riding", "Enabled", GameBootstrap.Settings.OnlineRidingEnabled);
			_optionsOnlineToggle.Toggled += OnOnlineRidingToggled;
			_onlineStatusLabel = new Label { Name = "OnlineStatusLabel", Text = "Online Riding: Off", AutowrapMode = TextServer.AutowrapMode.WordSmart };
			ConfigureStartupStatusLabel(_onlineStatusLabel, clipText: false); content.AddChild(_onlineStatusLabel);
			_optionsMediaControlsToggle = AddOptionsSetting(content, "MediaControlsToggle", "In-ride media controls (Spotify and other Windows players)", "Enabled", GameBootstrap.Settings.InRideMediaControlsEnabled);
			_optionsMediaControlsToggle.Toggled += enabled =>
			{
				GameBootstrap.Settings.InRideMediaControlsEnabled = enabled;
				GameBootstrap.Settings.Save();
			};
			content.AddChild(new Label { Text = "Uses the active Windows media session. This is off by default; turn it on to show now-playing details and supported playback controls during rides.", AutowrapMode = TextServer.AutowrapMode.WordSmart, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
			content.AddChild(new HSeparator());
			var companionHeading = new Label { Text = "Companion" }; companionHeading.AddThemeFontSizeOverride("font_size", 16); content.AddChild(companionHeading);
			_optionsCompanionToggle = AddOptionsSetting(content, "CompanionEnabledToggle", "Enable read-only phone companion", "Enabled", GameBootstrap.Settings.CompanionEnabled);
			_optionsCompanionToggle.Toggled += enabled =>
			{
				GameBootstrap.Settings.CompanionEnabled = enabled;
				GameBootstrap.Settings.Save();
				ConfigureCompanion();
			};
			_optionsCompanionControls = new VBoxContainer { Name = "CompanionControls", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
			_optionsCompanionControls.AddThemeConstantOverride("separation", 5);
			content.AddChild(_optionsCompanionControls);
			_optionsCompanionAdvanced = new Button { Name = "CompanionAdvancedOverride", Text = "Advanced relay override", CustomMinimumSize = new Vector2(0, 36), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
			_optionsCompanionAdvanced.Pressed += () =>
			{
				if (_optionsCompanionUrl is null || _optionsCompanionAdvanced is null) return;
				_optionsCompanionUrl.Visible = !_optionsCompanionUrl.Visible;
				_optionsCompanionAdvanced.Text = _optionsCompanionUrl.Visible ? "Hide relay override" : "Advanced relay override";
			};
			_optionsCompanionControls.AddChild(_optionsCompanionAdvanced);
			_optionsCompanionUrl = new LineEdit { Name = "CompanionRelayUrl", PlaceholderText = AppSettings.ProductionCompanionRelayBaseUrl, Text = GameBootstrap.Settings.CompanionRelayBaseUrl, CustomMinimumSize = new Vector2(0, 40), Visible = false };
			_optionsCompanionControls.AddChild(_optionsCompanionUrl);
			var companionActions = new HBoxContainer { Name = "CompanionActions" };
			_optionsCompanionStart = new Button { Name = "StartCompanionPairing", Text = "Connect new phone", CustomMinimumSize = new Vector2(0, 40), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
			_optionsCompanionStart.Pressed += async () => await StartCompanionPairingAsync();
			companionActions.AddChild(_optionsCompanionStart);
			_optionsCompanionCancel = new Button { Name = "CancelCompanionPairing", Text = "Cancel", CustomMinimumSize = new Vector2(0, 40) };
			_optionsCompanionCancel.Pressed += async () => { if (_companionClient is not null) await _companionClient.CancelAsync(); UpdateCompanionUi(); };
			companionActions.AddChild(_optionsCompanionCancel);
			_optionsCompanionControls.AddChild(companionActions);
			_optionsCompanionQr = new TextureRect { Name = "CompanionQr", CustomMinimumSize = new Vector2(150, 150), ExpandMode = TextureRect.ExpandModeEnum.FitWidthProportional, StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered, SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter };
			_optionsCompanionControls.AddChild(_optionsCompanionQr);
			_optionsCompanionCode = new Label { Name = "CompanionManualCode", HorizontalAlignment = HorizontalAlignment.Center, Text = "" }; _optionsCompanionCode.AddThemeFontSizeOverride("font_size", 20);
			_optionsCompanionControls.AddChild(_optionsCompanionCode);
			_optionsCompanionStatus = new Label { Name = "CompanionStatus", AutowrapMode = TextServer.AutowrapMode.WordSmart };
			_optionsCompanionControls.AddChild(_optionsCompanionStatus);
			_optionsCompanionControls.AddChild(new HSeparator());
			var rememberedHeading = new Label { Text = "Remembered phones" }; rememberedHeading.AddThemeFontSizeOverride("font_size", 15);
			_optionsCompanionControls.AddChild(rememberedHeading);
			_optionsCompanionDevices = new VBoxContainer { Name = "RememberedCompanionDevices", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
			_optionsCompanionDevices.AddThemeConstantOverride("separation", 4);
			_optionsCompanionControls.AddChild(_optionsCompanionDevices);
			_optionsCompanionRevokeAll = new Button { Name = "RevokeAllCompanionDevices", Text = "Revoke all phones", CustomMinimumSize = new Vector2(0, 36) };
			_optionsCompanionRevokeAll.Pressed += async () => { if (_companionClient is not null) await _companionClient.RevokeAllDevicesAsync(); _lastCompanionDeviceSignature = ""; UpdateCompanionUi(); };
			_optionsCompanionControls.AddChild(_optionsCompanionRevokeAll);
			var approvalRow = new HBoxContainer();
			_optionsCompanionApprove = new Button { Name = "ApproveCompanion", Text = "Approve phone", CustomMinimumSize = new Vector2(0, 40), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
			_optionsCompanionApprove.Pressed += async () => { if (_companionClient is not null) await _companionClient.DecideAsync(true); UpdateCompanionUi(); };
			_optionsCompanionReject = new Button { Name = "RejectCompanion", Text = "Decline", CustomMinimumSize = new Vector2(0, 40) };
			_optionsCompanionReject.Pressed += async () => { if (_companionClient is not null) await _companionClient.DecideAsync(false); UpdateCompanionUi(); };
			approvalRow.AddChild(_optionsCompanionApprove); approvalRow.AddChild(_optionsCompanionReject);
			_optionsCompanionControls.AddChild(approvalRow);
			_optionsCompanionUrl.TextSubmitted += _ => SaveCompanionUrl();
			_optionsCompanionUrl.FocusExited += SaveCompanionUrl;
			content.AddChild(new Label { Text = "Connect a new phone with a short-lived QR code. Paired phones reconnect automatically. Companion sharing is read-only and remains opt-in.", AutowrapMode = TextServer.AutowrapMode.WordSmart, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
			content.AddChild(new HSeparator());
			_advancedOptionsButton = new Button { Name = "AdvancedTestingToggle", Text = "Advanced / Testing & Diagnostics", ToggleMode = true, ButtonPressed = false, CustomMinimumSize = new Vector2(0, 40), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
			content.AddChild(_advancedOptionsButton);
			_advancedOptionsContent = new VBoxContainer { Name = "AdvancedTestingContent", Visible = false, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
			_advancedOptionsContent.AddThemeConstantOverride("separation", 7);
			content.AddChild(_advancedOptionsContent);
			_advancedOptionsButton.Toggled += expanded => { if (_advancedOptionsContent is not null) _advancedOptionsContent.Visible = expanded; };
			_advancedOptionsContent.AddChild(new Label { Text = "Testing & Diagnostics", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
			_advancedOptionsContent.AddChild(new Label { Text = "Synthetic trainer rides are marked as test mode. They do not represent a connected physical trainer.", AutowrapMode = TextServer.AutowrapMode.WordSmart, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
			if (_dummySettingsRow is not null) { _dummySettingsRow.Visible = true; _dummySettingsRow.Reparent(_advancedOptionsContent); }
			if (_dummyStartButton is not null) { _dummyStartButton.Visible = true; _dummyStartButton.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill; _dummyStartButton.Reparent(_advancedOptionsContent); }
			if (_debugModeCheckButton is not null) { _debugModeCheckButton.Text = "Debug Overlay"; _debugModeCheckButton.Visible = true; _debugModeCheckButton.Reparent(_advancedOptionsContent); }
			if (_fitReplayButton is not null) { _fitReplayButton.Visible = true; _fitReplayButton.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill; _fitReplayButton.Reparent(_advancedOptionsContent); }
			if (_diagnosticsButton is not null) { _diagnosticsButton.Visible = true; _diagnosticsButton.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill; _diagnosticsButton.Reparent(_advancedOptionsContent); }
			content.AddChild(new HSeparator());
			var aboutHeading = new Label { Text = "About" }; aboutHeading.AddThemeFontSizeOverride("font_size", 16); content.AddChild(aboutHeading);
			_aboutBuildInformation = new Label
			{
				Name = "AboutBuildInformation",
				Text = BuildIdentity.Current.ToPlainText(),
				AutowrapMode = TextServer.AutowrapMode.WordSmart,
				SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
			};
			_aboutBuildInformation.AddThemeFontSizeOverride("font_size", 12);
			content.AddChild(_aboutBuildInformation);
			var aboutActions = new VBoxContainer { Name = "AboutActions", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }; content.AddChild(aboutActions);
			var copyBuildInformation = new Button { Name = "CopyBuildInformationButton", Text = "Copy build information", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
			copyBuildInformation.Pressed += CopyBuildInformation;
			aboutActions.AddChild(copyBuildInformation);
			_aboutCopyStatus = new Label { Name = "BuildInformationCopyStatus", AutowrapMode = TextServer.AutowrapMode.WordSmart, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
			_aboutCopyStatus.AddThemeFontSizeOverride("font_size", 12);
			aboutActions.AddChild(_aboutCopyStatus);
			var close = new Button { Name = "DoneButton", Text = "Done", CustomMinimumSize = new Vector2(0, 40) }; close.Pressed += CloseOptions; column.AddChild(close);
			_optionsWindow.CloseRequested += CloseOptions;
			GetParent().AddChild(_optionsWindow);
		}
		ResizeOptionsWindow();
		_optionsGameFeaturesToggle!.SetPressedNoSignal(GameFeaturesPreference.Enabled);
		_optionsOnlineToggle!.SetPressedNoSignal(GameBootstrap.Settings.OnlineRidingEnabled);
		_optionsMediaControlsToggle!.SetPressedNoSignal(GameBootstrap.Settings.InRideMediaControlsEnabled);
		_optionsCompanionToggle!.SetPressedNoSignal(GameBootstrap.Settings.CompanionEnabled);
		_optionsCompanionUrl!.Text = GameBootstrap.Settings.CompanionRelayBaseUrl;
		_optionsCompanionUrl.Visible = !string.Equals(GameBootstrap.Settings.CompanionRelayBaseUrl, AppSettings.ProductionCompanionRelayBaseUrl, StringComparison.OrdinalIgnoreCase);
		if (_optionsCompanionAdvanced is not null)
			_optionsCompanionAdvanced.Text = _optionsCompanionUrl.Visible ? "Hide relay override" : "Advanced relay override";
		ConfigureCompanion();
		// Do not allow a test trainer or FIT replay to replace an active real ride.
		if (_dummyStartButton is not null) _dummyStartButton.Disabled = returnToRide;
		if (_fitReplayButton is not null) _fitReplayButton.Disabled = returnToRide;
		_aboutBuildInformation!.Text = BuildIdentity.Current.ToPlainText();
		_aboutCopyStatus!.Text = string.Empty;
		if (_optionsReturnToRoutes || _optionsReturnToWorkouts || _optionsReturnToRide) Hide(); else Show();
		_optionsWindow.PopupCentered();
		UpdateCompanionUi();
		if (GameBootstrap.Settings.OnlineRidingEnabled && _rideSessionNode is not null) _ = _rideSessionNode.RefreshOnlineAvailabilityAsync();
	}

	private static CheckButton AddOptionsSetting(VBoxContainer content, string name, string label, string buttonText, bool pressed)
	{
		var row = new HBoxContainer { Name = $"{name}Row", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
		var settingLabel = new Label { Name = $"{name}Label", Text = label, AutowrapMode = TextServer.AutowrapMode.WordSmart,
			SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, VerticalAlignment = VerticalAlignment.Center };
		var toggle = new CheckButton { Name = name, Text = buttonText, ButtonPressed = pressed,
			SizeFlagsHorizontal = Control.SizeFlags.ShrinkEnd, TooltipText = label };
		row.AddChild(settingLabel);
		row.AddChild(toggle);
		content.AddChild(row);
		return toggle;
	}

	private void ResizeOptionsWindow()
	{
		if (_optionsWindow is null || !IsInsideTree()) return;
		_optionsWindow.Size = CalculateOptionsWindowSize(GetViewportRect().Size);
	}

	internal static Vector2I CalculateOptionsWindowSize(Vector2 viewport)
	{
		// Keep a broad content column on desktop while leaving margins for the
		// host window and respecting smaller displays (including scaled Windows).
		var availableWidth = Math.Max(440, (int)viewport.X - 48);
		var width = Math.Min(availableWidth, Math.Max(720, Math.Min(1600, (int)(viewport.X * 0.82f))));
		var height = Math.Max(400, Math.Min(720, (int)viewport.Y - 48));
		return new Vector2I(width, height);
	}

	private void CopyBuildInformation()
	{
		var information = BuildIdentity.Current.ToPlainText();
		DisplayServer.ClipboardSet(information);
		if (_aboutCopyStatus is not null) _aboutCopyStatus.Text = "Copied.";
	}

	private void CloseOptions()
	{
		_optionsWindow?.Hide();
		if (_optionsReturnToRoutes) _routeSelectionPanel?.ReturnFromOptions();
		else if (_optionsReturnToWorkouts) _workoutSelectionPanel?.ShowSelection();
		else if (!_optionsReturnToRide) Show();
	}

	private void FitStartupPanel()
	{
		if (_startupPanel is null || !IsInsideTree()) return;
		var size = CalculateStartupPanelSize(GetViewportRect().Size);
		_startupPanel.CustomMinimumSize = new Vector2(size.X, size.Y);
		var width = size.X; var height = size.Y;
		_startupPanel.OffsetLeft = -width / 2; _startupPanel.OffsetRight = width / 2;
		_startupPanel.OffsetTop = -height / 2; _startupPanel.OffsetBottom = height / 2;
	}

	public override void _Notification(int what)
	{
		if (what == NotificationVisibilityChanged && Visible)
			_updatePanel?.OnStartupShown();
	}

	public override void _Process(double delta)
	{
		UpdatePreRideDeviceSummary();
		UpdateCompanionUi();
		if (_onlineStatusLabel is null || _rideSessionNode is null) return;
		var usingEos = OnlineStatusText.UsesEos(GameBootstrap.Settings.OnlineMultiplayerProvider);
		_onlineStatusLabel.Text = !_rideSessionNode.OnlineRidingEnabled
			? "Local rides are always available"
			: OnlineStatusText.ForStartup(_rideSessionNode.OnlineRidingEnabled,
			_rideSessionNode.IsMultiplayerConnected, _rideSessionNode.OnlineServiceAvailable,
			EpicOnlineServicesMultiplayerProvider.HasRequiredConfiguration, usingEos,
			EosNativeLibraryLoader.LoadFailed, EpicOnlineServicesMultiplayerProvider.ConfigurationInvalid);
		if (usingEos && _rideSessionNode.OnlineRidingEnabled)
			_onlineStatusLabel.Text += "\nWindows may request network access if direct peer connections need it.";
	}

	private void SaveCompanionUrl()
	{
		if (_optionsCompanionUrl is null) return;
		var value = _optionsCompanionUrl.Text.Trim();
		if (value.Length == 0) value = AppSettings.ProductionCompanionRelayBaseUrl;
		if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || !string.IsNullOrEmpty(uri.UserInfo)
			|| !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)
			|| (uri.Scheme != Uri.UriSchemeHttps && !(GameBootstrap.DebugModeEnabled && uri.IsLoopback && uri.Scheme == Uri.UriSchemeHttp)))
		{
			if (_optionsCompanionStatus is not null) _optionsCompanionStatus.Text = "Relay override must be an HTTPS address.";
			_optionsCompanionUrl.Text = GameBootstrap.Settings.CompanionRelayBaseUrl;
			return;
		}
		GameBootstrap.Settings.CompanionRelayBaseUrl = value;
		GameBootstrap.Settings.Save();
		ConfigureCompanion();
	}

	private void ConfigureCompanion()
	{
		_companionClient?.Configure(_rideSessionNode, GameBootstrap.Settings.CompanionEnabled, GameBootstrap.Settings.CompanionRelayBaseUrl ?? "");
	}

	private async Task StartCompanionPairingAsync()
	{
		SaveCompanionUrl();
		if (_companionClient is not null && await _companionClient.CreateInvitationAsync())
		{
			UpdateCompanionUi();
			await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
			var scroll = _optionsCompanionControls?.GetParent()?.GetParent() as ScrollContainer;
			if (scroll is not null)
				scroll.GetVScrollBar().Value = scroll.GetVScrollBar().MaxValue;
		}
		UpdateCompanionUi();
	}

	private void UpdateCompanionUi()
	{
		if (_optionsCompanionControls is null || _companionClient is null) return;
		_optionsCompanionControls.Visible = GameBootstrap.Settings?.CompanionEnabled == true;
		if (!_optionsCompanionControls.Visible) return;
		if (_optionsCompanionStatus is not null) _optionsCompanionStatus.Text = _companionClient.StatusText;
		if (_optionsCompanionCode is not null) _optionsCompanionCode.Text = _companionClient.ManualCode;
		if (_optionsCompanionQr is not null) _optionsCompanionQr.Texture = _companionClient.QrTexture;
		if (_optionsCompanionApprove is not null) _optionsCompanionApprove.Visible = _companionClient.PhonePending;
		if (_optionsCompanionReject is not null) _optionsCompanionReject.Visible = _companionClient.PhonePending;
		if (_optionsCompanionStart is not null) _optionsCompanionStart.Disabled = !_optionsCompanionClientReady();
		if (_optionsCompanionCancel is not null)
		{
			_optionsCompanionCancel.Visible = _companionClient.HasInvitation;
			_optionsCompanionCancel.Text = "Cancel pairing";
		}
		RefreshCompanionDeviceUi();
	}

	private void RefreshCompanionDeviceUi()
	{
		if (_optionsCompanionDevices is null || _companionClient is null) return;
		var devices = _companionClient.Devices;
		var signature = string.Join('|', devices.Select(device => $"{device.Id}:{device.LastSeenAt.ToUnixTimeSeconds()}"));
		if (signature == _lastCompanionDeviceSignature) return;
		_lastCompanionDeviceSignature = signature;
		foreach (var child in _optionsCompanionDevices.GetChildren()) child.QueueFree();
		foreach (var device in devices)
		{
			var row = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
			var label = new Label
			{
				Text = $"{device.Label} · last seen {device.LastSeenAt.ToLocalTime():g}",
				AutowrapMode = TextServer.AutowrapMode.WordSmart,
				SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
			};
			var revoke = new Button { Text = "Revoke", CustomMinimumSize = new Vector2(76, 36) };
			var id = device.Id;
			revoke.Pressed += async () => { await _companionClient.RevokeDeviceAsync(id); _lastCompanionDeviceSignature = ""; UpdateCompanionUi(); };
			row.AddChild(label); row.AddChild(revoke); _optionsCompanionDevices.AddChild(row);
		}
		if (_optionsCompanionRevokeAll is not null) _optionsCompanionRevokeAll.Visible = devices.Count > 0;
	}

	private bool _optionsCompanionClientReady() => _companionClient is not null && GameBootstrap.Settings.CompanionEnabled &&
		Uri.TryCreate(GameBootstrap.Settings.CompanionRelayBaseUrl, UriKind.Absolute, out var uri) &&
		(uri.Scheme == Uri.UriSchemeHttps || (GameBootstrap.DebugModeEnabled && uri.IsLoopback && uri.Scheme == Uri.UriSchemeHttp));

	private static void ConfigureStartupStatusLabel(Label? label, bool clipText = true)
	{
		if (label is null) return;
		label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
		label.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
		label.ClipText = clipText;
		label.AddThemeFontSizeOverride("font_size", 14);
		label.Modulate = new Color(0.78f, 0.78f, 0.72f, 0.9f);
	}

	private void BuildLocalSettingsUi()
	{
		var column = GetNodeOrNull<VBoxContainer>("PanelContainer/MarginContainer/StartupScroll/VBoxContainer");
		if (column is null) return;
		var profileRow = _profileRow = new HBoxContainer { Name = "ActiveProfileRow" }; profileRow.AddChild(new Label { Text = "Rider profile", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, VerticalAlignment = VerticalAlignment.Center });
		_profilePicker = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
		_profilePicker.ItemSelected += OnProfileSelected; profileRow.AddChild(_profilePicker);
		var manage = new Button { Text = "Manage Profiles" }; manage.Pressed += ShowProfileEditor; profileRow.AddChild(manage);
		column.AddChild(profileRow); column.MoveChild(profileRow, 2);
		var dummyRow = _dummySettingsRow = new GridContainer { Name = "DummyTrainerSettings", Columns = 2, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
		dummyRow.AddChild(new Label { Text = "Mode", VerticalAlignment = VerticalAlignment.Center });
		_dummyMode = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }; _dummyMode.AddItem("W/kg"); _dummyMode.AddItem("Watts"); _dummyMode.ItemSelected += _ => UpdateDummyModeUi(); dummyRow.AddChild(_dummyMode);
		dummyRow.AddChild(new Label { Text = "Power target", VerticalAlignment = VerticalAlignment.Center });
		_dummyPower = new SpinBox { MinValue = 0, MaxValue = 12, Step = .1, Value = 2, CustomMinimumSize = new Vector2(115, 0) };
		_dummyPower.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
		_dummyPower.ValueChanged += _ => SaveDummySettings(); dummyRow.AddChild(_dummyPower);
		dummyRow.AddChild(new Label { Text = "Cadence", VerticalAlignment = VerticalAlignment.Center });
		_dummyCadence = new SpinBox { MinValue = 0, MaxValue = 160, Step = 1, Value = 85, CustomMinimumSize = new Vector2(85, 0) };
		_dummyCadence.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
		_dummyCadence.ValueChanged += _ => SaveDummySettings(); dummyRow.AddChild(_dummyCadence);
		column.AddChild(dummyRow); column.MoveChild(dummyRow, 3);
		LoadDummySettingsToUi();
		BuildProfileEditor();
	}

	private void BuildPreRideHomeUi(VBoxContainer? startupColumn)
	{
		if (startupColumn is null) return;
		var startupScroll = GetNode<ScrollContainer>("PanelContainer/MarginContainer/StartupScroll");
		startupScroll.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
		startupScroll.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
		startupScroll.HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled;
		startupColumn.AddThemeConstantOverride("separation", 14);

		var title = startupColumn.GetNode<Label>("TitleLabel");
		title.Text = "Welcome to Wodra";
		title.AddThemeFontSizeOverride("font_size", 30);
		title.HorizontalAlignment = HorizontalAlignment.Left;
		title.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
		var status = _statusLabel;
		if (status is not null) status.Text = "Choose a route or workout. Your devices will connect as usual.";

		var cat = new TextureRect { Name = "WodraCatMark", Texture = GD.Load<Texture2D>("res://Assets/Branding/wodra-icon-128.png"), CustomMinimumSize = new Vector2(52, 52), ExpandMode = TextureRect.ExpandModeEnum.FitWidthProportional, StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered };
		var brand = new HBoxContainer { Name = "BrandHeader", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, Alignment = BoxContainer.AlignmentMode.Begin };
		brand.AddChild(cat);
		var titleText = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, Alignment = BoxContainer.AlignmentMode.Center };
		var brandName = new Label { Text = "Wodra", VerticalAlignment = VerticalAlignment.Bottom }; brandName.AddThemeFontSizeOverride("font_size", 26); titleText.AddChild(brandName);
		var subtitle = new Label { Text = "Indoor cycling", Modulate = new Color(0.78f, 0.82f, 0.82f, 1), VerticalAlignment = VerticalAlignment.Top }; subtitle.AddThemeFontSizeOverride("font_size", 13); titleText.AddChild(subtitle);
		brand.AddChild(titleText);
		var build = new Label { Name = "HomeBuildInfo", Text = BuildIdentity.Current.ApplicationVersion, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, SizeFlagsHorizontal = Control.SizeFlags.ShrinkEnd }; build.AddThemeFontSizeOverride("font_size", 12); build.Modulate = new Color(0.72f, 0.75f, 0.74f, 1); brand.AddChild(build);
		startupColumn.AddChild(brand);
		startupColumn.MoveChild(brand, 0);
		startupColumn.MoveChild(title, 1);
		if (status is not null) startupColumn.MoveChild(status, 2);
		startupColumn.MoveChild(_profileRow!, 3);

		var devicesHeading = new Label { Text = "Your devices" }; devicesHeading.AddThemeFontSizeOverride("font_size", 16); startupColumn.AddChild(devicesHeading);
		var devicesRow = new HBoxContainer { Name = "PreRideDeviceCards", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }; devicesRow.AddThemeConstantOverride("separation", 10); startupColumn.AddChild(devicesRow);
		(_trainerHomeName, _trainerHomeStatus) = AddDeviceSummaryCard(devicesRow, "Trainer", "TrainerSummary");
		(_heartRateHomeName, _heartRateHomeStatus) = AddDeviceSummaryCard(devicesRow, "Heart rate", "HeartRateSummary");
		(_steeringHomeName, _steeringHomeStatus) = AddDeviceSummaryCard(devicesRow, "Steering", "SteeringSummary");
		var dummyNotice = _dummyModeNotice = new Label { Name = "DummyTrainerActiveNotice", Text = "Test trainer active · ride eligibility may be limited", Visible = false, AutowrapMode = TextServer.AutowrapMode.WordSmart, Modulate = new Color(1.0f, 0.78f, 0.42f, 1) };
		startupColumn.AddChild(dummyNotice);

		var actionsTitle = new Label { Text = "Choose your ride", }; actionsTitle.AddThemeFontSizeOverride("font_size", 16); startupColumn.AddChild(actionsTitle);
		var rideChoices = new HBoxContainer { Name = "RideChoices", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }; rideChoices.AddThemeConstantOverride("separation", 12); startupColumn.AddChild(rideChoices);
		var routesButton = new Button { Name = "ChooseRouteButton", Text = "Ride a route", CustomMinimumSize = new Vector2(0, 62), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }; routesButton.AddThemeFontSizeOverride("font_size", 18); routesButton.Pressed += () => { Hide(); _routeSelectionPanel?.ShowRouteSelectionFromHome(); }; rideChoices.AddChild(routesButton);
		var workoutButton = new Button { Name = "ChooseWorkoutButton", Text = "Do a workout", CustomMinimumSize = new Vector2(0, 62), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }; workoutButton.AddThemeFontSizeOverride("font_size", 18); workoutButton.Pressed += () => { Hide(); _workoutSelectionPanel?.ShowSelection(); }; rideChoices.AddChild(workoutButton);

		var primary = startupColumn.GetNode<Button>("ConnectTrainerButton");
		primary.Text = "Connect devices";
		primary.CustomMinimumSize = new Vector2(0, 44);
		startupColumn.MoveChild(primary, startupColumn.GetChildCount() - 1);
		var secondary = new HBoxContainer { Name = "PreRideSecondaryActions", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }; secondary.AddThemeConstantOverride("separation", 8); startupColumn.AddChild(secondary);
		MoveToParent(startupColumn.GetNode<Button>("OptionsButton"), secondary, "Settings");
		MoveToParent(startupColumn.GetNode<Button>("HistoryButton"), secondary, "Ride History");
		// Keep Quit available to ordinary riders, but visually secondary to riding actions.
		_quitButton = startupColumn.GetNode<Button>("QuitButton");
		MoveToParent(_quitButton, secondary, "Quit");
		_quitButton.SizeFlagsHorizontal = Control.SizeFlags.ShrinkEnd;
		_quitButton.CustomMinimumSize = new Vector2(92, 0);

		// Testing, diagnostics and debug preferences remain available but no longer
		// compete with normal ride actions on launch.
		_dummyStartButton = startupColumn.GetNode<Button>("Test with Fake Trainer");
		_dummyStartButton.Text = "Start with Dummy Trainer";
		_dummyStartButton.CustomMinimumSize = new Vector2(0, 40);
		_dummyStartButton.Visible = false;
		if (_dummySettingsRow is not null) _dummySettingsRow.Visible = false;
		_fitReplayButton = startupColumn.GetNode<Button>("LoadFitButton"); _fitReplayButton.Visible = false;
		_debugModeCheckButton!.Visible = false;
		foreach (var child in startupColumn.GetChildren())
			if (child is Button button && button.Name == "LocalDiagnosticsButton") { _diagnosticsButton = button; button.Visible = false; }

		UpdatePreRideDeviceSummary();
	}

	private static (Label Name, Label Status) AddDeviceSummaryCard(HBoxContainer row, string title, string name)
	{
		var card = new PanelContainer { Name = name, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsStretchRatio = 1 };
		var margin = new MarginContainer();
		foreach (var (key, value) in new[] { ("margin_left", 12), ("margin_right", 12), ("margin_top", 10), ("margin_bottom", 10) }) margin.AddThemeConstantOverride(key, value);
		card.AddChild(margin);
		var content = new VBoxContainer(); content.AddThemeConstantOverride("separation", 4); margin.AddChild(content);
		var heading = new Label { Text = title }; heading.AddThemeFontSizeOverride("font_size", 12); heading.Modulate = new Color(0.76f, 0.80f, 0.79f, 1); content.AddChild(heading);
		var deviceName = new Label { Name = "DeviceName", Text = title, AutowrapMode = TextServer.AutowrapMode.WordSmart, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }; deviceName.AddThemeFontSizeOverride("font_size", 16); content.AddChild(deviceName);
		var state = new Label { Name = "DeviceState", Text = "Checking…", AutowrapMode = TextServer.AutowrapMode.WordSmart, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }; state.AddThemeFontSizeOverride("font_size", 12); state.Modulate = new Color(0.77f, 0.79f, 0.76f, 1); content.AddChild(state);
		row.AddChild(card);
		return (deviceName, state);
	}

	private static void MoveToParent(Button control, Container parent, string text)
	{
		control.Reparent(parent);
		control.Text = text;
		control.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
	}

	private void UpdatePreRideDeviceSummary()
	{
		if (_trainerHomeName is null || _deviceConnectionPanel is null) return;
		_trainerHomeName.Text = _deviceConnectionPanel.PreRideTrainerName;
		_trainerHomeStatus!.Text = _deviceConnectionPanel.PreRideTrainerStatus;
		_heartRateHomeName!.Text = _deviceConnectionPanel.PreRideHeartRateName;
		_heartRateHomeStatus!.Text = _deviceConnectionPanel.PreRideHeartRateStatus;
		_steeringHomeName!.Text = _deviceConnectionPanel.PreRideSteeringName;
		_steeringHomeStatus!.Text = _deviceConnectionPanel.PreRideSteeringStatus;
		if (_dummyModeNotice is not null) _dummyModeNotice.Visible = GameBootstrap.TrainerClient is IndoorCycling.Trainer.Implementations.FakeTrainerClient;
	}

	internal static Vector2I CalculateStartupPanelSize(Vector2 viewport)
	{
		var width = Math.Min(1120, Math.Max(360, (int)viewport.X - 40));
		var height = Math.Min(820, Math.Max(360, (int)viewport.Y - 40));
		return new Vector2I(width, height);
	}

	private async Task RefreshProfilesAsync()
		=> await RefreshProfilesAsync(openCreateProfileWhenEmpty: false);

	private async Task RefreshProfilesAsync(bool openCreateProfileWhenEmpty)
	{
		try
		{
			await GameBootstrap.GetPersistentRiderAsync();
			_profiles = (await GameBootstrap.RideHistory!.GetAllProfilesAsync()).ToList();
			if (_profilePicker is null) return;
			_profilePicker.Clear();
			for (var i = 0; i < _profiles.Count; i++)
			{
				var profile = _profiles[i]; _profilePicker.AddItem(profile.Name + (profile.IsTestProfile ? " (Test)" : ""));
				if (profile.Id == GameBootstrap.Settings.ActiveProfileId) _profilePicker.Select(i);
			}
			if (_profileEditPicker is not null)
			{
				_profileEditPicker.Clear();
				foreach (var profile in _profiles) _profileEditPicker.AddItem(profile.Name);
				if (_profileEditPicker.ItemCount > 0) _profileEditPicker.Select(0);
			}
			if (RiderProfileStartupSelection.ShouldAutoOpenCreateEditor(_profiles.Count, openCreateProfileWhenEmpty))
			{
				StartNewProfile();
				_profileWindow?.PopupCentered();
			}
		}
		catch (Exception ex) { SetStatus($"Profiles unavailable: {ex.Message}"); }
	}

	private async void OnProfileSelected(long index)
	{
		if (index < 0 || index >= _profiles.Count) return;
		try { await GameBootstrap.SetActiveProfileAsync(_profiles[(int)index].Id); ConfigureCompanion(); await RefreshProfilesAsync(); }
		catch (Exception ex) { SetStatus($"Profile could not be selected: {ex.Message}"); }
	}

	private void BuildProfileEditor()
	{
		_profileWindow = new Window { Title = "Manage Rider Profiles", Size = new Vector2I(480, 560), MinSize = new Vector2I(440, 500), Exclusive = true };
		var root = new VBoxContainer(); root.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
		var margin = new MarginContainer(); margin.AddThemeConstantOverride("margin_left", 16); margin.AddThemeConstantOverride("margin_right", 16); margin.AddThemeConstantOverride("margin_top", 16); margin.AddThemeConstantOverride("margin_bottom", 16);
		var body = new VBoxContainer(); margin.AddChild(body); root.AddChild(margin); _profileWindow.AddChild(root); AddChild(_profileWindow);
		_profileEditPicker = new OptionButton(); _profileEditPicker.ItemSelected += OnEditProfileSelected; body.AddChild(_profileEditPicker);
		var newButton = new Button { Text = "New Profile" }; newButton.Pressed += StartNewProfile; body.AddChild(newButton);
		_profileName = new LineEdit { PlaceholderText = "Name" }; body.AddChild(_profileName);
		_profileBody = ProfileSpin(30, 200, 75); body.AddChild(Labeled("Body weight (kg)", _profileBody));
		_profileBike = ProfileSpin(4, 30, 8); body.AddChild(Labeled("Bike weight (kg)", _profileBike));
		_profileTrainerDifficulty = new HSlider { MinValue = 0, MaxValue = 100, Step = 5, Value = TrainerDifficultyPolicy.DefaultPercent, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
		_profileTrainerDifficultyValue = new Label { Text = $"{TrainerDifficultyPolicy.DefaultPercent}%", CustomMinimumSize = new Vector2(48, 0) };
		_profileTrainerDifficulty.ValueChanged += value => _profileTrainerDifficultyValue.Text = $"{value:0}%";
		var difficultyRow = new HBoxContainer();
		difficultyRow.AddChild(new Label { Text = "Trainer difficulty", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
		difficultyRow.AddChild(_profileTrainerDifficulty); difficultyRow.AddChild(_profileTrainerDifficultyValue); body.AddChild(difficultyRow);
		_profileTest = new CheckButton { Text = "Test profile" }; body.AddChild(_profileTest);
		_stravaProfileStatus = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
		body.AddChild(_stravaProfileStatus);
		_stravaAutoUpload = new CheckButton { Text = "Automatically upload completed rides to Strava", Disabled = true, AutowrapMode = TextServer.AutowrapMode.WordSmart };
		_stravaAutoUpload.Toggled += OnAutomaticUploadToggled;
		body.AddChild(_stravaAutoUpload);
		body.AddChild(new Label { Text = "When enabled, future completed rides recorded by this profile are queued after their FIT files are saved. Test profiles and synthetic trainer rides are excluded; existing rides are never queued.", AutowrapMode = TextServer.AutowrapMode.WordSmart });
		var stravaActions = new HBoxContainer();
		_stravaLinkButton = new Button { Text = "Link Strava" }; _stravaLinkButton.Pressed += OnLinkStravaPressed; stravaActions.AddChild(_stravaLinkButton);
		_stravaDisconnectButton = new Button { Text = "Disconnect Strava" }; _stravaDisconnectButton.Pressed += OnDisconnectStravaPressed; stravaActions.AddChild(_stravaDisconnectButton);
		_stravaCancelButton = new Button { Text = "Cancel linking", Visible = false }; _stravaCancelButton.Pressed += () => _stravaLinkCancellation?.Cancel(); stravaActions.AddChild(_stravaCancelButton);
		body.AddChild(stravaActions);
		var buttons = new HBoxContainer();
		var save = new Button { Text = "Save" }; save.Pressed += SaveEditedProfile; buttons.AddChild(save);
		var cancel = new Button { Text = "Cancel" }; cancel.Pressed += () => _profileWindow.Hide(); buttons.AddChild(cancel); body.AddChild(buttons);
		_profileWindow.Hide();
	}

	private static SpinBox ProfileSpin(double min, double max, double value) => new() { MinValue = min, MaxValue = max, Step = .1, Value = value };
	private static HBoxContainer Labeled(string title, Control field) { var row = new HBoxContainer(); row.AddChild(new Label { Text = title, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }); row.AddChild(field); return row; }
	private async void ShowProfileEditor()
	{
		await RefreshProfilesAsync();
		var profile = _profiles.FirstOrDefault(p => p.Id == GameBootstrap.Settings.ActiveProfileId) ?? _profiles.FirstOrDefault();
		if (profile is not null) LoadProfileEditor(profile); else StartNewProfile();
		_profileWindow?.PopupCentered();
	}
	private void OnEditProfileSelected(long index) { if (index >= 0 && index < _profiles.Count) LoadProfileEditor(_profiles[(int)index]); }
	private void LoadProfileEditor(RiderProfileRecord profile) { _stravaLinkCancellation?.Cancel(); _editingProfileId = profile.Id; _profileName!.Text = profile.Name; _profileBody!.Value = profile.BodyWeightKg; _profileBike!.Value = profile.BikeWeightKg; _profileTest!.ButtonPressed = profile.IsTestProfile; _profileTrainerDifficulty!.Value = profile.TrainerDifficultyPercent; _profileTrainerDifficultyValue!.Text = $"{profile.TrainerDifficultyPercent}%"; RefreshStravaProfileUi(); }
	private void StartNewProfile() { _stravaLinkCancellation?.Cancel(); _editingProfileId = null; _profileName!.Text = ""; _profileBody!.Value = 75; _profileBike!.Value = 8; _profileTest!.ButtonPressed = true; _profileTrainerDifficulty!.Value = TrainerDifficultyPolicy.DefaultPercent; _profileTrainerDifficultyValue!.Text = $"{TrainerDifficultyPolicy.DefaultPercent}%"; RefreshStravaProfileUi(); _profileName.GrabFocus(); }
	private void RefreshStravaProfileUi(bool refreshStatus = true)
	{
		if (_stravaProfileStatus is null) return;
		var link = _editingProfileId is { } id ? StravaIntegrationClient.Shared.GetLink(id) : null;
		if (refreshStatus)
			_stravaProfileStatus.Text = link is null
				? _editingProfileId is null ? "Save this profile before linking Strava." : "No Strava account linked to this profile."
				: $"Linked to Strava: {link.AthleteName} (athlete {link.AthleteId}). Uploads from this profile go to this account.";
		if (_stravaLinkButton is not null) _stravaLinkButton.Visible = link is null;
		if (_stravaDisconnectButton is not null) _stravaDisconnectButton.Visible = link is not null;
		if (_stravaLinkButton is not null) _stravaLinkButton.Disabled = _editingProfileId is null || _stravaLinkCancellation is not null;
		if (_stravaAutoUpload is not null)
		{
			_stravaAutoUpload.SetPressedNoSignal(link?.AutomaticUploadEnabled == true);
			_stravaAutoUpload.Disabled = link is null;
			_stravaAutoUpload.TooltipText = link is null ? "Link Strava to enable this option." : "Only future completed rides recorded by this profile are considered. Test profiles and synthetic trainer rides are never uploaded automatically.";
		}
	}
	private async void OnAutomaticUploadToggled(bool enabled)
	{
		if (_stravaAutoUpload is not null) _stravaAutoUpload.Disabled = true;
		if (_editingProfileId is not { } profileId || StravaIntegrationClient.Shared.GetLink(profileId) is null)
		{
			if (_stravaAutoUpload is not null) _stravaAutoUpload.SetPressedNoSignal(false);
			if (_stravaAutoUpload is not null) _stravaAutoUpload.Disabled = StravaIntegrationClient.Shared.GetLink(_editingProfileId ?? Guid.Empty) is null;
			return;
		}
		try { await StravaIntegrationClient.Shared.SetAutomaticUploadAsync(profileId, enabled); }
		catch (Exception ex) { if (_stravaAutoUpload is not null) _stravaAutoUpload.SetPressedNoSignal(!enabled); SetStatus($"Automatic Strava upload setting could not be saved: {ex.Message}"); }
		finally { if (_stravaAutoUpload is not null) _stravaAutoUpload.Disabled = StravaIntegrationClient.Shared.GetLink(profileId) is null; }
	}
	private async void OnLinkStravaPressed()
	{
		if (_editingProfileId is not { } profileId || _stravaLinkCancellation is not null) return;
		_stravaLinkCancellation = new CancellationTokenSource();
		if (_stravaLinkButton is not null) _stravaLinkButton.Disabled = true;
		if (_stravaCancelButton is not null) _stravaCancelButton.Visible = true;
		if (_stravaProfileStatus is not null) _stravaProfileStatus.Text = "Opening Strava authorization in your browser…";
		try
		{
			var linked = await StravaIntegrationClient.Shared.LinkAsync(profileId, _stravaLinkCancellation.Token);
			if (_stravaProfileStatus is not null) _stravaProfileStatus.Text = $"Linked to Strava: {linked.AthleteName} (athlete {linked.AthleteId}).";
		}
		catch (OperationCanceledException)
		{
			if (_stravaProfileStatus is not null) _stravaProfileStatus.Text = "Strava linking was cancelled. This profile remains unlinked.";
		}
		catch (Exception ex)
		{
			if (_stravaProfileStatus is not null) _stravaProfileStatus.Text = ex.Message;
		}
		finally
		{
			_stravaLinkCancellation.Dispose(); _stravaLinkCancellation = null;
			if (_stravaCancelButton is not null) _stravaCancelButton.Visible = false;
			RefreshStravaProfileUi(refreshStatus: false);
		}
	}
	private async void OnDisconnectStravaPressed()
	{
		if (_editingProfileId is not { } profileId) return;
		if (_stravaDisconnectButton is not null) _stravaDisconnectButton.Disabled = true;
		var revokedRemotely = true;
		try { revokedRemotely = await StravaIntegrationClient.Shared.DisconnectAsync(profileId); }
		catch (Exception ex) { if (_stravaProfileStatus is not null) _stravaProfileStatus.Text = ex.Message; }
		finally
		{
			if (_stravaDisconnectButton is not null) _stravaDisconnectButton.Disabled = false;
			RefreshStravaProfileUi();
			if (!revokedRemotely && _stravaProfileStatus is not null)
				_stravaProfileStatus.Text = "Disconnected on this PC. Wodra could not reach Strava to revoke access; remove Wodra in Strava's Connected Apps if you want to revoke it now.";
		}
	}
	private async void SaveEditedProfile()
	{
		try
		{
			var now = DateTimeOffset.UtcNow;
			var existing = _profiles.FirstOrDefault(p => p.Id == _editingProfileId);
			var profile = new RiderProfileRecord(_editingProfileId ?? Guid.NewGuid(), _profileName!.Text.Trim(), existing?.CreatedAtUtc ?? now, now,
				_profileBody!.Value, _profileBike!.Value, _profileTest!.ButtonPressed, (int)_profileTrainerDifficulty!.Value);
			await GameBootstrap.SaveProfileAsync(profile);
			if (RiderProfileStartupSelection.ShouldActivateProfileAfterSave(_editingProfileId is null, GameBootstrap.Settings.ActiveProfileId))
				await GameBootstrap.SetActiveProfileAsync(profile.Id);
			_profileWindow?.Hide(); await RefreshProfilesAsync();
		}
		catch (Exception ex) { SetStatus($"Profile was not saved: {ex.Message}"); }
	}

	private void LoadDummySettingsToUi()
	{
		var settings = GameBootstrap.Settings.DummyTrainer; settings.Validate();
		if (_dummyMode is null) return;
		_dummyMode.Select(settings.Mode == "Watts" ? 1 : 0);
		_dummyPower!.MinValue = 0; _dummyPower.MaxValue = settings.Mode == "Watts" ? 1500 : 12;
		_dummyPower.Step = settings.Mode == "Watts" ? 5 : .1; _dummyPower.Value = settings.Mode == "Watts" ? settings.TargetWatts : settings.WattsPerKg;
		_dummyCadence!.Value = settings.CadenceRpm;
	}
	private void UpdateDummyModeUi()
	{
		if (_dummyMode is null || _dummyPower is null) return;
		var settings = GameBootstrap.Settings.DummyTrainer;
		if (settings.Mode == "W/kg") settings.WattsPerKg = _dummyPower.Value; else settings.TargetWatts = (int)_dummyPower.Value;
		settings.Mode = _dummyMode.Selected == 1 ? "Watts" : "W/kg";
		LoadDummySettingsToUi(); SaveDummySettings();
	}
	private void SaveDummySettings()
	{
		if (_dummyPower is null || _dummyMode is null) return;
		var settings = GameBootstrap.Settings.DummyTrainer; settings.Mode = _dummyMode.Selected == 1 ? "Watts" : "W/kg";
		if (settings.Mode == "Watts") settings.TargetWatts = (int)_dummyPower.Value; else settings.WattsPerKg = _dummyPower.Value;
		settings.CadenceRpm = (int)(_dummyCadence?.Value ?? 85); settings.Validate(); GameBootstrap.Settings.Save();
	}

	private void OnDebugModeToggled(bool enabled)
	{
		GameBootstrap.Settings.DebugModeEnabled = enabled;
		GameBootstrap.Settings.Save();
		_typedHudController?.SetDebugMode(enabled);
	}

	private async void OnOnlineRidingToggled(bool enabled)
	{
		if (_rideSessionNode is null) return;
		if (_optionsOnlineToggle is not null) _optionsOnlineToggle.Disabled = true;
		try { await _rideSessionNode.SetOnlineRidingEnabledAsync(enabled); }
		catch (Exception ex)
		{
			GameLogger.Log("multiplayer_startup_toggle_failed", new { message = ex.Message });
			if (_onlineStatusLabel is not null) _onlineStatusLabel.Text = "Online unavailable";
		}
		finally { if (_optionsOnlineToggle is not null) _optionsOnlineToggle.Disabled = false; }
	}

	private void OnConnectTrainerPressed()
	{
		if (_deviceConnectionPanel is null)
		{
			SetStatus("Device connection panel not found.");
			return;
		}

		Hide();
		_deviceConnectionPanel.ShowTrainerConnectionScreen();
	}

	private void OnQuickFakeTrainerPressed()
	{
		if (_routeSelectionPanel is null)
		{
			SetStatus("Route selection panel not found.");
			return;
		}

		// The entry point now lives in an exclusive Options window. Close it before
		// opening the existing route selector so the new screen is not obscured.
		_optionsWindow?.Hide();
		GameBootstrap.ConfigureFakeTrainerMode();

		Hide();
		_hudController?.Hide();
		_routeSelectionPanel.ShowRouteSelectionFromHome();
	}

	private void OnLoadFitPressed()
	{
		if (_fileDialog is null)
		{
			SetStatus("FileDialog not found.");
			return;
		}

		// FIT replay is launched from Advanced Options; remove its exclusive window
		// before opening the existing file picker.
		_optionsWindow?.Hide();
		_fileDialog.PopupCenteredRatio(0.7f);
	}

	private async void OnFitFileSelected(string path)
	{
		if (_rideSessionNode is null)
		{
			SetStatus("RideSessionNode not found.");
			return;
		}

		GameBootstrap.ConfigureFitReplayMode(path);
		_hudController?.Show();
		Hide();

		await _rideSessionNode.StartFitReplaySessionAsync(path);
	}

	private async void OnQuitPressed()
	{
		if (_rideSessionNode is not null)
			await _rideSessionNode.QuitAsync();
	}

	private void SetStatus(string text)
	{
		if (_statusLabel is not null)
			_statusLabel.Text = text;
	}
}
