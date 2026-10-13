using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using IndoorCycling.Trainer.Models;
using IndoorCycling.Trainer.Windows;

/// <summary>Owns the selected trainer for the full application session, including ride transitions.</summary>
public partial class DeviceConnectionPanelController : Control
{
	private const int MaximumAutomaticReconnects = TrainerRecoveryPolicy.MaximumAutomaticAttempts;
	private readonly TrainerConnectionState _connectionState = new();
	private WindowsBleTrainerClient? _realTrainerClient;
	private RideSessionNode? _rideSessionNode;
	private Control? _hudController;
	private Control? _startPanel;
	private RouteSelectionPanelController? _routeSelectionPanel;
	private Label? _overallStatusLabel;
	private Label? _trainerNameLabel;
	private Label? _protocolStatusLabel;
	private Label? _powerStatusLabel;
	private Label? _cadenceStatusLabel;
	private Label? _diagnosticsLabel;
	private OptionButton? _trainerPicker;
	private Button? _connectButton;
	private Button? _cancelButton;
	private Button? _disconnectButton;
	private Button? _changeTrainerButton;
	private Button? _continueButton;
	private Button? _diagnosticsButton;
	private BleDiscoveredDevice[] _discovered = [];
	private CancellationTokenSource? _attemptCancellation;
	private Task? _attemptTask;
	private CancellationTokenSource? _recoveryCancellation;
	private Task? _recoveryTask;
	private Action? _disconnectedHandler;
	private bool _attemptInProgress;
	private bool _intentionalDisconnect;
	private bool _awaitingDeviceChoice;
	private double _telemetryRefreshSeconds;

	public override void _Ready()
	{
		_rideSessionNode = GetNodeOrNull<RideSessionNode>("../RideSessionNode");
		_hudController = GetNodeOrNull<Control>("../HudController");
		_startPanel = GetNodeOrNull<Control>("../StartPanel");
		_routeSelectionPanel = GetNodeOrNull<RouteSelectionPanelController>("../RouteSelectionPanel");
		_overallStatusLabel = GetNode<Label>("PanelContainer/MarginContainer/VBoxContainer/OverallStatusLabel");
		_trainerNameLabel = GetNode<Label>("PanelContainer/MarginContainer/VBoxContainer/TrainerNameLabel");
		_protocolStatusLabel = GetNode<Label>("PanelContainer/MarginContainer/VBoxContainer/ProtocolStatusLabel");
		_powerStatusLabel = GetNode<Label>("PanelContainer/MarginContainer/VBoxContainer/ReadingsRow/PowerCard/MarginContainer/PowerVBox/PowerStatusLabel");
		_cadenceStatusLabel = GetNode<Label>("PanelContainer/MarginContainer/VBoxContainer/ReadingsRow/CadenceCard/MarginContainer/CadenceVBox/CadenceStatusLabel");
		_trainerPicker = GetNode<OptionButton>("PanelContainer/MarginContainer/VBoxContainer/TrainerPicker");
		_diagnosticsLabel = GetNode<Label>("PanelContainer/MarginContainer/VBoxContainer/DiagnosticsLabel");
		_connectButton = GetNode<Button>("PanelContainer/MarginContainer/VBoxContainer/ActionsRow/ConnectButton");
		_cancelButton = GetNode<Button>("PanelContainer/MarginContainer/VBoxContainer/ActionsRow/CancelButton");
		_disconnectButton = GetNode<Button>("PanelContainer/MarginContainer/VBoxContainer/ActionsRow/DisconnectButton");
		_changeTrainerButton = GetNode<Button>("PanelContainer/MarginContainer/VBoxContainer/ActionsRow/ChangeTrainerButton");
		GetNode<Button>("PanelContainer/MarginContainer/VBoxContainer/ActionsRow/BackButton").Pressed += OnBackPressed;
		_continueButton = GetNode<Button>("PanelContainer/MarginContainer/VBoxContainer/ActionsRow/ContinueButton");
		_diagnosticsButton = GetNode<Button>("PanelContainer/MarginContainer/VBoxContainer/DiagnosticsButton");
		_connectButton.Pressed += OnConnectPressed;
		_cancelButton.Pressed += OnCancelPressed;
		_disconnectButton.Pressed += () => _ = DisconnectTrainerAsync();
		_changeTrainerButton.Pressed += OnChangeTrainerPressed;
		_continueButton.Pressed += OnContinuePressed;
		_diagnosticsButton.Pressed += ToggleDiagnostics;
		_diagnosticsLabel.Hide();
		_trainerPicker.Hide();
		Hide();
		ResetUi();
	}

