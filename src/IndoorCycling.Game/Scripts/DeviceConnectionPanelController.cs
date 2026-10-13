using System;
using IndoorCycling.Trainer.Abstractions;
using IndoorCycling.Trainer.Implementations;
//using IndoorCycling.Core.Models;
//using IndoorCycling.Core.Simulation;
using System.Threading.Tasks;
using System.Threading;
using IndoorCycling.Trainer.Windows;
using IndoorCycling.Trainer.Models;
using Godot;

public partial class DeviceConnectionPanelController : Control
{
	private WindowsBleTrainerClient? _realTrainerClient;
	private RideSessionNode? _rideSessionNode;
	private Control? _hudController;
	private Control? _startPanel;

	private RouteSelectionPanelController? _routeSelectionPanel;

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
	private Button[] _pairButtons = [];
	private readonly TrainerConnectionState _connectionState = new();
	private CancellationTokenSource? _connectionCancellation;

	public override void _Ready()
	{
		_rideSessionNode = GetNodeOrNull<RideSessionNode>("../RideSessionNode");
		_hudController = GetNodeOrNull<Control>("../HudController");
		_startPanel = GetNodeOrNull<Control>("../StartPanel");
		_routeSelectionPanel = GetNodeOrNull<RouteSelectionPanelController>("../RouteSelectionPanel");

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

		_pairButtons = new[]
		{
			GetNode<Button>("PanelContainer/MarginContainer/VBoxContainer/GridContainer/PowerCard/MarginContainer/VBoxContainer/ActionButton"),
			GetNode<Button>("PanelContainer/MarginContainer/VBoxContainer/GridContainer/CadenceCard/MarginContainer/VBoxContainer/ActionButton"),
			GetNode<Button>("PanelContainer/MarginContainer/VBoxContainer/GridContainer/ControlCard/MarginContainer/VBoxContainer/ActionButton")
		};
		foreach (var button in _pairButtons) button.Pressed += OnPairTrainerPressed;
		GetNode<Button>("PanelContainer/MarginContainer/VBoxContainer/HBoxContainer/BackButton").Pressed += OnBackPressed;
		_continueButton!.Pressed += OnContinuePressed;

		Hide();
		ResetUi();
	}

	public void ShowTrainerConnectionScreen()
	{
		if (_connectionState.Phase == TrainerConnectionPhase.Ready && _realTrainerClient?.IsConnected == true)
			ShowReadyUi();
		else if (_connectionState.Phase == TrainerConnectionPhase.NoTrainer)
		{
			UpdateCardStatuses("No trainer found");
			SetOverallStatus("No trainer found. Check that your trainer is awake, then Retry.");
			if (_continueButton is not null) _continueButton.Disabled = true;
			SetAttemptControlsBusy(false, "Retry");
		}
		else if (_connectionState.Phase is TrainerConnectionPhase.Failed or TrainerConnectionPhase.Disconnected)
		{
			UpdateCardStatuses(_connectionState.Phase == TrainerConnectionPhase.Disconnected ? "Disconnected" : "Not ready");
			SetOverallStatus($"{_connectionState.Error ?? "Trainer is not ready."} Retry when ready.");
			if (_continueButton is not null) _continueButton.Disabled = true;
			SetAttemptControlsBusy(false, "Retry");
		}
		else
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
		foreach (var button in _pairButtons) { button.Disabled = false; button.Text = "Pair"; }
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
		var attemptId = _connectionState.BeginAttempt();
		_connectionCancellation?.Cancel();
		_connectionCancellation?.Dispose();
		_connectionCancellation = new CancellationTokenSource();
		var cancellationToken = _connectionCancellation.Token;
		var client = new WindowsBleTrainerClient();
		_realTrainerClient = client;
		client.DebugEvent += evt => GameLogger.Log("trainer_event", evt);
		client.Disconnected += () => CallDeferred(nameof(HandleTrainerDisconnected), attemptId);
		SetAttemptControlsBusy(true);
		var stage = "scan";
		try
		{
			SetOverallStatus("Scanning for trainer...");
			GameLogger.Log("trainer_client_created", new { attemptId });
			if (_connectionState.SetPhase(attemptId, TrainerConnectionPhase.Scanning)) UpdateCardStatuses("Scanning…");
			var discovered = await client.ScanForDevicesAsync(TimeSpan.FromSeconds(6), cancellationToken);
			if (!_connectionState.SetPhase(attemptId, TrainerConnectionPhase.Connecting)) return;
			if (discovered.Count == 0)
			{
				_connectionState.SetPhase(attemptId, TrainerConnectionPhase.NoTrainer, "No trainer found.");
				UpdateCardStatuses("No trainer found");
				SetOverallStatus("No trainer found. Check that your trainer is awake, then Retry.");
				SetAttemptControlsBusy(false, "Retry");
				return;
			}

			SetOverallStatus($"Connecting to {discovered[0].Name}...");
			UpdateCardStatuses("Connecting…");
			stage = "connect_and_gatt_discovery";
			await client.ConnectToLastSeenDeviceAsync(cancellationToken);
			if (!_connectionState.SetPhase(attemptId, TrainerConnectionPhase.SettingUp)) return;
			if (!client.IsConnected) throw new InvalidOperationException("The trainer disconnected before setup completed.");

			SetOverallStatus("Setting up trainer control and telemetry...");
			UpdateCardStatuses("Setting up…");
			stage = "control_subscription";
			await client.SubscribeControlPointAsync(cancellationToken);
			stage = "request_control_acknowledgement";
			await client.RequestControlAsync(cancellationToken);
			stage = "telemetry_subscription";
			await client.SubscribeFtmsTelemetryAsync(cancellationToken);
			if (!client.IsConnected) throw new InvalidOperationException("The trainer disconnected during setup.");
			if (!_connectionState.SetPhase(attemptId, TrainerConnectionPhase.Ready)) return;

			GameBootstrap.ConfigureRealTrainerMode(client);
			if (GameBootstrap.DebugModeEnabled)
				GameLogger.Log("trainer_ready", new { mode = "real", connected = client.IsConnected, attemptId });
			ShowReadyUi();
		}
		catch (System.Exception ex)
		{
			if (attemptId != _connectionState.AttemptId)
			{
				GameLogger.Log("trainer_connection_stage_failed", new { attemptId, stage, reason = ex.Message, exceptionType = ex.GetType().FullName, staleAttempt = true });
				try { await client.DisconnectAsync(); } catch (Exception cleanupError) { GameLogger.Log("trainer_stale_attempt_cleanup_failed", new { message = cleanupError.Message }); }
				return;
			}
			GameLogger.Log("trainer_connection_stage_failed", new { attemptId, stage, reason = ex.Message, exceptionType = ex.GetType().FullName, staleAttempt = false });
			_connectionState.SetPhase(attemptId, TrainerConnectionPhase.Failed, ex.Message);
			try { await client.DisconnectAsync(); } catch (Exception cleanupError) { GameLogger.Log("trainer_disconnect_cleanup_failed", new { message = cleanupError.Message }); }
			if (ReferenceEquals(_realTrainerClient, client)) _realTrainerClient = null;
			if (ReferenceEquals(GameBootstrap.TrainerClient, client)) GameBootstrap.ResetSession();
			GameLogger.Log("trainer_connection_failed", new
			{
				message = ex.Message,
				exceptionType = ex.GetType().FullName,
				stackTrace = ex.StackTrace
			});

			UpdateCardStatuses("Not ready");
			SetOverallStatus($"Trainer connection failed: {ex.Message} Retry when ready.");
			SetAttemptControlsBusy(false, "Retry");
		}
	}

