using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
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
	private AntFeCTrainerClient? _antTrainerClient;
	private RideSessionNode? _rideSessionNode;
	private Control? _hudController;
	private Control? _startPanel;
	private RouteSelectionPanelController? _routeSelectionPanel;
	private Label? _overallStatusLabel;
	private Label? _trainerNameLabel;
	private Label? _protocolStatusLabel;
	private Label? _capabilitiesLabel;
	private Label? _powerStatusLabel;
	private Label? _cadenceStatusLabel;
	private Label? _diagnosticsLabel;
	private VBoxContainer? _connectionDetails;
	private VBoxContainer? _antSetupSection;
	private Label? _antSetupStatus;
	private LineEdit? _antKeyInput;
	private Button? _antSaveButton;
	private Button? _antClearButton;
	private PanelContainer? _connectionPanel;
	private OptionButton? _trainerPicker;
	private OptionButton? _protocolPreferenceOption;
	private Button? _connectButton;
	private Button? _cancelButton;
	private Button? _disconnectButton;
	private Button? _changeTrainerButton;
	private Button? _continueButton;
	private Button? _diagnosticsButton;
	private BleDiscoveredDevice[] _discovered = [];
	private AntFeCDeviceIdentity[] _antDiscovered = [];
	private readonly Dictionary<AntFeCDeviceIdentity, (ushort? ManufacturerId, ushort? ProductId)> _antDeviceInfo = [];
	private BleDiscoveredDevice? _selectedTrainer;
	private CancellationTokenSource? _attemptCancellation;
	private Task? _attemptTask;
	private CancellationTokenSource? _recoveryCancellation;
	private Task? _recoveryTask;
	private Action? _disconnectedHandler;
	private Action? _antDisconnectedHandler;
	private bool _attemptInProgress;
	private bool _attemptUsingAnt;
	private bool _intentionalDisconnect;
	private bool _awaitingDeviceChoice;
	private bool _awaitingAntDeviceChoice;
	private double _telemetryRefreshSeconds;

	public override void _Ready()
	{
		_rideSessionNode = GetNodeOrNull<RideSessionNode>("../RideSessionNode");
		_hudController = GetNodeOrNull<Control>("../HudController");
		_startPanel = GetNodeOrNull<Control>("../StartPanel");
		_routeSelectionPanel = GetNodeOrNull<RouteSelectionPanelController>("../RouteSelectionPanel");
		_connectionPanel = GetNodeOrNull<PanelContainer>("PanelContainer");
		_overallStatusLabel = GetNode<Label>("PanelContainer/MarginContainer/VBoxContainer/OverallStatusLabel");
		_trainerNameLabel = GetNode<Label>("PanelContainer/MarginContainer/VBoxContainer/TrainerNameLabel");
		_protocolStatusLabel = GetNode<Label>("PanelContainer/MarginContainer/VBoxContainer/ProtocolStatusLabel");
		_capabilitiesLabel = GetNode<Label>("PanelContainer/MarginContainer/VBoxContainer/CapabilitiesLabel");
		_powerStatusLabel = GetNode<Label>("PanelContainer/MarginContainer/VBoxContainer/ReadingsRow/PowerCard/MarginContainer/PowerVBox/PowerStatusLabel");
		_cadenceStatusLabel = GetNode<Label>("PanelContainer/MarginContainer/VBoxContainer/ReadingsRow/CadenceCard/MarginContainer/CadenceVBox/CadenceStatusLabel");
		_trainerPicker = GetNode<OptionButton>("PanelContainer/MarginContainer/VBoxContainer/TrainerPicker");
		_protocolPreferenceOption = GetNode<OptionButton>("PanelContainer/MarginContainer/VBoxContainer/ProtocolPreferenceOption");
		_protocolPreferenceOption.AddItem("Auto · Bluetooth FTMS first", (int)TrainerProtocolPreference.Auto);
		_protocolPreferenceOption.AddItem("Bluetooth FTMS", (int)TrainerProtocolPreference.BluetoothFtms);
		_protocolPreferenceOption.AddItem("ANT+ FE-C", (int)TrainerProtocolPreference.AntFeC);
		_protocolPreferenceOption.Select((int)GameBootstrap.Settings.Trainer.ProtocolPreference);
		_protocolPreferenceOption.ItemSelected += OnProtocolPreferenceSelected;
		_diagnosticsLabel = GetNode<Label>("PanelContainer/MarginContainer/VBoxContainer/DiagnosticsLabel");
		_connectButton = GetNode<Button>("PanelContainer/MarginContainer/VBoxContainer/ActionsRow/ConnectButton");
		_cancelButton = GetNode<Button>("PanelContainer/MarginContainer/VBoxContainer/ActionsRow/CancelButton");
		_disconnectButton = GetNode<Button>("PanelContainer/MarginContainer/VBoxContainer/ActionsRow/DisconnectButton");
		_changeTrainerButton = GetNode<Button>("PanelContainer/MarginContainer/VBoxContainer/ActionsRow/ChangeTrainerButton");
		GetNode<Button>("PanelContainer/MarginContainer/VBoxContainer/ActionsRow/BackButton").Pressed += OnBackPressed;
		_continueButton = GetNode<Button>("PanelContainer/MarginContainer/VBoxContainer/ActionsRow/ContinueButton");
		_diagnosticsButton = GetNode<Button>("PanelContainer/MarginContainer/VBoxContainer/DiagnosticsButton");
		MakeConnectionDetailsScrollable();
		BuildAntSetupSection();
		Resized += FitConnectionPanel;
		FitConnectionPanel();
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

	private void OnProtocolPreferenceSelected(long selectedIndex)
	{
		if (_protocolPreferenceOption is null || _attemptInProgress || _connectionState.Phase == TrainerConnectionPhase.Ready) return;
		var selected = (TrainerProtocolPreference)_protocolPreferenceOption.GetItemId((int)selectedIndex);
		if (!Enum.IsDefined(selected)) return;
		_awaitingDeviceChoice = false;
		_awaitingAntDeviceChoice = false;
		_discovered = [];
		_antDiscovered = [];
		_trainerPicker!.Clear();
		_trainerPicker.Hide();
		GameBootstrap.Settings.Trainer.ProtocolPreference = selected;
		try { GameBootstrap.Settings.Save(); }
		catch (Exception ex) { GameLogger.Log("trainer_preference_save_failed", new { message = ex.Message }); }
		_protocolStatusLabel!.Text = $"{ProtocolPreferenceLabel(selected)} · Not connected";
		if (selected == TrainerProtocolPreference.AntFeC)
		{
			var configured = AntNetworkKeyMaterial.GetStatus() == "Configured";
			_overallStatusLabel!.Text = configured
				? "ANT+ FE-C selected. Connect the ANTUSB-m and wake your trainer before scanning."
				: "ANT+ needs its 64-bit network key. Enter it in ANT+ Setup below.";
			_trainerNameLabel!.Text = "ANT+ trainer not connected";
			_connectButton!.Text = "Connect trainer";
			_connectionState.SetPhase(_connectionState.AttemptId, TrainerConnectionPhase.Idle);
			_connectButton.Disabled = false;
		}
		else
		{
			_connectionState.SetPhase(_connectionState.AttemptId, TrainerConnectionPhase.Idle);
			_overallStatusLabel!.Text = selected == TrainerProtocolPreference.Auto
				? "Auto prefers Bluetooth FTMS, then uses ANT+ FE-C when Bluetooth is unavailable."
				: "Bluetooth FTMS selected. Connect when your trainer is ready.";
			_trainerNameLabel!.Text = "No trainer connected";
			_connectButton!.Text = "Connect trainer";
			_connectButton.Disabled = false;
		}
		RefreshAntSetupUi();
	}

	private static string ProtocolPreferenceLabel(TrainerProtocolPreference preference) => preference switch
	{
		TrainerProtocolPreference.Auto => "Auto · Bluetooth FTMS first",
		TrainerProtocolPreference.BluetoothFtms => "Bluetooth FTMS",
		TrainerProtocolPreference.AntFeC => "ANT+ FE-C",
		_ => "Unknown protocol"
	};

	private void MakeConnectionDetailsScrollable()
	{
		var column = GetNodeOrNull<VBoxContainer>("PanelContainer/MarginContainer/VBoxContainer");
		if (column is null || column.GetChildCount() < 3) return;
		var detailNodes = column.GetChildren().Take(column.GetChildCount() - 2).ToArray();
		var scroll = new ScrollContainer { Name = "ConnectionDetailsScroll", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
		var details = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
		_connectionDetails = details;
		foreach (var child in detailNodes) { column.RemoveChild(child); details.AddChild(child); }
		column.AddChild(scroll);
		column.MoveChild(scroll, 0);
		scroll.AddChild(details);
	}

	private void BuildAntSetupSection()
	{
		if (_connectionDetails is null) return;
		_antSetupSection = new VBoxContainer
		{
			Name = "AntSetupSection",
			Visible = false,
			SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
		};
		_antSetupSection.AddChild(new HSeparator());
		_antSetupSection.AddChild(new Label { Text = "ANT+ Setup", HorizontalAlignment = HorizontalAlignment.Center });
		_antSetupSection.AddChild(new Label
		{
			Text = "Paste the ANT+ 64-bit entry from Garmin’s network key page. Wodra stores it for this Windows account.",
			AutowrapMode = TextServer.AutowrapMode.WordSmart,
			SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
		});
		_antSetupStatus = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
		_antSetupSection.AddChild(_antSetupStatus);
		_antKeyInput = new LineEdit
		{
			PlaceholderText = "16 hexadecimal digits or eight byte values",
			ClearButtonEnabled = true,
			SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
		};
		_antSetupSection.AddChild(_antKeyInput);
		var actions = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
		_antSaveButton = new Button { Text = "Save key" };
		_antClearButton = new Button { Text = "Clear key" };
		var open = new Button { Text = "Open Garmin key page" };
		_antSaveButton.Pressed += SaveAntSetup;
		_antClearButton.Pressed += ClearAntSetup;
		open.Pressed += () => OS.ShellOpen(AntNetworkKeyMaterial.GarminNetworkKeysUrl);
		actions.AddChild(_antSaveButton);
		actions.AddChild(_antClearButton);
		actions.AddChild(open);
		_antSetupSection.AddChild(actions);
		_connectionDetails.AddChild(_antSetupSection);
		RefreshAntSetupUi();
	}

	private bool IsAntConfigurationInUse() =>
		(_attemptInProgress && _attemptUsingAnt) || _antTrainerClient?.IsConnected == true;

	private void RefreshAntSetupUi()
	{
		if (_antSetupSection is null) return;
		var status = AntNetworkKeyMaterial.GetStatus();
		_antSetupSection.Visible = GameBootstrap.Settings.Trainer.ProtocolPreference == TrainerProtocolPreference.AntFeC || status != "Configured";
		if (_antSetupStatus is not null)
			_antSetupStatus.Text = IsAntConfigurationInUse()
				? "Disconnect ANT+ before changing or clearing this configuration."
				: status;
		var disabled = IsAntConfigurationInUse();
		if (_antSaveButton is not null) _antSaveButton.Disabled = disabled;
		if (_antClearButton is not null) _antClearButton.Disabled = disabled;
	}

	private void SaveAntSetup()
	{
		if (IsAntConfigurationInUse())
		{
			if (_antSetupStatus is not null) _antSetupStatus.Text = "Disconnect ANT+ before changing its setup.";
			return;
		}
		try
		{
			AntNetworkKeyMaterial.Save(_antKeyInput?.Text ?? string.Empty);
			_antKeyInput?.Clear();
			RefreshAntSetupUi();
			if (_antSetupStatus is not null) _antSetupStatus.Text = "ANT+ key saved for this Windows account.";
		}
		catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or System.Security.SecurityException)
		{
			if (_antSetupStatus is not null) _antSetupStatus.Text = ex.Message;
		}
	}

	private void ClearAntSetup()
	{
		if (IsAntConfigurationInUse())
		{
			if (_antSetupStatus is not null) _antSetupStatus.Text = "Disconnect ANT+ before clearing its setup.";
			return;
		}
		try
		{
			AntNetworkKeyMaterial.Clear();
			_antKeyInput?.Clear();
			RefreshAntSetupUi();
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Security.SecurityException)
		{
			if (_antSetupStatus is not null) _antSetupStatus.Text = ex.Message;
		}
	}

	private void FitConnectionPanel()
	{
		if (_connectionPanel is null || !IsInsideTree()) return;
		var viewport = GetViewportRect().Size;
		var width = Math.Min(1120, Math.Max(320, viewport.X - 32));
		var height = Math.Min(650, Math.Max(280, viewport.Y - 32));
		_connectionPanel.CustomMinimumSize = new Vector2(width, height);
		_connectionPanel.AnchorLeft = .5f; _connectionPanel.AnchorRight = .5f;
		_connectionPanel.AnchorTop = .5f; _connectionPanel.AnchorBottom = .5f;
		_connectionPanel.OffsetLeft = -width / 2; _connectionPanel.OffsetRight = width / 2;
		_connectionPanel.OffsetTop = -height / 2; _connectionPanel.OffsetBottom = height / 2;
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
		if (_connectionState.Phase == TrainerConnectionPhase.Ready && (_realTrainerClient?.IsReady == true || _antTrainerClient?.IsReady == true))
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
		if (_awaitingDeviceChoice || _awaitingAntDeviceChoice) return;
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
		_overallStatusLabel!.Text = GameBootstrap.Settings.Trainer.ProtocolPreference == TrainerProtocolPreference.AntFeC
			? "Connect your ANT+ FE-C trainer. Wake it and keep it nearby."
			: "Connect your Bluetooth smart trainer. Auto uses Bluetooth FTMS first.";
		_trainerNameLabel!.Text = "No trainer connected";
		_protocolStatusLabel!.Text = $"{ProtocolPreferenceLabel(GameBootstrap.Settings.Trainer.ProtocolPreference)} · Not connected";
		_capabilitiesLabel!.Text = "Trainer capabilities · not connected";
		_powerStatusLabel!.Text = "Waiting for connection";
		_cadenceStatusLabel!.Text = "Waiting for connection";
		_trainerPicker!.Clear();
		_trainerPicker.Hide();
		_connectButton!.Text = "Connect trainer";
		_connectButton.Disabled = false;
		_protocolPreferenceOption!.Disabled = false;
		_cancelButton!.Hide();
		_disconnectButton!.Disabled = true;
		_changeTrainerButton!.Disabled = false;
		_continueButton!.Disabled = true;
		RefreshAntSetupUi();
	}

	private async Task StartDiscoveryAsync(AntFeCDeviceIdentity? selectedAntDevice = null)
	{
		if (_attemptInProgress) return;
		_attemptInProgress = true;
		_attemptUsingAnt = false;
		_awaitingDeviceChoice = false;
		_awaitingAntDeviceChoice = false;
		_discovered = [];
		_antDiscovered = [];
		_trainerPicker!.Clear();
		_trainerPicker.Hide();
		_attemptCancellation?.Cancel();
		_attemptCancellation?.Dispose();
		var operationCancellation = new CancellationTokenSource();
		_attemptCancellation = operationCancellation;
		var cancellationToken = operationCancellation.Token;
		var attemptId = _connectionState.BeginAttempt();
		_protocolPreferenceOption!.Disabled = true;
		_cancelButton!.Show();
		_connectButton!.Text = "Scanning…";
		_connectButton.Disabled = true;
		_changeTrainerButton!.Disabled = true;
		_continueButton!.Disabled = true;
		var protocolSelection = TrainerProtocolPreferencePolicy.Select(
			GameBootstrap.Settings.Trainer.ProtocolPreference,
			bluetoothAvailable: true,
			antFeCAvailable: true);
		SetPhase(attemptId, TrainerConnectionPhase.Scanning);
		try
		{
			if (!protocolSelection.IsAvailable)
			{
				var reason = GameBootstrap.Settings.Trainer.ProtocolPreference == TrainerProtocolPreference.AntFeC
					? protocolSelection.UnavailableReason ?? "ANT+ FE-C is unavailable."
					: protocolSelection.UnavailableReason ?? "No supported trainer protocol is available.";
				_connectionState.SetPhase(attemptId, TrainerConnectionPhase.NoTrainer, reason);
				_overallStatusLabel!.Text = reason;
				_trainerNameLabel!.Text = "No trainer connected";
				_protocolStatusLabel!.Text = $"{ProtocolPreferenceLabel(GameBootstrap.Settings.Trainer.ProtocolPreference)} · Unavailable";
				_powerStatusLabel!.Text = "No trainer connected";
				_cadenceStatusLabel!.Text = "No trainer connected";
				_connectButton!.Text = "Choose another protocol";
				_connectButton.Disabled = true;
				return;
			}
			if (protocolSelection.Protocol == TrainerProtocol.AntFeC)
			{
				await ConnectAntFeCAsync(attemptId, cancellationToken, selectedAntDevice);
				return;
			}
			_overallStatusLabel!.Text = "Looking for nearby Bluetooth smart trainers…";
			_trainerNameLabel!.Text = "Searching nearby";
			_protocolStatusLabel!.Text = "Bluetooth FTMS · Scanning";
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
				if (GameBootstrap.Settings.Trainer.ProtocolPreference == TrainerProtocolPreference.Auto)
				{
					await ReleaseClientAsync(client);
					if (ReferenceEquals(_realTrainerClient, client)) _realTrainerClient = null;
					var fallback = TrainerProtocolPreferencePolicy.Select(
						TrainerProtocolPreference.Auto, bluetoothAvailable: false, antFeCAvailable: true);
					if (fallback.Protocol == TrainerProtocol.AntFeC)
					{
						await ConnectAntFeCAsync(attemptId, cancellationToken);
					return;
					}
				}
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
				_overallStatusLabel!.Text = "Scanning cancelled. Connect when your trainer is ready.";
				_trainerNameLabel!.Text = "No trainer connected";
				_connectButton!.Text = "Scan again";
			}
		}
		catch (Exception ex)
		{
			if (_connectionState.IsCurrent(attemptId))
			{
				var antNeedsSetup = _attemptUsingAnt && AntNetworkKeyMaterial.GetStatus() != "Configured";
				_connectionState.SetPhase(attemptId, TrainerConnectionPhase.Failed, ex.Message);
				_overallStatusLabel!.Text = antNeedsSetup
					? "ANT+ needs its 64-bit network key. Enter it in ANT+ Setup below, then retry."
					: $"Trainer scan failed: {ex.Message} You can retry.";
				_trainerNameLabel!.Text = "Connection unavailable";
				_protocolStatusLabel!.Text = _attemptUsingAnt
					? "ANT+ FE-C · Connection failed" : "Bluetooth FTMS · Scan failed";
				_connectButton!.Text = "Retry";
				RefreshAntSetupUi();
			}
			GameLogger.Log("trainer_scan_failed", new { attemptId, ex.Message });
		}
		finally
		{
			if (ReferenceEquals(_attemptCancellation, operationCancellation))
			{
				_attemptInProgress = false;
				_protocolPreferenceOption.Disabled = _connectionState.Phase == TrainerConnectionPhase.Ready;
				_cancelButton.Hide();
				_changeTrainerButton.Disabled = false;
				_connectButton.Disabled = _connectionState.Phase == TrainerConnectionPhase.Ready;
				_attemptCancellation = null;
				operationCancellation.Dispose();
				RefreshAntSetupUi();
			}
		}
	}

	private void OnConnectPressed()
	{
		if (_attemptInProgress) return;
		if (GameBootstrap.Settings.Trainer.ProtocolPreference == TrainerProtocolPreference.AntFeC &&
			AntNetworkKeyMaterial.GetStatus() != "Configured")
		{
			_overallStatusLabel!.Text = "Enter the ANT+ 64-bit network key in ANT+ Setup below, then connect.";
			_antKeyInput?.CallDeferred(Control.MethodName.GrabFocus);
			return;
		}
		if (_awaitingAntDeviceChoice && _antDiscovered.Length > 1)
		{
			var index = (int)_trainerPicker!.Selected;
			if (index >= 0 && index < _antDiscovered.Length)
			{
				_trainerPicker.Hide();
				_attemptTask = StartDiscoveryAsync(_antDiscovered[index]);
			}
			return;
		}
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

	private async Task ConnectAntFeCAsync(long attemptId, CancellationToken cancellationToken, AntFeCDeviceIdentity? selectedDevice = null, bool forceFreshDiscovery = false)
	{
		_attemptUsingAnt = true;
		_selectedTrainer = null;
		if (_antTrainerClient is not null)
		{
			if (_antDisconnectedHandler is not null) _antTrainerClient.Disconnected -= _antDisconnectedHandler;
			await _antTrainerClient.DisconnectAsync();
		}
		var rememberedNumber = GameBootstrap.Settings.Trainer.LastAntDeviceNumber;
		if (selectedDevice is null && (!rememberedNumber.HasValue || forceFreshDiscovery))
		{
			_antDeviceInfo.Clear();
			_overallStatusLabel!.Text = "Scanning nearby ANT+ FE-C trainers…";
			_trainerNameLabel!.Text = "Searching nearby";
			_protocolStatusLabel!.Text = "ANT+ FE-C · Scanning";
			var trainerDataReceivedWithoutIdentity = false;
			var savedDeviceIdentity = GameBootstrap.Settings.Trainer is { LastAntDeviceNumber: { } savedNumber,
				LastAntDeviceType: { } savedType, LastAntTransmissionType: { } savedTransmission }
				? new AntFeCDeviceIdentity(savedNumber, savedType, savedTransmission) : (AntFeCDeviceIdentity?)null;
			var discoveredDevices = await AntFeCTrainerClient.DiscoverAsync(
				cancellationToken, diagnostic =>
				{
					if (diagnostic.Operation == "device_info_received" && diagnostic.DeviceNumber is { } number &&
						diagnostic.DeviceType is { } type && diagnostic.TransmissionType is { } transmission)
					{
						var identity = new AntFeCDeviceIdentity(number, type, transmission);
						var previous = _antDeviceInfo.GetValueOrDefault(identity);
						_antDeviceInfo[identity] = (
							diagnostic.ManufacturerId ?? previous.ManufacturerId,
							diagnostic.ProductId ?? previous.ProductId);
					}
					if (diagnostic.Operation == "scan_window_completed" &&
						diagnostic.Outcome == "trainer_data_received_identification_failed")
						trainerDataReceivedWithoutIdentity = true;
					GameLogger.Log("ant_diagnostic", diagnostic);
				}, elapsed => Callable.From(() =>
				{
					if (!_connectionState.IsCurrent(attemptId) || cancellationToken.IsCancellationRequested) return;
					var elapsedText = elapsed.ToString(@"m\:ss");
					_overallStatusLabel!.Text = $"Searching for ANT+ FE-C trainers… {elapsedText} elapsed. Keep the trainer awake nearby.";
					_trainerNameLabel!.Text = $"Searching · {elapsedText}";
				}).CallDeferred());
			var discovery = AntFeCDiscoveryPolicy.Select(discoveredDevices, savedDeviceIdentity);
			if (!_connectionState.IsCurrent(attemptId)) return;
			if (discovery.Outcome == AntFeCDiscoveryOutcome.None)
			{
				var failureMessage = trainerDataReceivedWithoutIdentity
					? "ANT+ FE-C trainer data arrived, but its identity could not be resolved. Retry the scan."
					: "No compatible ANT+ FE-C trainer was found.";
				_connectionState.SetPhase(attemptId, TrainerConnectionPhase.NoTrainer, failureMessage);
				_overallStatusLabel.Text = trainerDataReceivedWithoutIdentity
					? "ANT+ FE-C data received; trainer identification failed. Retry the scan."
					: "No ANT+ FE-C trainer found. Wake the trainer, keep it nearby, then retry.";
				_trainerNameLabel.Text = trainerDataReceivedWithoutIdentity ? "Trainer identity unavailable" : "No trainer found";
				_protocolStatusLabel.Text = trainerDataReceivedWithoutIdentity
					? "ANT+ FE-C · Identification failed" : "ANT+ FE-C · No device detected";
				_connectButton!.Text = "Retry scan";
				_connectButton.Disabled = false;
				return;
			}
			if (discovery.Outcome == AntFeCDiscoveryOutcome.Choose)
			{
				_antDiscovered = discovery.Candidates.ToArray();
				_trainerPicker!.Clear();
				foreach (var device in _antDiscovered) _trainerPicker.AddItem(AntDeviceDisplayName(device));
				_trainerPicker.Select(0);
				_trainerPicker.Show();
				_awaitingAntDeviceChoice = true;
				_connectionState.SetPhase(attemptId, TrainerConnectionPhase.Idle);
				_overallStatusLabel.Text = "Several ANT+ FE-C trainers were found. Choose one, then connect.";
				_trainerNameLabel.Text = "Choose your trainer";
				_protocolStatusLabel.Text = "ANT+ FE-C · Multiple devices found";
				_connectButton!.Text = "Connect selected trainer";
				_connectButton.Disabled = false;
				return;
			}
			selectedDevice = discovery.Selected;
		}
		var client = new AntFeCTrainerClient(GameBootstrap.Settings.Trainer, selectedDevice,
			diagnostic =>
			{
				GameLogger.Log("ant_diagnostic", diagnostic);
				if (diagnostic.Operation == "target_search_progress")
				{
					var elapsed = TimeSpan.FromMilliseconds(diagnostic.ElapsedMilliseconds).ToString(@"m\:ss");
					Callable.From(() =>
					{
						if (!_connectionState.IsCurrent(attemptId) || !_attemptInProgress && _recoveryTask is null) return;
						_overallStatusLabel!.Text = $"Waiting for the selected ANT+ trainer… {elapsed} elapsed. Keep it awake nearby.";
						_trainerNameLabel!.Text = $"Connecting · {elapsed}";
					}).CallDeferred();
				}
			});
		_antTrainerClient = client;
		if (_antDisconnectedHandler is not null) client.Disconnected -= _antDisconnectedHandler;
		_antDisconnectedHandler = () => Callable.From(() => HandleAntTrainerDisconnected(attemptId)).CallDeferred();
		client.Disconnected += _antDisconnectedHandler;
		_overallStatusLabel!.Text = "Searching for an ANT+ FE-C trainer… Wake the trainer and keep it nearby.";
		_trainerNameLabel!.Text = "Searching nearby";
		_protocolStatusLabel!.Text = "ANT+ FE-C · Scanning";
		_connectButton!.Text = "Searching…";
		SetPhase(attemptId, TrainerConnectionPhase.Connecting);
		try
		{
			await client.ConnectAsync(cancellationToken);
		}
		catch (TimeoutException) when (selectedDevice is null && !forceFreshDiscovery &&
			AntFeCInitialConnectionPolicy.ShouldFallbackToDiscovery(rememberedNumber.HasValue, isRecovery: false,
				cancelled: cancellationToken.IsCancellationRequested))
		{
			GameLogger.Log("ant_initial_target_fallback", new
			{
				attemptId,
				savedTarget = new { deviceNumber = rememberedNumber, deviceType = GameBootstrap.Settings.Trainer.LastAntDeviceType,
					transmissionType = GameBootstrap.Settings.Trainer.LastAntTransmissionType },
				outcome = "remembered_target_timed_out_starting_fresh_discovery"
			});
			if (_antDisconnectedHandler is not null) client.Disconnected -= _antDisconnectedHandler;
			await client.DisconnectAsync(CancellationToken.None);
			if (ReferenceEquals(_antTrainerClient, client)) _antTrainerClient = null;
			await ConnectAntFeCAsync(attemptId, cancellationToken, selectedDevice: null, forceFreshDiscovery: true);
			return;
		}
		if (!_connectionState.IsCurrent(attemptId))
		{
			await client.DisconnectAsync();
			return;
		}
		var configuredTarget = client.ConfiguredDeviceIdentity;
		if (AntFeCInitialConnectionPolicy.CanPersistIdentity(client.VerifiedDeviceIdentity, configuredTarget))
		{
			var verified = client.VerifiedDeviceIdentity!.Value;
			GameBootstrap.Settings.Trainer.LastAntDeviceNumber = verified.DeviceNumber;
			GameBootstrap.Settings.Trainer.LastAntDeviceType = verified.DeviceType;
			GameBootstrap.Settings.Trainer.LastAntTransmissionType = verified.TransmissionType;
			try { GameBootstrap.Settings.Save(); }
			catch (Exception ex) { GameLogger.Log("trainer_preference_save_failed", new { message = ex.Message }); }
			GameLogger.Log("ant_trainer_identity_persisted", new { source = selectedDevice is null ? "resolved_channel_or_extended_source" : "verified_discovery_candidate",
				deviceNumber = verified.DeviceNumber, deviceType = verified.DeviceType, transmissionType = verified.TransmissionType });
		}
		else
		{
			GameLogger.Log("ant_trainer_identity_not_persisted", new
			{
				source = client.VerifiedDeviceIdentity is null ? "no_verified_source_identity" : "verified_identity_did_not_match_configured_target",
				configuredTarget = configuredTarget is { } target ? new { target.DeviceNumber, target.DeviceType, target.TransmissionType } : null
			});
		}
		GameBootstrap.ConfigureRealTrainerMode(client);
		_connectionState.SetPhase(attemptId, TrainerConnectionPhase.Ready);
		ShowReadyUi();
		GameLogger.Log("trainer_ready", new
		{
			mode = "real", protocol = client.ActiveProtocol,
			deviceNumber = client.ConnectedDeviceNumber,
			deviceType = client.ConnectedDeviceType,
			attemptId
		});
	}

	private async Task ConnectSelectedAsync(BleDiscoveredDevice device, long attemptId, CancellationToken cancellationToken)
	{
		_selectedTrainer = device;
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
			_selectedTrainer = device with { Name = string.IsNullOrWhiteSpace(device.Name) ? client.ConnectedDeviceName ?? device.Name : device.Name };
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
				RefreshAntSetupUi();
			}
		}
	}

	private async Task ConfigureClientAsync(WindowsBleTrainerClient client, ulong address, long attemptId, CancellationToken cancellationToken, bool markReady = true)
	{
		client.SetOperationStage("ble_device_creation_and_ftms_gatt_discovery");
		await client.ConnectToDeviceAsync(address, cancellationToken);
		if (!client.IsConnected) throw new InvalidOperationException("The trainer did not establish a Bluetooth connection.");
		_connectionState.SetPhase(attemptId, TrainerConnectionPhase.SettingUp);
		_overallStatusLabel!.Text = "Bluetooth is connected. Setting up trainer control and live measurements…";
		_protocolStatusLabel!.Text = "Bluetooth FTMS · Configuring";
		client.SetOperationStage("ftms_control_point_subscription");
		await client.SubscribeControlPointAsync(cancellationToken);
		client.SetOperationStage("ftms_control_request_acknowledgement");
		await client.RequestControlAsync(cancellationToken);
		client.SetOperationStage("ftms_telemetry_subscription");
		await client.SubscribeFtmsTelemetryAsync(cancellationToken);
		if (!client.IsConnected) throw new InvalidOperationException("The trainer disconnected during setup.");
		client.SetOperationStage("trainer_ready");
		if (markReady) client.MarkReady();
	}

	private void ShowReadyUi()
	{
		IndoorCycling.Trainer.Abstractions.ITrainerClient? client = _antTrainerClient is not null
			? _antTrainerClient : _realTrainerClient;
		if (client?.IsReady != true || _connectionState.Phase != TrainerConnectionPhase.Ready) return;
		_trainerNameLabel!.Text = client is AntFeCTrainerClient ant
			? ant.ConnectedDeviceName ?? "ANT+ fitness equipment"
			: (_realTrainerClient?.ConnectedDeviceName ?? "Bluetooth trainer");
		_protocolStatusLabel!.Text = _antTrainerClient is not null
			? "ANT+ FE-C · Telemetry connected"
			: "Bluetooth FTMS · Connected and ready";
		_capabilitiesLabel!.Text = client is AntFeCTrainerClient antTrainer
			? $"FE-C modes: {antTrainer.CapabilitiesDescription}\n{antTrainer.ControlEvidenceDescription}"
			: "Trainer capabilities: FTMS control";
		_overallStatusLabel!.Text = _antTrainerClient is not null
			? "Trainer telemetry is connected. Capability metadata may be unavailable; control is checked during the ride. Resistance change must be verified on the trainer."
			: "Trainer ready. Pedal to check live power and cadence; stationary zero readings are normal.";
		_connectButton!.Text = "Connected";
		_connectButton.Disabled = true;
		_disconnectButton!.Disabled = false;
		_changeTrainerButton!.Disabled = false;
		if (_antTrainerClient is not null) _changeTrainerButton.Text = "Find another trainer";
		_protocolPreferenceOption!.Disabled = true;
		_continueButton!.Disabled = _antTrainerClient is { CanStartControlledRide: false };
		if (_antTrainerClient is { CanStartControlledRide: false })
			_overallStatusLabel!.Text = "Telemetry works, but this trainer reports that simulation control is unsupported. Wodra cannot start a controlled ride.";
		RefreshTelemetryUi();
	}

	private void RefreshTelemetryUi()
	{
		IndoorCycling.Trainer.Abstractions.ITrainerClient? client = _antTrainerClient is not null
			? _antTrainerClient : _realTrainerClient;
		if (client is not { IsReady: true })
		{
			if (_connectionState.Phase == TrainerConnectionPhase.Ready)
			{
				if (_antTrainerClient is not null) HandleAntTrainerDisconnected(_connectionState.AttemptId);
				else HandleTrainerDisconnected(_connectionState.AttemptId);
			}
			return;
		}
		var telemetry = client.GetLatestTelemetry();
		var protocolName = client is AntFeCTrainerClient ? "ANT+ FE-C" : "Bluetooth FTMS";
		var now = DateTimeOffset.UtcNow;
		var power = TrainerTelemetryPresentation.Power(telemetry, now);
		var cadence = TrainerTelemetryPresentation.Cadence(telemetry, now);
		var stationary = telemetry.HasSpeedMeasurement && telemetry.SpeedKph < 0.3 && cadence.State == TelemetryReadingState.Fresh && cadence.Value == 0;
		_powerStatusLabel!.Text = FormatReading(power, "W", stationary);
		_cadenceStatusLabel!.Text = FormatReading(cadence, "rpm", stationary);
		if (client is AntFeCTrainerClient ant)
		{
			_capabilitiesLabel!.Text = $"FE-C modes: {ant.CapabilitiesDescription}\n{ant.ControlEvidenceDescription}";
			_continueButton!.Disabled = !ant.CanStartControlledRide;
			if (_connectionState.Phase == TrainerConnectionPhase.Ready && !ant.CanStartControlledRide)
				_overallStatusLabel!.Text = "Telemetry works, but this trainer reports that simulation control is unsupported. Wodra cannot start a controlled ride.";
			else if (_connectionState.Phase == TrainerConnectionPhase.Ready)
				_overallStatusLabel!.Text = ant.CapabilityFlags is null
					? "Telemetry connected; FE-C capability response was not received. Wodra will use a compatibility control attempt during the ride. Verify resistance on the trainer."
					: "Trainer telemetry is connected. Control status is reported separately; verify resistance on the trainer.";
		}
		if (_diagnosticsLabel?.Visible == true)
			_diagnosticsLabel.Text = $"Device: {(client is AntFeCTrainerClient antForDiagnostics ? antForDiagnostics.ConnectedDeviceName : _realTrainerClient?.ConnectedDeviceName) ?? "unknown"}\nProtocol: {protocolName}\nCapabilities: {(client is AntFeCTrainerClient antFeC ? antFeC.CapabilitiesDescription : "FTMS control")}\nLink: {(client.IsConnected ? "connected" : "disconnected")}\nLast telemetry: {(telemetry.HasReceivedMeasurement ? telemetry.Timestamp.ToLocalTime().ToString("T") : "not received")}\nControl ready: {_connectionState.Phase == TrainerConnectionPhase.Ready}";
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
		if (_realTrainerClient is { } lostClient && _selectedTrainer is null && lostClient.ConnectedBluetoothAddress is { } address)
			_selectedTrainer = new BleDiscoveredDevice(address, lostClient.ConnectedDeviceName ?? "Trainer", [], 0);
		_continueButton!.Disabled = true;
		_connectButton!.Text = "Retry";
		_connectButton.Disabled = true;
		_disconnectButton!.Disabled = true;
		_protocolStatusLabel!.Text = "Bluetooth FTMS · Connection lost";
		_powerStatusLabel!.Text = "Connection lost · reconnecting";
		_cadenceStatusLabel!.Text = "Connection lost · reconnecting";
		_overallStatusLabel!.Text = "Trainer connection lost. Trying to reconnect; you can retry manually if needed.";
		(_hudController as HudController)?.SetStatus("Trainer connection lost. Rider input is paused while reconnecting.");
		(_hudController as HudController)?.SetTrainerRecoveryAction("Trainer connection lost. Searching for your trainer…", null);
		if (_recoveryTask is null)
		{
			_recoveryCancellation?.Dispose();
			_recoveryCancellation = new CancellationTokenSource();
			_recoveryTask = RecoverTrainerAsync(attemptId, _recoveryCancellation.Token, manual: false);
		}
		GameLogger.Log("trainer_connection_lost", new { attemptId, selectedAddress = _selectedTrainer?.BluetoothAddress.ToString("X12"), selectedName = _selectedTrainer?.Name, recoveryStartedAt = DateTimeOffset.UtcNow });
	}

	private void HandleAntTrainerDisconnected(long attemptId)
	{
		if (_intentionalDisconnect || _antTrainerClient is null ||
			!_connectionState.SetPhase(attemptId, TrainerConnectionPhase.Disconnected, "ANT+ FE-C trainer connection was lost.")) return;
		_continueButton!.Disabled = true;
		_connectButton!.Text = "Retry";
		_connectButton.Disabled = true;
		_disconnectButton!.Disabled = true;
		_protocolStatusLabel!.Text = "ANT+ FE-C · Connection lost";
		_capabilitiesLabel!.Text = "Trainer capabilities · connection lost";
		_powerStatusLabel!.Text = "Connection lost · reconnecting";
		_cadenceStatusLabel!.Text = "Connection lost · reconnecting";
		_overallStatusLabel!.Text = "ANT+ trainer connection lost. Trying the remembered trainer again.";
		(_hudController as HudController)?.SetStatus("ANT+ trainer connection lost. Rider input is paused while reconnecting.");
		if (_recoveryTask is null)
		{
			_recoveryCancellation?.Dispose();
			_recoveryCancellation = new CancellationTokenSource();
			_recoveryTask = RecoverAntTrainerAsync(attemptId, _recoveryCancellation.Token);
		}
		GameLogger.Log("trainer_connection_lost", new { attemptId, protocol = "ANT+ FE-C", recoveryStartedAt = DateTimeOffset.UtcNow });
	}

	private async Task RecoverAntTrainerAsync(long attemptId, CancellationToken cancellationToken)
	{
		var client = _antTrainerClient;
		if (client is null) { CompleteRecovery(); return; }
		var result = await TrainerRecoveryLoop.RunAsync(async (attempt, token) =>
		{
			if (_intentionalDisconnect || !_connectionState.IsCurrent(attemptId)) return false;
			_overallStatusLabel!.Text = $"Searching for the remembered ANT+ trainer ({attempt}/{MaximumAutomaticReconnects})…";
			try
			{
				await client.DisconnectAsync(token);
				await client.ConnectAsync(token);
				if (!_connectionState.IsCurrent(attemptId) || _intentionalDisconnect) return false;
				if (_rideSessionNode is not null) await _rideSessionNode.ReapplyCurrentTrainerSettingsAsync();
				_connectionState.SetPhase(attemptId, TrainerConnectionPhase.Ready);
				ShowReadyUi();
				(_hudController as HudController)?.SetStatus("ANT+ trainer reconnected. Current ride settings were restored.");
				GameLogger.Log("trainer_recovery_restored", new { attemptId, attempt, protocol = "ANT+ FE-C" });
				return true;
			}
			catch (OperationCanceledException) { throw; }
			catch (Exception ex)
			{
				GameLogger.Log("trainer_recovery_attempt_failed", new { attemptId, attempt, protocol = "ANT+ FE-C", message = ex.Message });
				try { await client.DisconnectAsync(CancellationToken.None); } catch { }
				return false;
			}
		}, TrainerRecoveryPolicy.MaximumAutomaticAttempts, TrainerRecoveryPolicy.AutomaticRecoveryWindow,
			TrainerRecoveryPolicy.DelayBeforeAttempt, cancellationToken,
			shouldContinue: () => !_intentionalDisconnect && _connectionState.IsCurrent(attemptId));

		if (result.Outcome == TrainerRecoveryOutcome.Expired && _connectionState.IsCurrent(attemptId))
		{
			_connectionState.SetPhase(attemptId, TrainerConnectionPhase.Failed, "Automatic ANT+ recovery expired.");
			_overallStatusLabel!.Text = "ANT+ trainer was not found. Wake it and retry the connection.";
			_protocolStatusLabel!.Text = "ANT+ FE-C · Retry available";
			_connectButton!.Text = "Retry";
			_connectButton.Disabled = false;
			(_hudController as HudController)?.SetTrainerRecoveryAction("ANT+ trainer was not found. Retry when it is awake.",
				() => { CompleteRecovery(); _ = StartDiscoveryAsync(); });
		}
		CompleteRecovery();
	}

	private async Task RecoverTrainerAsync(long attemptId, CancellationToken cancellationToken, bool manual)
	{
		var client = _realTrainerClient;
		var recoveryStartedAt = DateTimeOffset.UtcNow;
		if (client is null || _selectedTrainer is null)
		{
				CompleteRecovery();
				return;
		}

		var scheduledDelay = TimeSpan.Zero;
		var result = await TrainerRecoveryLoop.RunAsync(
			async (attempt, token) =>
			{
				var attemptStartedAt = DateTimeOffset.UtcNow;
				var elapsedMs = (attemptStartedAt - recoveryStartedAt).TotalMilliseconds;
				var target = _selectedTrainer;
				if (target is null || _intentionalDisconnect || !_connectionState.IsCurrent(attemptId)) return false;
				var identity = new { address = target.BluetoothAddress.ToString("X12"), name = target.Name };
				var stage = "dispose_stale_ble_gatt_resources";
				_overallStatusLabel!.Text = $"Looking for {target.Name} again ({attempt}/{MaximumAutomaticReconnects})…";
				_protocolStatusLabel!.Text = $"Bluetooth FTMS · Rediscovering ({attempt}/{MaximumAutomaticReconnects})";
				GameLogger.Log("trainer_recovery_attempt_started", new { attemptId, attempt, manual, attemptStartedAt, elapsedMs, selectedDevice = identity });
				try
				{
					// Do not reuse the disconnected BluetoothLEDevice or any GATT service/characteristic handles.
					client.SetOperationStage(stage);
					await client.DisconnectAsync(token);
					stage = "trainer_advertisement_discovery";
					client.SetOperationStage(stage);
					var discovered = await client.ScanForDevicesAsync(TrainerRecoveryPolicy.DiscoveryDuration, token);
					var returning = TrainerRecoveryPolicy.SelectReturningTrainer(discovered,
						target.BluetoothAddress, target.Name);
					GameLogger.Log("trainer_recovery_discovery", new
					{
						attemptId, attempt, selectedDevice = identity,
						compatibleCount = discovered.Count,
						candidates = discovered.Select(device => new { address = device.BluetoothAddress.ToString("X12"), device.Name, device.Rssi }).ToArray(),
						selected = returning is null ? null : new { address = returning.BluetoothAddress.ToString("X12"), returning.Name, returning.Rssi },
						discoveryCompletedAt = DateTimeOffset.UtcNow
					});
					if (returning is null)
					{
						_overallStatusLabel.Text = $"Waiting for {target.Name} to power on and advertise…";
						GameLogger.Log("trainer_recovery_attempt_failed", new
						{
							attemptId, attempt, manual, failedAt = DateTimeOffset.UtcNow,
							elapsedMs = (DateTimeOffset.UtcNow - recoveryStartedAt).TotalMilliseconds,
							stage = stage, reason = "selected_trainer_not_advertising", selectedDevice = identity
						});
						return false;
					}

					stage = "fresh_ble_device_and_ftms_gatt_setup";
					await ConfigureClientAsync(client, returning.BluetoothAddress, attemptId, token, markReady: false);
					if (!_connectionState.IsCurrent(attemptId) || _intentionalDisconnect) return false;
					stage = "reapply_current_ride_settings";
					if (_rideSessionNode is not null) await _rideSessionNode.ReapplyCurrentTrainerSettingsAsync();
					if (!_connectionState.IsCurrent(attemptId) || _intentionalDisconnect) return false;

					client.MarkReady();
					_selectedTrainer = returning with { Name = string.IsNullOrWhiteSpace(returning.Name) ? client.ConnectedDeviceName ?? returning.Name : returning.Name };
					try
					{
						GameBootstrap.Settings.LastTrainerBluetoothAddress = returning.BluetoothAddress;
						GameBootstrap.Settings.Save();
					}
					catch (Exception settingsError) { GameLogger.Log("trainer_preference_save_failed", new { message = settingsError.Message }); }
					_connectionState.SetPhase(attemptId, TrainerConnectionPhase.Ready);
					ShowReadyUi();
					(_hudController as HudController)?.SetStatus("Trainer reconnected. Current ride settings were restored.");
					(_hudController as HudController)?.SetTrainerRecoveryAction(null, null);
					GameLogger.Log("trainer_recovery_restored", new
					{
						attemptId, attempt, manual, startedAt = attemptStartedAt,
						restoredAt = DateTimeOffset.UtcNow,
						elapsedMs = (DateTimeOffset.UtcNow - recoveryStartedAt).TotalMilliseconds,
						selectedDevice = identity, address = returning.BluetoothAddress.ToString("X12"), name = _selectedTrainer.Name
					});
					return true;
				}
				catch (OperationCanceledException) { throw; }
				catch (Exception ex)
				{
					GameLogger.Log("trainer_recovery_attempt_failed", new
					{
						attemptId, attempt, manual, failedAt = DateTimeOffset.UtcNow,
						elapsedMs = (DateTimeOffset.UtcNow - recoveryStartedAt).TotalMilliseconds,
						stage = client.LastConnectionFailureStage ?? client.CurrentOperationStage ?? stage,
						selectedDevice = identity, message = ex.Message, exceptionType = ex.GetType().FullName
					});
					try { await client.DisconnectAsync(CancellationToken.None); }
					catch (Exception cleanupError) { GameLogger.Log("trainer_recovery_cleanup_failed", new { attemptId, attempt, message = cleanupError.Message }); }
					return false;
				}
			},
			TrainerRecoveryPolicy.MaximumAutomaticAttempts,
			TrainerRecoveryPolicy.AutomaticRecoveryWindow,
			attempt =>
			{
				scheduledDelay = TrainerRecoveryPolicy.DelayBeforeAttempt(attempt);
				GameLogger.Log("trainer_recovery_attempt_scheduled", new { attemptId, attempt, manual, scheduledAt = DateTimeOffset.UtcNow, delayMs = scheduledDelay.TotalMilliseconds, selectedAddress = _selectedTrainer?.BluetoothAddress.ToString("X12"), selectedName = _selectedTrainer?.Name });
				return scheduledDelay;
			},
			cancellationToken,
			shouldContinue: () => !_intentionalDisconnect && _connectionState.IsCurrent(attemptId));

		if (result.Outcome == TrainerRecoveryOutcome.Expired && _connectionState.IsCurrent(attemptId) && !_intentionalDisconnect)
		{
			_connectionState.SetPhase(attemptId, TrainerConnectionPhase.Failed, "Automatic reconnect attempts were exhausted.");
			_overallStatusLabel!.Text = "Could not reconnect yet. Check trainer power, then Retry to keep searching while your ride continues.";
			_protocolStatusLabel!.Text = "Bluetooth FTMS · Not connected";
			_connectButton!.Text = "Retry";
			_connectButton.Disabled = false;
			_changeTrainerButton!.Disabled = false;
			(_hudController as HudController)?.SetStatus("Trainer disconnected. Ride and recording continue; trainer input is paused.");
			(_hudController as HudController)?.SetTrainerRecoveryAction(
				"Trainer not found yet. Check its power and retry without ending the ride.",
				StartManualRecoveryFromRide);
		}
		else if (result.Outcome == TrainerRecoveryOutcome.Cancelled && !_intentionalDisconnect && _connectionState.IsCurrent(attemptId))
			GameLogger.Log("trainer_recovery_cancelled", new { attemptId, manual, attempts = result.Attempts, completedAt = DateTimeOffset.UtcNow });
		CompleteRecovery();
		GameLogger.Log("trainer_recovery_window_finished", new { attemptId, manual, outcome = result.Outcome.ToString(), attempts = result.Attempts, lastFailure = result.LastFailure, startedAt = recoveryStartedAt, completedAt = DateTimeOffset.UtcNow });
	}

	private void StartManualRecoveryFromRide()
	{
		if (_intentionalDisconnect || _recoveryTask is not null || _realTrainerClient is null || _selectedTrainer is null) return;
		var attemptId = _connectionState.AttemptId;
		_connectionState.SetPhase(attemptId, TrainerConnectionPhase.Disconnected, "Rider requested trainer recovery.");
		_recoveryCancellation?.Dispose();
		_recoveryCancellation = new CancellationTokenSource();
		(_hudController as HudController)?.SetTrainerRecoveryAction("Retrying trainer discovery…", null);
		_recoveryTask = RecoverTrainerAsync(attemptId, _recoveryCancellation.Token, manual: true);
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
		(_hudController as HudController)?.SetTrainerRecoveryAction(null, null);
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
		if (GameBootstrap.Settings.Trainer.ProtocolPreference == TrainerProtocolPreference.AntFeC)
		{
			GameBootstrap.Settings.Trainer.LastAntDeviceNumber = null;
			GameBootstrap.Settings.Trainer.LastAntDeviceType = null;
			GameBootstrap.Settings.Trainer.LastAntTransmissionType = null;
			try { GameBootstrap.Settings.Save(); }
			catch (Exception ex) { GameLogger.Log("trainer_preference_save_failed", new { message = ex.Message }); }
			GameLogger.Log("ant_remembered_identity_cleared", new { reason = "rider_requested_find_another_trainer" });
		}
		if (_antTrainerClient is { } antClient)
		{
			if (_antDisconnectedHandler is not null) antClient.Disconnected -= _antDisconnectedHandler;
			await antClient.DisconnectAsync();
			if (ReferenceEquals(GameBootstrap.TrainerClient, antClient)) GameBootstrap.ResetSession();
			_antTrainerClient = null;
		}
		_intentionalDisconnect = false;
		ResetUi();
		_attemptTask = StartDiscoveryAsync();
		await _attemptTask;
	}

	private async Task DisconnectTrainerAsync()
	{
		(_hudController as HudController)?.SetTrainerRecoveryAction(null, null);
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
		if (_antTrainerClient is { } antClient)
		{
			if (_antDisconnectedHandler is not null) antClient.Disconnected -= _antDisconnectedHandler;
			await antClient.DisconnectAsync();
			if (ReferenceEquals(GameBootstrap.TrainerClient, antClient)) GameBootstrap.ResetSession();
		}
		_realTrainerClient = null;
		_antTrainerClient = null;
		_recoveryCancellation?.Dispose();
		_recoveryCancellation = null;
		_intentionalDisconnect = false;
		_attemptInProgress = false;
		_awaitingDeviceChoice = false;
		_awaitingAntDeviceChoice = false;
		_discovered = [];
		_antDiscovered = [];
		ResetUi();
		_connectionState.SetPhase(_connectionState.AttemptId, TrainerConnectionPhase.IntentionalDisconnect);
		_overallStatusLabel!.Text = "Trainer disconnected. Connect again whenever you are ready.";
		_protocolStatusLabel!.Text = $"{ProtocolPreferenceLabel(GameBootstrap.Settings.Trainer.ProtocolPreference)} · Disconnected by rider";
	}

	/// <summary>Called by ride shutdown so the panel owner can suppress recovery and release its client.</summary>
	public async Task DisconnectForMainMenuAsync(WindowsBleTrainerClient client)
	{
		(_hudController as HudController)?.SetTrainerRecoveryAction(null, null);
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

	public async Task DisconnectForMainMenuAsync(AntFeCTrainerClient client)
	{
		_intentionalDisconnect = true;
		_connectionState.Invalidate();
		_attemptCancellation?.Cancel();
		if (_attemptTask is { } attempt) { try { await attempt; } catch { } }
		_recoveryCancellation?.Cancel();
		if (_recoveryTask is { } recovery) { try { await recovery; } catch { } }
		await client.DisconnectAsync();
		if (_antDisconnectedHandler is not null) client.Disconnected -= _antDisconnectedHandler;
		if (ReferenceEquals(_antTrainerClient, client)) _antTrainerClient = null;
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
		if (!_connectionState.CanContinue || _connectionState.Phase != TrainerConnectionPhase.Ready || (_antTrainerClient?.IsReady != true && _realTrainerClient?.IsReady != true))
		{
			_continueButton!.Disabled = true;
			_overallStatusLabel!.Text = "Trainer is not ready. Select Retry to reconnect before continuing.";
			_connectButton!.Disabled = false;
			return;
		}
		if (_antTrainerClient is { CanStartControlledRide: false })
		{
			_continueButton!.Disabled = true;
			_overallStatusLabel!.Text = "This trainer reports that simulation control is unsupported, so a controlled ride cannot start.";
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

	private string AntDeviceDisplayName(AntFeCDeviceIdentity device)
	{
		if (!_antDeviceInfo.TryGetValue(device, out var info)) return device.DisplayName;
		var details = new System.Collections.Generic.List<string>();
		if (info.ManufacturerId is > 0 and < ushort.MaxValue) details.Add($"manufacturer {info.ManufacturerId}");
		if (info.ProductId is > 0 and < ushort.MaxValue) details.Add($"product {info.ProductId}");
		return details.Count == 0 ? device.DisplayName : $"{string.Join(", ", details)} · {device.DisplayName}";
	}
}