	public override void _Process(double delta)
	{
		_telemetryRefreshSeconds += delta;
		if (_telemetryRefreshSeconds < 0.15) return;
		_telemetryRefreshSeconds = 0;
		RefreshTelemetryUi();
	}

	/// <summary>Clicking Connect Trainer on the launch screen starts Bluetooth discovery immediately.</summary>
	public void ShowTrainerConnectionScreen()
	{
		Show();
		if (_connectionState.Phase == TrainerConnectionPhase.Ready && _realTrainerClient?.IsReady == true)
		{
			ShowReadyUi();
			return;
		}
		if (_recoveryTask is not null)
		{
			_overallStatusLabel!.Text = "Trainer connection was lost. Automatic recovery is in progress.";
			_connectButton!.Text = "Retry";
			_connectButton.Disabled = true;
			return;
		}
		if (_attemptInProgress) return;
		if (_awaitingDeviceChoice) return;
		if (_connectionState.Phase is TrainerConnectionPhase.NoTrainer or TrainerConnectionPhase.Failed)
		{
			_connectButton!.Disabled = false;
			_cancelButton!.Hide();
			return;
		}
		_attemptTask = StartDiscoveryAsync();
	}

	private void ResetUi()
	{
		_overallStatusLabel!.Text = "Connect your Bluetooth smart trainer. ANT+ support is not available yet.";
		_trainerNameLabel!.Text = "No trainer connected";
		_protocolStatusLabel!.Text = "Bluetooth FTMS · Not connected";
		_powerStatusLabel!.Text = "Waiting for connection";
		_cadenceStatusLabel!.Text = "Waiting for connection";
		_trainerPicker!.Clear();
		_trainerPicker.Hide();
		_connectButton!.Text = "Connect trainer";
		_connectButton.Disabled = false;
		_cancelButton!.Hide();
		_disconnectButton!.Disabled = true;
		_changeTrainerButton!.Disabled = false;
		_continueButton!.Disabled = true;
	}