	private void ShowReadyUi()
	{
		SetCard(_powerDeviceLabel, _powerStatusLabel, "Smart Trainer", "Connected · telemetry ready");
		SetCard(_cadenceDeviceLabel, _cadenceStatusLabel, "Smart Trainer", "Connected · telemetry ready");
		SetCard(_controlDeviceLabel, _controlStatusLabel, "Smart Trainer", "Connected · control ready");
		SetOverallStatus("Trainer connected and ready. Stationary power and cadence may be zero.");
		if (_continueButton is not null) _continueButton.Disabled = !(_realTrainerClient?.IsConnected == true && _connectionState.Phase == TrainerConnectionPhase.Ready);
		SetAttemptControlsBusy(true, "Connected");
	}

	private void UpdateCardStatuses(string status)
	{
		SetCard(_powerDeviceLabel, _powerStatusLabel, "Smart Trainer", status);
		SetCard(_cadenceDeviceLabel, _cadenceStatusLabel, "Smart Trainer", status);
		SetCard(_controlDeviceLabel, _controlStatusLabel, "Smart Trainer", status);
	}

	private void SetAttemptControlsBusy(bool busy, string? text = null)
	{
		foreach (var button in _pairButtons)
		{
			button.Disabled = busy;
			if (text is not null) button.Text = text;
		}
	}

	private void HandleTrainerDisconnected(long attemptId)
	{
		if (!_connectionState.SetPhase(attemptId, TrainerConnectionPhase.Disconnected, "Trainer disconnected.")) return;
		if (_continueButton is not null) _continueButton.Disabled = true;
		UpdateCardStatuses("Disconnected");
		SetOverallStatus("Trainer disconnected. Reconnect and Retry.");
		SetAttemptControlsBusy(false, "Retry");
		(_hudController as HudController)?.SetStatus("Trainer disconnected. The ride is continuing without trainer telemetry.");
		GameLogger.Log("trainer_connection_lost", new { attemptId });
	}

	private void OnContinuePressed()
	{
		if (!_connectionState.CanContinue || _realTrainerClient?.IsConnected != true)
		{
			_connectionState.SetPhase(_connectionState.AttemptId, TrainerConnectionPhase.Disconnected, "Trainer disconnected.");
			if (_continueButton is not null) _continueButton.Disabled = true;
			UpdateCardStatuses("Disconnected");
			SetOverallStatus("Trainer is not connected. Reconnect and Retry.");
			SetAttemptControlsBusy(false, "Retry");
			return;
		}

		if (_routeSelectionPanel is null)
		{
			SetOverallStatus("Route selection panel not found.");
			return;
		}

		Hide();
		_routeSelectionPanel.ShowRouteSelection();
	}

	private void OnBackPressed()
	{
		_connectionState.Invalidate();
		_connectionCancellation?.Cancel();
		_hudController?.Hide();
		Hide();
		_startPanel?.Show();
	}
}
