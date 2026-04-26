using System.Threading.Tasks;
using Godot;

public partial class DeviceConnectionPanelController : Control
{
	private RideSessionNode? _rideSessionNode;
	private Control? _hudController;
	private Control? _startPanel;

	private Label? _overallStatusLabel;

	private Label? _powerDeviceLabel;
	private Label? _powerStatusLabel;

	private Label? _cadenceDeviceLabel;
	private Label? _cadenceStatusLabel;

	private Label? _controlDeviceLabel;
	private Label? _controlStatusLabel;

	private Label? _steeringDeviceLabel;
	private Label? _steeringStatusLabel;

	private Label? _heartRateDeviceLabel;
	private Label? _heartRateStatusLabel;

	private Button? _continueButton;

	public override void _Ready()
	{
		_rideSessionNode = GetNodeOrNull<RideSessionNode>("../../RideSessionNode");
		_hudController = GetNodeOrNull<Control>("../HudController");
		_startPanel = GetNodeOrNull<Control>("../StartPanel");

		_overallStatusLabel = GetNodeOrNull<Label>("PanelContainer/MarginContainer/VBoxContainer/OverallStatusLabel");

		_powerDeviceLabel = GetNodeOrNull<Label>("PanelContainer/MarginContainer/VBoxContainer/GridContainer/PowerCard/MarginContainer/VBoxContainer/DeviceLabel");
		_powerStatusLabel = GetNodeOrNull<Label>("PanelContainer/MarginContainer/VBoxContainer/GridContainer/PowerCard/MarginContainer/VBoxContainer/StatusLabel");

		_cadenceDeviceLabel = GetNodeOrNull<Label>("PanelContainer/MarginContainer/VBoxContainer/GridContainer/CadenceCard/MarginContainer/VBoxContainer/DeviceLabel");
		_cadenceStatusLabel = GetNodeOrNull<Label>("PanelContainer/MarginContainer/VBoxContainer/GridContainer/CadenceCard/MarginContainer/VBoxContainer/StatusLabel");

		_controlDeviceLabel = GetNodeOrNull<Label>("PanelContainer/MarginContainer/VBoxContainer/GridContainer/ControlCard/MarginContainer/VBoxContainer/DeviceLabel");
		_controlStatusLabel = GetNodeOrNull<Label>("PanelContainer/MarginContainer/VBoxContainer/GridContainer/ControlCard/MarginContainer/VBoxContainer/StatusLabel");

		_steeringDeviceLabel = GetNodeOrNull<Label>("PanelContainer/MarginContainer/VBoxContainer/GridContainer/SteeringCard/MarginContainer/VBoxContainer/DeviceLabel");
		_steeringStatusLabel = GetNodeOrNull<Label>("PanelContainer/MarginContainer/VBoxContainer/GridContainer/SteeringCard/MarginContainer/VBoxContainer/StatusLabel");

		_heartRateDeviceLabel = GetNodeOrNull<Label>("PanelContainer/MarginContainer/VBoxContainer/GridContainer/HeartRateCard/MarginContainer/VBoxContainer/DeviceLabel");
		_heartRateStatusLabel = GetNodeOrNull<Label>("PanelContainer/MarginContainer/VBoxContainer/GridContainer/HeartRateCard/MarginContainer/VBoxContainer/StatusLabel");

		_continueButton = GetNodeOrNull<Button>("PanelContainer/MarginContainer/VBoxContainer/HBoxContainer/ContinueButton");

		Hide();
		ResetUi();
	}

	public void ShowTrainerConnectionScreen()
	{
		ResetUi();
		Show();
	}

	private void ResetUi()
	{
		SetOverallStatus("Pair your trainer to continue.");

		SetCard(_powerDeviceLabel, _powerStatusLabel, "Not paired", "Waiting");
		SetCard(_cadenceDeviceLabel, _cadenceStatusLabel, "Not paired", "Waiting");
		SetCard(_controlDeviceLabel, _controlStatusLabel, "Not paired", "Waiting");

		SetCard(_steeringDeviceLabel, _steeringStatusLabel, "Coming soon", "Unavailable");
		SetCard(_heartRateDeviceLabel, _heartRateStatusLabel, "Coming soon", "Unavailable");

		if (_continueButton is not null)
		{
			_continueButton.Disabled = true;
		}
	}

	private static void SetCard(Label? deviceLabel, Label? statusLabel, string deviceText, string statusText)
	{
		if (deviceLabel is not null)
			deviceLabel.Text = deviceText;

		if (statusLabel is not null)
			statusLabel.Text = statusText;
	}

	private void SetOverallStatus(string text)
	{
		if (_overallStatusLabel is not null)
			_overallStatusLabel.Text = text;
	}

	private async void OnPairTrainerPressed()
	{
		try
		{
			SetOverallStatus("Connecting to trainer...");

			GameBootstrap.ConfigureTrainerMode();

			if (GameBootstrap.TrainerClient is null)
			{
				SetOverallStatus("Trainer client not configured.");
				return;
			}

			if (!GameBootstrap.TrainerClient.IsConnected)
			{
				await GameBootstrap.TrainerClient.ConnectAsync();
			}

			SetCard(_powerDeviceLabel, _powerStatusLabel, "Smart Trainer", "Connected");
			SetCard(_cadenceDeviceLabel, _cadenceStatusLabel, "Smart Trainer", "Connected");
			SetCard(_controlDeviceLabel, _controlStatusLabel, "Smart Trainer", "Connected");

			SetOverallStatus("Trainer paired successfully.");

			if (_continueButton is not null)
			{
				_continueButton.Disabled = false;
			}
		}
		catch (System.Exception ex)
		{
			SetOverallStatus($"Trainer connection failed: {ex.Message}");
		}
	}

	private async void OnContinuePressed()
	{
		if (_rideSessionNode is null)
		{
			SetOverallStatus("RideSessionNode not found.");
			return;
		}

		Hide();
		_hudController?.Show();

		await _rideSessionNode.StartTrainerSessionAsync();
	}

	private void OnBackPressed()
	{
		_hudController?.Hide();
		Hide();
		_startPanel?.Show();
	}
}