	private async Task StartDiscoveryAsync()
	{
		if (_attemptInProgress) return;
		_attemptInProgress = true;
		_awaitingDeviceChoice = false;
		_discovered = [];
		_trainerPicker!.Clear();
		_trainerPicker.Hide();
		_attemptCancellation?.Cancel();
		_attemptCancellation?.Dispose();
		var operationCancellation = new CancellationTokenSource();
		_attemptCancellation = operationCancellation;
		var cancellationToken = operationCancellation.Token;
		var attemptId = _connectionState.BeginAttempt();
		_cancelButton!.Show();
		_connectButton!.Text = "Scanning…";
		_connectButton.Disabled = true;
		_changeTrainerButton!.Disabled = true;
		_continueButton!.Disabled = true;
		_overallStatusLabel!.Text = "Looking for nearby Bluetooth smart trainers…";
		_trainerNameLabel!.Text = "Searching nearby";
		_protocolStatusLabel!.Text = "Bluetooth FTMS · Scanning";
		SetPhase(attemptId, TrainerConnectionPhase.Scanning);
		try
		{
			if (_realTrainerClient is not null && !_realTrainerClient.IsConnected)
				await ReleaseClientAsync(_realTrainerClient);
			var client = new WindowsBleTrainerClient();
			_realTrainerClient = client;
			var found = await client.ScanForDevicesAsync(TimeSpan.FromSeconds(7), cancellationToken);
			if (!_connectionState.IsCurrent(attemptId)) return;
			var selection = TrainerDiscoveryPolicy.Select(found, GameBootstrap.Settings.LastTrainerBluetoothAddress);
			_discovered = selection.Candidates.ToArray();
			if (selection.Outcome == TrainerDiscoveryOutcome.None)
			{
				_connectionState.SetPhase(attemptId, TrainerConnectionPhase.NoTrainer, "No compatible trainer was found.");
				_overallStatusLabel!.Text = "No compatible trainer found. Wake your trainer, keep it nearby, then retry.";
				_trainerNameLabel!.Text = "No trainer found";
				_protocolStatusLabel!.Text = "Bluetooth FTMS · No device detected";
				_powerStatusLabel!.Text = "No trainer connected";
				_cadenceStatusLabel!.Text = "No trainer connected";
				_connectButton!.Text = "Retry scan";
				return;
			}
			if (selection.Outcome == TrainerDiscoveryOutcome.Choose)
			{
				_awaitingDeviceChoice = true;
				foreach (var device in _discovered)
					_trainerPicker.AddItem($"{DisplayName(device)}  ·  {device.Rssi} dBm");
				var preferredIndex = GameBootstrap.Settings.LastTrainerBluetoothAddress is { } preferred
					? Array.FindIndex(_discovered, device => device.BluetoothAddress == preferred) : -1;
				_trainerPicker.Select(Math.Max(0, preferredIndex));
				_trainerPicker.Show();
				_connectionState.SetPhase(attemptId, TrainerConnectionPhase.Idle);
				_overallStatusLabel.Text = "Several compatible trainers were found. Choose one, then connect.";
				_trainerNameLabel.Text = "Choose your trainer";
				_protocolStatusLabel.Text = "Bluetooth FTMS · Multiple devices found";
				_connectButton.Text = "Connect selected trainer";
				return;
			}
			await ConnectSelectedAsync(selection.Selected!, attemptId, cancellationToken);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			if (_connectionState.IsCurrent(attemptId))
			{
				_connectionState.SetPhase(attemptId, TrainerConnectionPhase.Idle);
				_overallStatusLabel.Text = "Scanning cancelled. Connect when your trainer is ready.";
				_trainerNameLabel.Text = "No trainer connected";
				_connectButton.Text = "Scan again";
			}
		}
		catch (Exception ex)
		{
			if (_connectionState.IsCurrent(attemptId))
			{
				_connectionState.SetPhase(attemptId, TrainerConnectionPhase.Failed, ex.Message);
				_overallStatusLabel.Text = $"Trainer scan failed: {ex.Message} You can retry.";
				_trainerNameLabel.Text = "Connection unavailable";
				_protocolStatusLabel.Text = "Bluetooth FTMS · Scan failed";
				_connectButton.Text = "Retry";
			}
			GameLogger.Log("trainer_scan_failed", new { attemptId, ex.Message });
		}
		finally
		{
			if (ReferenceEquals(_attemptCancellation, operationCancellation))
			{
				_attemptInProgress = false;
				_cancelButton.Hide();
				_changeTrainerButton.Disabled = false;
				_connectButton.Disabled = _connectionState.Phase == TrainerConnectionPhase.Ready;
				_attemptCancellation = null;
				operationCancellation.Dispose();
			}
		}
	}

	private void OnConnectPressed()
	{
		if (_attemptInProgress) return;
		if (_awaitingDeviceChoice && _discovered.Length > 1)
		{
			var index = (int)_trainerPicker!.Selected;
			if (index >= 0 && index < _discovered.Length)
			{
				_attemptInProgress = true;
				var attemptId = _connectionState.BeginAttempt();
				_attemptCancellation?.Dispose();
				_attemptCancellation = new CancellationTokenSource();
				_awaitingDeviceChoice = false;
				_connectButton!.Disabled = true;
				_changeTrainerButton!.Disabled = true;
				_cancelButton!.Show();
				_attemptTask = ConnectSelectedAsync(_discovered[index], attemptId, _attemptCancellation.Token);
			}
			return;
		}
		_attemptTask = StartDiscoveryAsync();
	}

