using Godot;




public partial class StartPanelController : Control
{
	private RideSessionNode? _rideSessionNode;
	private FileDialog? _fileDialog;
	private Label? _statusLabel;

	private Control? _hudController;

	private DeviceConnectionPanelController? _deviceConnectionPanel;

	private RouteSelectionPanelController? _routeSelectionPanel;
	private CheckButton? _debugModeCheckButton;
	private HudController? _typedHudController;

	public override void _Ready()
	{
		_rideSessionNode = GetNodeOrNull<RideSessionNode>("../RideSessionNode");
		_fileDialog = GetNodeOrNull<FileDialog>("FileDialog");
		_statusLabel = GetNodeOrNull<Label>("PanelContainer/MarginContainer/VBoxContainer/StatusLabel");
		_deviceConnectionPanel = GetNodeOrNull<DeviceConnectionPanelController>("../DeviceConnectionPanel");
		_routeSelectionPanel = GetNodeOrNull<RouteSelectionPanelController>("../RouteSelectionPanel");
		_hudController = GetNodeOrNull<Control>("../HudController");
		_typedHudController = _hudController as HudController;
		_debugModeCheckButton = GetNode<CheckButton>("PanelContainer/MarginContainer/VBoxContainer/DebugModeCheckButton");
		_debugModeCheckButton.ButtonPressed = GameBootstrap.DebugModeEnabled;
		_debugModeCheckButton.Toggled += OnDebugModeToggled;
		_typedHudController?.SetDebugMode(GameBootstrap.DebugModeEnabled);
		_hudController?.Hide();
		GetNode<Button>("PanelContainer/MarginContainer/VBoxContainer/ConnectTrainerButton").Pressed += OnConnectTrainerPressed;
		GetNode<Button>("PanelContainer/MarginContainer/VBoxContainer/Test with Fake Trainer").Pressed += OnQuickFakeTrainerPressed;
		GetNode<Button>("PanelContainer/MarginContainer/VBoxContainer/LoadFitButton").Pressed += OnLoadFitPressed;
		GetNode<Button>("PanelContainer/MarginContainer/VBoxContainer/QuitButton").Pressed += OnQuitPressed;

		if (_fileDialog is not null)
		{
			_fileDialog.FileSelected += OnFitFileSelected;
		}
	}

	private void OnDebugModeToggled(bool enabled)
	{
		GameBootstrap.Settings.DebugModeEnabled = enabled;
		GameBootstrap.Settings.Save();
		_typedHudController?.SetDebugMode(enabled);
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
