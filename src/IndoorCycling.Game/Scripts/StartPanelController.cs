using Godot;




public partial class StartPanelController : Control
{
	private RideSessionNode? _rideSessionNode;
	private FileDialog? _fileDialog;
	private Label? _statusLabel;

	private Control? _hudController;

	private DeviceConnectionPanelController? _deviceConnectionPanel;

	public override void _Ready()
	{
		_rideSessionNode = GetNodeOrNull<RideSessionNode>("../RideSessionNode");
		_fileDialog = GetNodeOrNull<FileDialog>("FileDialog");
		_statusLabel = GetNodeOrNull<Label>("PanelContainer/MarginContainer/VBoxContainer/StatusLabel");
		_deviceConnectionPanel = GetNodeOrNull<DeviceConnectionPanelController>("../DeviceConnectionPanel");
		_hudController = GetNodeOrNull<Control>("../HudController");
		_hudController?.Hide();

		if (_fileDialog is not null)
		{
			_fileDialog.FileSelected += OnFitFileSelected;
		}
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

	private async void OnQuickFakeTrainerPressed()
	{
		if (_rideSessionNode is null)
		{
			SetStatus("RideSessionNode not found.");
			return;
		}

		GameBootstrap.ConfigureFakeTrainerMode();

		_hudController?.Show();
		Hide();

		await _rideSessionNode.StartTrainerSessionAsync();
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

	private void OnQuitPressed()
	{
		GetTree().Quit();
	}

	private void SetStatus(string text)
	{
		if (_statusLabel is not null)
			_statusLabel.Text = text;
	}
}