	private async Task ConnectSelectedAsync(BleDiscoveredDevice device, long attemptId, CancellationToken cancellationToken)
	{
		var operationCancellation = _attemptCancellation;
		var client = _realTrainerClient ?? new WindowsBleTrainerClient();
		_realTrainerClient = client;
		if (_disconnectedHandler is not null) client.Disconnected -= _disconnectedHandler;
		_disconnectedHandler = () => Callable.From(() => HandleTrainerDisconnected(attemptId)).CallDeferred();
		client.Disconnected += _disconnectedHandler;
		client.DebugEvent += evt => GameLogger.Log("trainer_event", evt);
		_trainerPicker!.Hide();
		_overallStatusLabel!.Text = $"Connecting to {DisplayName(device)}…";
		_trainerNameLabel!.Text = DisplayName(device);
		_protocolStatusLabel!.Text = "Bluetooth FTMS · Connecting";
		_connectButton!.Text = "Connecting…";
		SetPhase(attemptId, TrainerConnectionPhase.Connecting);
		try
		{
			await ConfigureClientAsync(client, device.BluetoothAddress, attemptId, cancellationToken);
			if (!_connectionState.IsCurrent(attemptId)) { await ReleaseClientAsync(client); return; }
			client.MarkReady();
			try
			{
				GameBootstrap.Settings.LastTrainerBluetoothAddress = device.BluetoothAddress;
				GameBootstrap.Settings.Save();
			}
			catch (Exception settingsError) { GameLogger.Log("trainer_preference_save_failed", new { message = settingsError.Message }); }
			GameBootstrap.ConfigureRealTrainerMode(client);
			_connectionState.SetPhase(attemptId, TrainerConnectionPhase.Ready);
			ShowReadyUi();
			GameLogger.Log("trainer_ready", new { mode = "real", protocol = client.ActiveProtocol, name = client.ConnectedDeviceName, attemptId });
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			if (_connectionState.IsCurrent(attemptId))
			{
				_connectionState.SetPhase(attemptId, TrainerConnectionPhase.Idle);
				_overallStatusLabel!.Text = "Connection cancelled.";
				_connectButton!.Text = "Scan again";
			}
			await ReleaseClientAsync(client);
		}
		catch (Exception ex)
		{
			await ReleaseClientAsync(client);
			if (_connectionState.IsCurrent(attemptId))
			{
				_connectionState.SetPhase(attemptId, TrainerConnectionPhase.Failed, ex.Message);
				_overallStatusLabel!.Text = $"Could not connect to {DisplayName(device)}: {ex.Message} Retry or choose another trainer.";
				_protocolStatusLabel!.Text = "Bluetooth FTMS · Setup failed";
				_connectButton!.Text = "Retry";
				_continueButton!.Disabled = true;
			}
			GameLogger.Log("trainer_connection_failed", new { attemptId, message = ex.Message, exceptionType = ex.GetType().FullName });
		}
		finally
		{
			if (ReferenceEquals(_attemptCancellation, operationCancellation) && operationCancellation is not null)
			{
				_attemptInProgress = false;
				_cancelButton!.Hide();
				_changeTrainerButton!.Disabled = false;
				_connectButton!.Disabled = _connectionState.Phase == TrainerConnectionPhase.Ready;
				_attemptCancellation = null;
				operationCancellation.Dispose();
			}
		}
	}

	private async Task ConfigureClientAsync(WindowsBleTrainerClient client, ulong address, long attemptId, CancellationToken cancellationToken, bool markReady = true)
	{
		await client.ConnectToDeviceAsync(address, cancellationToken);
		if (!client.IsConnected) throw new InvalidOperationException("The trainer did not establish a Bluetooth connection.");
		_connectionState.SetPhase(attemptId, TrainerConnectionPhase.SettingUp);
		_overallStatusLabel!.Text = "Bluetooth is connected. Setting up trainer control and live measurements…";
		_protocolStatusLabel!.Text = "Bluetooth FTMS · Configuring";
		await client.SubscribeControlPointAsync(cancellationToken);
		await client.RequestControlAsync(cancellationToken);
		await client.SubscribeFtmsTelemetryAsync(cancellationToken);
		if (!client.IsConnected) throw new InvalidOperationException("The trainer disconnected during setup.");
		if (markReady) client.MarkReady();
	}

