using Godot;
using System.Collections.Generic;
using System.Linq;
using System;
using System.Threading.Tasks;
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
	private CheckButton? _debugModeCheckButton;
	private CheckButton? _onlineRidingCheckButton;
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
	private List<RiderProfileRecord> _profiles = [];
	private Guid? _editingProfileId;
	private VelopackUpdatePanel? _updatePanel;

	public override void _Ready()
	{
		_rideSessionNode = GetNodeOrNull<RideSessionNode>("../RideSessionNode");
		_fileDialog = GetNodeOrNull<FileDialog>("FileDialog");
		_statusLabel = GetNodeOrNull<Label>("PanelContainer/MarginContainer/VBoxContainer/StatusLabel");
		_deviceConnectionPanel = GetNodeOrNull<DeviceConnectionPanelController>("../DeviceConnectionPanel");
		_routeSelectionPanel = GetNodeOrNull<RouteSelectionPanelController>("../RouteSelectionPanel");
		_hudController = GetNodeOrNull<Control>("../HudController");
		_typedHudController = _hudController as HudController;
		_rideHistoryPanel = GetNodeOrNull<RideHistoryPanelController>("../RideHistoryPanel");
		BuildLocalSettingsUi();
		_ = RefreshProfilesAsync(openCreateProfileWhenEmpty: true);
		_debugModeCheckButton = GetNode<CheckButton>("PanelContainer/MarginContainer/VBoxContainer/DebugModeCheckButton");
		_debugModeCheckButton.ButtonPressed = GameBootstrap.DebugModeEnabled;
		_debugModeCheckButton.Toggled += OnDebugModeToggled;
		_typedHudController?.SetDebugMode(GameBootstrap.DebugModeEnabled);
		_onlineRidingCheckButton = GetNodeOrNull<CheckButton>("PanelContainer/MarginContainer/VBoxContainer/OnlineRidingCheckButton");
		_onlineStatusLabel = GetNodeOrNull<Label>("PanelContainer/MarginContainer/VBoxContainer/OnlineStatusLabel");
		ConfigureStartupStatusLabel(_statusLabel);
		ConfigureStartupStatusLabel(_onlineStatusLabel);
		var startupColumn = GetNodeOrNull<VBoxContainer>("PanelContainer/MarginContainer/VBoxContainer");
		if (startupColumn is not null)
		{
			_updatePanel = new VelopackUpdatePanel();
			startupColumn.AddChild(_updatePanel);
			// Keep update status/actions with the startup choices, before the terminal
			// Quit action, rather than extending the menu below it.
			var quitButton = startupColumn.GetNodeOrNull<Button>("QuitButton");
			if (quitButton is not null)
				startupColumn.MoveChild(_updatePanel, quitButton.GetIndex());
			_updatePanel.Attach();
		}
		if (_onlineRidingCheckButton is not null)
		{
			_onlineRidingCheckButton.ButtonPressed = GameBootstrap.Settings.OnlineRidingEnabled;
			_onlineRidingCheckButton.Toggled += OnOnlineRidingToggled;
			if (GameBootstrap.Settings.OnlineRidingEnabled && _rideSessionNode is not null)
				_ = _rideSessionNode.SetOnlineRidingEnabledAsync(true);
		}
		_hudController?.Hide();
		GetNode<Button>("PanelContainer/MarginContainer/VBoxContainer/ConnectTrainerButton").Pressed += OnConnectTrainerPressed;
		GetNode<Button>("PanelContainer/MarginContainer/VBoxContainer/Test with Fake Trainer").Pressed += OnQuickFakeTrainerPressed;
		GetNode<Button>("PanelContainer/MarginContainer/VBoxContainer/LoadFitButton").Pressed += OnLoadFitPressed;
		GetNode<Button>("PanelContainer/MarginContainer/VBoxContainer/HistoryButton").Pressed += () => _rideHistoryPanel?.ShowHistory();
		GetNode<Button>("PanelContainer/MarginContainer/VBoxContainer/QuitButton").Pressed += OnQuitPressed;

		if (_fileDialog is not null)
		{
			_fileDialog.FileSelected += OnFitFileSelected;
		}
	}

	public override void _Notification(int what)
	{
		if (what == NotificationVisibilityChanged && Visible)
			_updatePanel?.OnStartupShown();
	}

	public override void _Process(double delta)
	{
		if (_onlineStatusLabel is null || _rideSessionNode is null) return;
		var usingEos = OnlineStatusText.UsesEos(GameBootstrap.Settings.OnlineMultiplayerProvider);
		_onlineStatusLabel.Text = OnlineStatusText.ForStartup(_rideSessionNode.OnlineRidingEnabled,
			_rideSessionNode.IsMultiplayerConnected, _rideSessionNode.OnlineServiceAvailable,
			EpicOnlineServicesMultiplayerProvider.HasRequiredConfiguration, usingEos,
			EosNativeLibraryLoader.LoadFailed, EpicOnlineServicesMultiplayerProvider.ConfigurationInvalid);
		if (usingEos && _rideSessionNode.OnlineRidingEnabled)
			_onlineStatusLabel.Text += "\nWindows may request network access if direct peer connections need it.";
	}

	private static void ConfigureStartupStatusLabel(Label? label)
	{
		if (label is null) return;
		label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
		label.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
		label.ClipText = true;
		label.AddThemeFontSizeOverride("font_size", 14);
		label.Modulate = new Color(0.78f, 0.78f, 0.72f, 0.9f);
	}

	private void BuildLocalSettingsUi()
	{
		var column = GetNodeOrNull<VBoxContainer>("PanelContainer/MarginContainer/VBoxContainer");
		if (column is null) return;
		var profileRow = new HBoxContainer(); profileRow.AddChild(new Label { Text = "Profile:" });
		_profilePicker = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
		_profilePicker.ItemSelected += OnProfileSelected; profileRow.AddChild(_profilePicker);
		var manage = new Button { Text = "Manage Profiles" }; manage.Pressed += ShowProfileEditor; profileRow.AddChild(manage);
		column.AddChild(profileRow); column.MoveChild(profileRow, 2);
		var dummyRow = new HBoxContainer { Name = "DummyTrainerSettings" };
		dummyRow.AddChild(new Label { Text = "Dummy:" });
		_dummyMode = new OptionButton(); _dummyMode.AddItem("W/kg"); _dummyMode.AddItem("Watts"); _dummyMode.ItemSelected += _ => UpdateDummyModeUi(); dummyRow.AddChild(_dummyMode);
		_dummyPower = new SpinBox { MinValue = 0, MaxValue = 12, Step = .1, Value = 2, CustomMinimumSize = new Vector2(115, 0) };
		_dummyPower.ValueChanged += _ => SaveDummySettings(); dummyRow.AddChild(_dummyPower);
		dummyRow.AddChild(new Label { Text = "Cadence" });
		_dummyCadence = new SpinBox { MinValue = 0, MaxValue = 160, Step = 1, Value = 85, CustomMinimumSize = new Vector2(85, 0) };
		_dummyCadence.ValueChanged += _ => SaveDummySettings(); dummyRow.AddChild(_dummyCadence);
		column.AddChild(dummyRow); column.MoveChild(dummyRow, 3);
		LoadDummySettingsToUi();
		BuildProfileEditor();
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
		try { await GameBootstrap.SetActiveProfileAsync(_profiles[(int)index].Id); await RefreshProfilesAsync(); }
		catch (Exception ex) { SetStatus($"Profile could not be selected: {ex.Message}"); }
	}

	private void BuildProfileEditor()
	{
		_profileWindow = new Window { Title = "Manage Rider Profiles", Size = new Vector2I(460, 410), Exclusive = true };
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
	private void LoadProfileEditor(RiderProfileRecord profile) { _editingProfileId = profile.Id; _profileName!.Text = profile.Name; _profileBody!.Value = profile.BodyWeightKg; _profileBike!.Value = profile.BikeWeightKg; _profileTest!.ButtonPressed = profile.IsTestProfile; _profileTrainerDifficulty!.Value = profile.TrainerDifficultyPercent; _profileTrainerDifficultyValue!.Text = $"{profile.TrainerDifficultyPercent}%"; }
	private void StartNewProfile() { _editingProfileId = null; _profileName!.Text = ""; _profileBody!.Value = 75; _profileBike!.Value = 8; _profileTest!.ButtonPressed = true; _profileTrainerDifficulty!.Value = TrainerDifficultyPolicy.DefaultPercent; _profileTrainerDifficultyValue!.Text = $"{TrainerDifficultyPolicy.DefaultPercent}%"; _profileName.GrabFocus(); }
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
		if (_onlineRidingCheckButton is not null) _onlineRidingCheckButton.Disabled = true;
		try { await _rideSessionNode.SetOnlineRidingEnabledAsync(enabled); }
		catch (Exception ex)
		{
			GameLogger.Log("multiplayer_startup_toggle_failed", new { message = ex.Message });
			if (_onlineStatusLabel is not null) _onlineStatusLabel.Text = "Online unavailable";
		}
		finally { if (_onlineRidingCheckButton is not null) _onlineRidingCheckButton.Disabled = false; }
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

		GameBootstrap.ConfigureFakeTrainerMode();

		Hide();
		_hudController?.Hide();
		_routeSelectionPanel.ShowRouteSelection();
	}

	private void OnLoadFitPressed()
	{
		if (_fileDialog is null)
		{
			SetStatus("FileDialog not found.");
			return;
		}

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