	private void ShowReadyUi()
	{
		var client = _realTrainerClient;
		if (client?.IsReady != true || _connectionState.Phase != TrainerConnectionPhase.Ready) return;
		_trainerNameLabel!.Text = client.ConnectedDeviceName ?? "Bluetooth trainer";
		_protocolStatusLabel!.Text = $"{client.ActiveProtocol} · Connected and ready";
		_overallStatusLabel!.Text = "Trainer ready. Pedal to check live power and cadence; stationary zero readings are normal.";
		_connectButton!.Text = "Connected";
		_connectButton.Disabled = true;
		_disconnectButton!.Disabled = false;
		_changeTrainerButton!.Disabled = false;
		_continueButton!.Disabled = false;
		RefreshTelemetryUi();
	}

	private void RefreshTelemetryUi()
	{
		if (_realTrainerClient is not { IsReady: true } client)
		{
			if (_connectionState.Phase == TrainerConnectionPhase.Ready)
				HandleTrainerDisconnected(_connectionState.AttemptId);
			return;
		}
		var telemetry = client.GetLatestTelemetry();
		var now = DateTimeOffset.UtcNow;
		var power = TrainerTelemetryPresentation.Power(telemetry, now);
		var cadence = TrainerTelemetryPresentation.Cadence(telemetry, now);
		var stationary = telemetry.HasSpeedMeasurement && telemetry.SpeedKph < 0.3 && cadence.State == TelemetryReadingState.Fresh && cadence.Value == 0;
		_powerStatusLabel!.Text = FormatReading(power, "W", stationary);
		_cadenceStatusLabel!.Text = FormatReading(cadence, "rpm", stationary);
		if (_diagnosticsLabel?.Visible == true)
			_diagnosticsLabel.Text = $"Device: {client.ConnectedDeviceName ?? "unknown"}\nProtocol: {client.ActiveProtocol}\nLink: {(client.IsConnected ? "connected" : "disconnected")}\nLast telemetry: {(telemetry.HasReceivedMeasurement ? telemetry.Timestamp.ToLocalTime().ToString("T") : "not received")}\nControl ready: {_connectionState.Phase == TrainerConnectionPhase.Ready}";
	}

	private static string FormatReading(TelemetryReading reading, string unit, bool stationary) => reading.State switch
	{
		TelemetryReadingState.Awaiting => "Waiting for the first reading",
		TelemetryReadingState.Unsupported => "Not provided by this trainer",
		TelemetryReadingState.Stale => "No recent data · pedal or reconnect",
		_ when reading.Value == 0 && stationary => $"0 {unit} · stationary",
		_ => $"{reading.Value} {unit}"
	};

	private void HandleTrainerDisconnected(long attemptId)
	{
		if (_intentionalDisconnect || !_connectionState.SetPhase(attemptId, TrainerConnectionPhase.Disconnected, "Trainer connection was lost.")) return;
		_continueButton!.Disabled = true;
		_connectButton!.Text = "Retry";
		_connectButton.Disabled = true;
		_disconnectButton!.Disabled = true;
		_protocolStatusLabel!.Text = "Bluetooth FTMS · Connection lost";
		_powerStatusLabel!.Text = "Connection lost · reconnecting";
		_cadenceStatusLabel!.Text = "Connection lost · reconnecting";
		_overallStatusLabel!.Text = "Trainer connection lost. Trying to reconnect; you can retry manually if needed.";
		(_hudController as HudController)?.SetStatus("Trainer connection lost. Rider input is paused while reconnecting.");
		if (_recoveryTask is null)
		{
			_recoveryCancellation?.Dispose();
			_recoveryCancellation = new CancellationTokenSource();
			_recoveryTask = RecoverTrainerAsync(attemptId, _recoveryCancellation.Token);
		}
		GameLogger.Log("trainer_connection_lost", new { attemptId });
	}

	private async Task RecoverTrainerAsync(long attemptId, CancellationToken cancellationToken)
	{
		var client = _realTrainerClient;
		var address = client?.ConnectedBluetoothAddress ?? GameBootstrap.Settings.LastTrainerBluetoothAddress;
		if (client is null || address is null) { CompleteRecovery(); return; }
		for (var attempt = 1; TrainerRecoveryPolicy.ShouldRetry(attempt - 1, _intentionalDisconnect) && _connectionState.IsCurrent(attemptId); attempt++)
		{
			_overallStatusLabel!.Text = $"Reconnecting to {client.ConnectedDeviceName ?? "trainer"} ({attempt}/{MaximumAutomaticReconnects})…";
			_protocolStatusLabel!.Text = $"Bluetooth FTMS · Reconnecting ({attempt}/{MaximumAutomaticReconnects})";
			try
			{
				await Task.Delay(TrainerRecoveryPolicy.DelayBeforeAttempt(attempt), cancellationToken);
				if (!_connectionState.IsCurrent(attemptId) || _intentionalDisconnect) { CompleteRecovery(); return; }
				await ConfigureClientAsync(client, address.Value, attemptId, cancellationToken, markReady: false);
				if (!_connectionState.IsCurrent(attemptId) || _intentionalDisconnect) { CompleteRecovery(); return; }
				if (_rideSessionNode is not null) await _rideSessionNode.ReapplyCurrentTrainerSettingsAsync();
				client.MarkReady();
				_connectionState.SetPhase(attemptId, TrainerConnectionPhase.Ready);
				ShowReadyUi();
				(_hudController as HudController)?.SetStatus("Trainer reconnected. Current ride settings were restored.");
				GameLogger.Log("trainer_reconnected", new { attemptId, retry = attempt });
				CompleteRecovery();
				return;
			}
			catch (OperationCanceledException) { CompleteRecovery(); return; }
			catch (Exception ex)
			{
				GameLogger.Log("trainer_reconnect_attempt_failed", new { attemptId, retry = attempt, message = ex.Message });
				try { await client.DisconnectAsync(); } catch { }
			}
		}
		if (_connectionState.IsCurrent(attemptId))
		{
			_connectionState.SetPhase(attemptId, TrainerConnectionPhase.Failed, "Automatic reconnect attempts were exhausted.");
			_overallStatusLabel!.Text = "Could not reconnect. Check trainer power and select Retry to scan again.";
			_protocolStatusLabel!.Text = "Bluetooth FTMS · Not connected";
			_connectButton!.Text = "Retry";
			_connectButton.Disabled = false;
			_changeTrainerButton!.Disabled = false;
		}
		CompleteRecovery();
	}

	private void CompleteRecovery()
	{
		_recoveryTask = null;
		_recoveryCancellation?.Dispose();
		_recoveryCancellation = null;
	}

	private void OnCancelPressed()
	{
		_attemptCancellation?.Cancel();
		if (!_attemptInProgress) return;
		_overallStatusLabel!.Text = "Cancelling trainer setup…";
	}

	private void OnChangeTrainerPressed()
	{
		_ = ChangeTrainerAsync();
	}

	private async Task ChangeTrainerAsync()
	{
		if (_attemptInProgress) { OnCancelPressed(); return; }
		_intentionalDisconnect = true;
		_connectionState.Invalidate();
		_attemptCancellation?.Cancel();
		if (_attemptTask is { } attempt) { try { await attempt; } catch { } }
		_recoveryCancellation?.Cancel();
		if (_recoveryTask is { } recovery) { try { await recovery; } catch { } }
		if (_realTrainerClient is not null)
		{
			await ReleaseClientAsync(_realTrainerClient);
			if (ReferenceEquals(GameBootstrap.TrainerClient, _realTrainerClient)) GameBootstrap.ResetSession();
			_realTrainerClient = null;
		}
		_intentionalDisconnect = false;
		ResetUi();
		_attemptTask = StartDiscoveryAsync();
		await _attemptTask;
	}

	private async Task DisconnectTrainerAsync()
	{
		if (_attemptInProgress) _attemptCancellation?.Cancel();
		_intentionalDisconnect = true;
		_connectionState.Invalidate();
		_attemptCancellation?.Cancel();
		if (_attemptTask is { } attempt) { try { await attempt; } catch { } }
		_recoveryCancellation?.Cancel();
		if (_recoveryTask is { } recovery) { try { await recovery; } catch { } }
		if (_realTrainerClient is { } client)
		{
			await ReleaseClientAsync(client);
			if (ReferenceEquals(GameBootstrap.TrainerClient, client)) GameBootstrap.ResetSession();
		}
		_realTrainerClient = null;
		_recoveryCancellation?.Dispose();
		_recoveryCancellation = null;
		_intentionalDisconnect = false;
		_attemptInProgress = false;
		_awaitingDeviceChoice = false;
		_discovered = [];
		ResetUi();
		_connectionState.SetPhase(_connectionState.AttemptId, TrainerConnectionPhase.IntentionalDisconnect);
		_overallStatusLabel!.Text = "Trainer disconnected. Connect again whenever you are ready.";
		_protocolStatusLabel!.Text = "Bluetooth FTMS · Disconnected by rider";
	}

	/// <summary>Called by ride shutdown so the panel owner can suppress recovery and release its client.</summary>
	public async Task DisconnectForMainMenuAsync(WindowsBleTrainerClient client)
	{
		_intentionalDisconnect = true;
		_connectionState.Invalidate();
		_attemptCancellation?.Cancel();
		if (_attemptTask is { } attempt) { try { await attempt; } catch { } }
		_recoveryCancellation?.Cancel();
		if (_recoveryTask is { } recovery) { try { await recovery; } catch { } }
		await ReleaseClientAsync(client);
		if (ReferenceEquals(_realTrainerClient, client)) _realTrainerClient = null;
		_attemptInProgress = false;
		_connectionState.SetPhase(_connectionState.AttemptId, TrainerConnectionPhase.IntentionalDisconnect);
		_intentionalDisconnect = false;
	}

	private async Task ReleaseClientAsync(WindowsBleTrainerClient client)
	{
		try { await client.DisconnectAsync(); }
		catch (Exception ex) { GameLogger.Log("trainer_client_cleanup_failed", new { message = ex.Message }); }
	}

	private void OnContinuePressed()
	{
		if (!_connectionState.CanContinue || _connectionState.Phase != TrainerConnectionPhase.Ready || _realTrainerClient?.IsReady != true)
		{
			_continueButton!.Disabled = true;
			_overallStatusLabel!.Text = "Trainer is not ready. Select Retry to reconnect before continuing.";
			_connectButton!.Disabled = false;
			return;
		}
		if (_routeSelectionPanel is null) { _overallStatusLabel!.Text = "Route selection is unavailable."; return; }
		Hide();
		_routeSelectionPanel.ShowRouteSelection();
	}

	private void OnBackPressed()
	{
		_attemptCancellation?.Cancel();
		if (_connectionState.Phase is TrainerConnectionPhase.Scanning or TrainerConnectionPhase.Connecting or TrainerConnectionPhase.SettingUp)
			_connectionState.Invalidate();
		_hudController?.Hide();
		Hide();
		_startPanel?.Show();
	}

	private void ToggleDiagnostics()
	{
		var show = !_diagnosticsLabel!.Visible;
		_diagnosticsLabel.Visible = show;
		_diagnosticsButton!.Text = show ? "Hide technical details" : "Technical details";
		RefreshTelemetryUi();
	}

	private bool SetPhase(long attemptId, TrainerConnectionPhase phase) => _connectionState.SetPhase(attemptId, phase);
	private static string DisplayName(BleDiscoveredDevice device) => string.IsNullOrWhiteSpace(device.Name) ? "Bluetooth trainer" : device.Name;
}
