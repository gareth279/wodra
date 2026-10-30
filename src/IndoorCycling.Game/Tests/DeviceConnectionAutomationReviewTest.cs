using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using IndoorCycling.Trainer.Models;
using IndoorCycling.Trainer.Windows;

/// <summary>Exercises automatic startup discovery through the production launch and connection screens with radio fakes.</summary>
public partial class DeviceConnectionAutomationReviewTest : Node
{
	public override async void _Ready()
	{
		try
		{
			var trainer = new FakeTrainer([FakeTrainer.Remembered, FakeTrainer.Alternative]);
			var heartRate = new FakeHeartRate([FakeHeartRate.Remembered, FakeHeartRate.Alternative]);
			var steering = new FakeSteering([FakeSteering.Remembered, FakeSteering.Alternative]);
			var screen = CreateProductionRideScreen(trainer, heartRate, steering);
			GameBootstrap.Settings.Trainer.ProtocolPreference = TrainerProtocolPreference.BluetoothFtms;
			GameBootstrap.Settings.LastTrainerBluetoothAddress = FakeTrainer.Remembered.BluetoothAddress;
			GameBootstrap.Settings.LastHeartRateBluetoothAddress = FakeHeartRate.Remembered.BluetoothAddress;
			GameBootstrap.Settings.LastSteeringBluetoothAddress = FakeSteering.Remembered.BluetoothAddress;
			await Settle(2);
			VerifyTrainerRecoveryRetrySignal(screen);
			VerifyAmbientActorDiagnosticOutput();
			var connection = screen.GetNode<DeviceConnectionPanelController>("DeviceConnectionPanel");
			var heartRateAction = connection.HeartRateActionForTesting;
			var steeringAction = connection.SteeringActionForTesting;
			var start = screen.GetNode<StartPanelController>("StartPanel");
			var connect = screen.GetNode<Button>("StartPanel/PanelContainer/MarginContainer/StartupScroll/VBoxContainer/ConnectTrainerButton");
			await WaitUntil(() => trainer.ControlRequestStarted && heartRate.ScanCount == 1 && steering.ScanCount == 1);
			if (!start.Visible || connection.Visible)
				throw new InvalidOperationException("Automatic startup discovery hid the home screen or opened device management.");
			if (trainer.ScanCount != 1 || heartRate.ScanCount != 1 || steering.ScanCount != 1)
				throw new InvalidOperationException($"Startup did not launch exactly one trainer/HR/steering scan: trainer={trainer.ScanCount}, HR={heartRate.ScanCount}, steering={steering.ScanCount}.");
			if (trainer.LastConnectedBluetoothAddress != FakeTrainer.Remembered.BluetoothAddress ||
				heartRate.ConnectedBluetoothAddress != FakeHeartRate.Remembered.BluetoothAddress ||
				steering.ConnectedBluetoothAddress != FakeSteering.Remembered.BluetoothAddress ||
				connection.PreRideTrainerName != FakeTrainer.Remembered.Name ||
				connection.PreRideHeartRateName != FakeHeartRate.Remembered.Name ||
				connection.PreRideSteeringName != FakeSteering.Remembered.Name)
				throw new InvalidOperationException("Startup discovery did not select the previously remembered trainer, heart-rate monitor and steering device.");
			if (connection.PreRideTrainerStatus != "Setting up controls…")
				throw new InvalidOperationException($"Home connection state did not reflect trainer setup: {connection.PreRideTrainerStatus}");
			var trainerCardStatus = start.FindChild("TrainerSummary", true, false)?.FindChild("DeviceState", true, false) as Label;
			if (trainerCardStatus?.Text != "Setting up controls…")
				throw new InvalidOperationException("The home trainer card did not show the in-progress setup state.");

			// Opening device management during the scan exposes its current state but
			// must not issue a second trainer, HR or steering scan.
			connect.EmitSignal(BaseButton.SignalName.Pressed);
			connect.EmitSignal(BaseButton.SignalName.Pressed);
			connection.ShowTrainerConnectionScreen();
			await Settle(2);
			if (!connection.Visible || trainer.ScanCount != 1 || heartRate.ScanCount != 1 || steering.ScanCount != 1)
				throw new InvalidOperationException("Opening device management during startup discovery started a duplicate scan.");
			var continueButton = connection.GetNode<Button>("PanelContainer/MarginContainer/VBoxContainer/ActionsRow/ContinueButton");
			if (!continueButton.Disabled || trainer.IsReady) throw new InvalidOperationException("Trainer was shown ready before FTMS control acknowledgement completed.");

			// Existing optional-device retry/cancel controls continue to operate after
			// their remembered devices connect automatically at startup.
			heartRateAction.EmitSignal(BaseButton.SignalName.Pressed);
			if (heartRate.IsConnected) throw new InvalidOperationException("Manual heart-rate disconnect did not disconnect the optional sensor.");
			heartRate.HoldNextScan = true;
			heartRateAction.EmitSignal(BaseButton.SignalName.Pressed);
			await WaitUntil(() => heartRate.ScanCount == 2 && heartRate.ScanPending);
			heartRateAction.EmitSignal(BaseButton.SignalName.Pressed);
			await WaitUntil(() => heartRate.ScanCancellationCount == 1);
			heartRateAction.EmitSignal(BaseButton.SignalName.Pressed);
			await WaitUntil(() => heartRate.ScanCount == 3 && !heartRate.ScanPending);

			steeringAction.EmitSignal(BaseButton.SignalName.Pressed);
			if (steering.IsConnected) throw new InvalidOperationException("Manual steering disconnect did not disconnect the optional controller.");
			steering.HoldNextScan = true;
			steeringAction.EmitSignal(BaseButton.SignalName.Pressed);
			await WaitUntil(() => steering.ScanCount == 2 && steering.ScanPending);
			steeringAction.EmitSignal(BaseButton.SignalName.Pressed);
			await WaitUntil(() => steering.ScanCancellationCount == 1);
			steeringAction.EmitSignal(BaseButton.SignalName.Pressed);
			await WaitUntil(() => steering.ScanCount == 3 && !steering.ScanPending);
			if (trainer.ScanCount != 1 || !continueButton.Disabled)
				throw new InvalidOperationException("Optional-device retry started another trainer attempt or bypassed trainer readiness.");
			trainer.CompleteControlRequest();
			await WaitUntil(() => !continueButton.Disabled);
			if (!trainer.IsReady || connection.PreRideTrainerStatus != "Connected and ready")
				throw new InvalidOperationException("Trainer readiness was not reflected after successful setup.");
			connection.GetNode<Button>("PanelContainer/MarginContainer/VBoxContainer/ActionsRow/BackButton").EmitSignal(BaseButton.SignalName.Pressed);
			await WaitUntil(() => start.Visible && !connection.Visible);
			await Settle(3);
			var trainerCardName = start.FindChild("TrainerSummary", true, false)?.FindChild("DeviceName", true, false) as Label;
			var protocolStatus = connection.FindChild("ProtocolStatusLabel", true, false) as Label;
			if (trainerCardName?.Text != FakeTrainer.Remembered.Name ||
				trainerCardStatus.Text != "Connected and ready" ||
				protocolStatus?.Text != "Bluetooth FTMS · Connected and ready")
				throw new InvalidOperationException("Returning home did not retain the live trainer readiness state.");
			if (connection.BeginStartupDiscovery() || trainer.ScanCount != 1 || heartRate.ScanCount != 3 || steering.ScanCount != 3)
				throw new InvalidOperationException("Returning home or repeating the startup callback restarted discovery.");

			connection.ShowTrainerConnectionScreen();
			(connection.FindChild("DisconnectButton", true, false) as Button
				?? throw new InvalidOperationException("Device management did not expose the existing trainer disconnect action."))
				.EmitSignal(BaseButton.SignalName.Pressed);
			await WaitUntil(() => !trainer.IsReady);
			await Settle(12);
			if (trainer.ScanCount != 1 || connection.BeginStartupDiscovery())
				throw new InvalidOperationException("Explicit trainer disconnect immediately triggered an unwanted startup reconnect.");
			screen.QueueFree();
			await Settle(2);

			await VerifyMissingTrainerCanBeRetried();
			await VerifyMultipleTrainersRequireAnExplicitChoice();
			await VerifyConfiguredTestModesSkipPhysicalDiscovery(GameBootstrap.SessionMode.Trainer, "dummy");
			await VerifyConfiguredTestModesSkipPhysicalDiscovery(GameBootstrap.SessionMode.FitReplay, "replay.fit");
			GD.Print("[DeviceConnectionAutomationReview] PASS: production RideScreen automatically selected remembered devices using the saved protocol, kept home visible during setup, avoided duplicate/repeated scans and reconnect after manual disconnect, retained optional-sensor cancellation and readiness, prompted for multiple devices, exposed retry after a miss, and skipped physical discovery for Dummy Trainer/FIT replay.");
			GetTree().Quit(0);
		}
		catch (Exception ex)
		{
			GD.PushError($"[DeviceConnectionAutomationReview] FAIL: {ex}");
			GetTree().Quit(1);
		}
	}

	private Node CreateProductionRideScreen(FakeTrainer trainer, FakeHeartRate heartRate, FakeSteering steering)
	{
		var screen = GD.Load<PackedScene>("res://Scenes/RideScreen.tscn").Instantiate();
		var connection = screen.GetNode<DeviceConnectionPanelController>("DeviceConnectionPanel");
		connection.TrainerClientFactoryForTesting = () => trainer;
		connection.HeartRateClientFactoryForTesting = () => heartRate;
		connection.SteeringClientFactoryForTesting = () => steering;
		AddChild(screen);
		return screen;
	}

	private async Task VerifyMissingTrainerCanBeRetried()
	{
		var trainer = new FakeTrainer([]);
		var heartRate = new FakeHeartRate([]);
		var steering = new FakeSteering([]);
		var screen = CreateProductionRideScreen(trainer, heartRate, steering);
		GameBootstrap.Settings.Trainer.ProtocolPreference = TrainerProtocolPreference.BluetoothFtms;
		GameBootstrap.Settings.LastTrainerBluetoothAddress = FakeTrainer.Remembered.BluetoothAddress;
		await WaitUntil(() => trainer.ScanCount == 1 &&
			screen.GetNode<DeviceConnectionPanelController>("DeviceConnectionPanel").PreRideTrainerStatus.Contains("No trainer found", StringComparison.Ordinal));
		var connection = screen.GetNode<DeviceConnectionPanelController>("DeviceConnectionPanel");
		if (!screen.GetNode<StartPanelController>("StartPanel").Visible || connection.BeginStartupDiscovery() || trainer.ScanCount != 1)
			throw new InvalidOperationException("A missing trainer was not left in a recoverable home state or startup retried in a loop.");
		screen.GetNode<Button>("StartPanel/PanelContainer/MarginContainer/StartupScroll/VBoxContainer/ConnectTrainerButton").EmitSignal(BaseButton.SignalName.Pressed);
		var retry = connection.FindChild("ConnectButton", true, false) as Button
			?? throw new InvalidOperationException("Device management did not expose the existing trainer retry action.");
		if (retry.Text != "Retry scan" || retry.Disabled)
			throw new InvalidOperationException("Device management did not expose a manual retry after startup discovery failed.");
		retry.EmitSignal(BaseButton.SignalName.Pressed);
		await WaitUntil(() => trainer.ScanCount == 2);
		if (heartRate.ScanCount != 2 || steering.ScanCount != 2)
			throw new InvalidOperationException("Manual retry did not run the existing single optional-device scans alongside trainer discovery.");
		screen.QueueFree();
		await Settle(2);
	}

	private async Task VerifyMultipleTrainersRequireAnExplicitChoice()
	{
		GameBootstrap.ResetSession();
		var trainer = new FakeTrainer([FakeTrainer.Alternative, FakeTrainer.Remembered]);
		var heartRate = new FakeHeartRate([]);
		var steering = new FakeSteering([]);
		var screen = CreateProductionRideScreen(trainer, heartRate, steering);
		GameBootstrap.Settings.Trainer.ProtocolPreference = TrainerProtocolPreference.BluetoothFtms;
		GameBootstrap.Settings.LastTrainerBluetoothAddress = null;
		await WaitUntil(() => trainer.ScanCount == 1 &&
			screen.GetNode<DeviceConnectionPanelController>("DeviceConnectionPanel").PreRideTrainerStatus == "Choose a trainer in Devices");
		var connection = screen.GetNode<DeviceConnectionPanelController>("DeviceConnectionPanel");
		if (trainer.LastConnectedBluetoothAddress is not null ||
			connection.PreRideTrainerName != "Choose a trainer" ||
			!screen.GetNode<StartPanelController>("StartPanel").Visible || connection.Visible)
			throw new InvalidOperationException("Startup guessed between multiple trainers instead of prompting on the home screen.");
		screen.GetNode<Button>("StartPanel/PanelContainer/MarginContainer/StartupScroll/VBoxContainer/ConnectTrainerButton").EmitSignal(BaseButton.SignalName.Pressed);
		var picker = connection.FindChild("TrainerPicker", true, false) as OptionButton
			?? throw new InvalidOperationException("Device management did not expose its existing trainer picker.");
		if (!connection.Visible || !picker.Visible || picker.ItemCount != 2 || trainer.ScanCount != 1)
			throw new InvalidOperationException("Device management did not expose the existing explicit trainer choice without rescanning.");
		screen.QueueFree();
		await Settle(2);
		GameBootstrap.ResetSession();
	}

	private async Task VerifyConfiguredTestModesSkipPhysicalDiscovery(GameBootstrap.SessionMode mode, string replayPath)
	{
		GameBootstrap.ResetSession();
		if (mode == GameBootstrap.SessionMode.Trainer) GameBootstrap.ConfigureFakeTrainerMode();
		else GameBootstrap.ConfigureFitReplayMode(replayPath);
		var trainer = new FakeTrainer([FakeTrainer.Remembered]);
		var heartRate = new FakeHeartRate([FakeHeartRate.Remembered]);
		var steering = new FakeSteering([FakeSteering.Remembered]);
		var screen = CreateProductionRideScreen(trainer, heartRate, steering);
		await Settle(4);
		if (trainer.ScanCount != 0 || heartRate.ScanCount != 0 || steering.ScanCount != 0)
			throw new InvalidOperationException($"Configured {mode} mode initiated physical device discovery.");
		screen.QueueFree();
		await Settle(2);
		GameBootstrap.ResetSession();
	}

	private static void VerifyTrainerRecoveryRetrySignal(Node screen)
	{
		var hud = screen.GetNode<HudController>("HudController");
		var retry = hud.FindChild("TrainerRecoveryRetryButton", true, false) as Button
			?? throw new InvalidOperationException("Production RideScreen did not create its trainer recovery retry button.");
		var firstCalls = 0;
		var latestCalls = 0;
		for (var i = 0; i < 12; i++)
			hud.SetTrainerRecoveryAction("Trainer disconnected", () => firstCalls++);
		hud.SetTrainerRecoveryAction("Retry available", () => latestCalls++);
		retry.EmitSignal(BaseButton.SignalName.Pressed);
		if (firstCalls != 0 || latestCalls != 1)
			throw new InvalidOperationException($"Repeated recovery updates must invoke only the latest action once (old={firstCalls}, latest={latestCalls}).");
		hud.SetTrainerRecoveryAction("Searching", null);
		retry.EmitSignal(BaseButton.SignalName.Pressed);
		if (latestCalls != 1 || retry.Visible || !retry.Disabled)
			throw new InvalidOperationException("Clearing recovery state left a stale retry action or visible button.");
		hud.SetTrainerRecoveryAction("Retry available", () => latestCalls++);
		retry.EmitSignal(BaseButton.SignalName.Pressed);
		if (latestCalls != 2)
			throw new InvalidOperationException("Retry did not recover after a disconnect/search state cycle.");
	}

	private static void VerifyAmbientActorDiagnosticOutput()
	{
		var payload = new
		{
			actorId = 991,
			spawnPosition = AmbientActorDiagnosticPoint.From(new System.Numerics.Vector3(12.5f, 3.25f, -8.75f)),
			pathStart = AmbientActorDiagnosticPoint.From(new System.Numerics.Vector3(1, 2, 3)),
			pathEnd = AmbientActorDiagnosticPoint.From(new System.Numerics.Vector3(4, 5, 6))
		};
		GameLogger.Log("ambient_actor_spawned", payload);
		var line = File.ReadLines(GameLogger.LogPath!).Select(text => JsonDocument.Parse(text))
			.Last(entry => entry.RootElement.GetProperty("type").GetString() == "ambient_actor_spawned");
		using (line)
		{
			var actual = line.RootElement.GetProperty("payload");
			foreach (var field in new[] { "spawnPosition", "pathStart", "pathEnd" })
				foreach (var axis in new[] { "x", "y", "z" })
					if (actual.GetProperty(field).GetProperty(axis).ValueKind != JsonValueKind.Number)
						throw new InvalidOperationException($"Emitted {field}.{axis} was not a JSON number.");
			if (actual.GetProperty("spawnPosition").GetProperty("z").GetSingle() != -8.75f)
				throw new InvalidOperationException("Emitted actor spawn coordinates did not preserve the actual position.");
		}
	}

	private async Task Settle(int frames)
	{
		for (var i = 0; i < frames; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
	}

	private async Task WaitUntil(Func<bool> condition)
	{
		for (var i = 0; i < 180 && !condition(); i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
		if (!condition()) throw new TimeoutException("Timed out waiting for the production device connection flow.");
	}

	private sealed class FakeTrainer : WindowsBleTrainerClient
	{
		public static readonly BleDiscoveredDevice Remembered = new(301, "Remembered FTMS trainer", [TrainerDiscoveryPolicy.FtmsServiceUuid], -42);
		public static readonly BleDiscoveredDevice Alternative = new(302, "Other FTMS trainer", [TrainerDiscoveryPolicy.FtmsServiceUuid], -24);
		private readonly IReadOnlyList<BleDiscoveredDevice> _devices;
		private readonly TaskCompletionSource _controlRequest = new(TaskCreationOptions.RunContinuationsAsynchronously);
		private bool _connected;
		private bool _ready;
		public FakeTrainer(IReadOnlyList<BleDiscoveredDevice> devices) => _devices = devices;
		public int ScanCount { get; private set; }
		public bool ControlRequestStarted { get; private set; }
		public ulong? LastConnectedBluetoothAddress { get; private set; }
		public override bool IsConnected => _connected;
		public override bool IsReady => _ready;
		public override string? ConnectedDeviceName => _devices.FirstOrDefault(device => device.BluetoothAddress == LastConnectedBluetoothAddress)?.Name;
		public override Task<IReadOnlyList<BleDiscoveredDevice>> ScanForDevicesAsync(TimeSpan duration, CancellationToken cancellationToken = default)
		{
			ScanCount++;
			return Task.FromResult(_devices);
		}
		public override Task ConnectToDeviceAsync(ulong bluetoothAddress, CancellationToken cancellationToken = default)
		{
			if (!_devices.Any(device => device.BluetoothAddress == bluetoothAddress)) throw new InvalidOperationException("Unexpected fake trainer identity.");
			_connected = true;
			LastConnectedBluetoothAddress = bluetoothAddress;
			return Task.CompletedTask;
		}
		public override Task SubscribeControlPointAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
		public override async Task RequestControlAsync(CancellationToken cancellationToken = default)
		{
			ControlRequestStarted = true;
			await _controlRequest.Task.WaitAsync(cancellationToken);
		}
		public override Task SubscribeFtmsTelemetryAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
		public override void MarkReady() => _ready = _connected;
		public override Task DisconnectAsync(CancellationToken cancellationToken = default)
		{
			_connected = false;
			_ready = false;
			return Task.CompletedTask;
		}
		public void CompleteControlRequest() => _controlRequest.TrySetResult();
	}

	private sealed class FakeHeartRate : WindowsBleHeartRateClient
	{
		public static readonly BleDiscoveredDevice Remembered = new(401, "Remembered heart-rate monitor", [HeartRateDiscoveryPolicy.ServiceUuid], -50);
		public static readonly BleDiscoveredDevice Alternative = new(402, "Other heart-rate monitor", [HeartRateDiscoveryPolicy.ServiceUuid], -30);
		private readonly IReadOnlyList<BleDiscoveredDevice> _devices;
		private bool _connected;
		private bool _holdNextScan;
		public FakeHeartRate(IReadOnlyList<BleDiscoveredDevice> devices) => _devices = devices;
		public int ScanCount { get; private set; }
		public bool ScanPending { get; private set; }
		public int ScanCancellationCount { get; private set; }
		public bool HoldNextScan { get => _holdNextScan; set => _holdNextScan = value; }
		public ulong? ConnectedBluetoothAddress { get; private set; }
		public override bool IsConnected => _connected;
		public override string? ConnectedDeviceName => _devices.FirstOrDefault(device => device.BluetoothAddress == ConnectedBluetoothAddress)?.Name;
		public override async Task<IReadOnlyList<BleDiscoveredDevice>> ScanAsync(TimeSpan duration, CancellationToken cancellationToken = default)
		{
			ScanCount++;
			if (_holdNextScan)
			{
				_holdNextScan = false;
				ScanPending = true;
				try { await Task.Delay(Timeout.Infinite, cancellationToken); }
				catch (OperationCanceledException) { ScanCancellationCount++; ScanPending = false; throw; }
			}
			return _devices;
		}
		public override Task ConnectAsync(BleDiscoveredDevice candidate, CancellationToken cancellationToken = default)
		{
			ConnectedBluetoothAddress = candidate.BluetoothAddress;
			_connected = true;
			return Task.CompletedTask;
		}
		public override Task DisconnectAsync() { _connected = false; ConnectedBluetoothAddress = null; return Task.CompletedTask; }
		public override void Dispose() { }
	}

	private sealed class FakeSteering : EliteSterzoSmartClient
	{
		public static readonly BleDiscoveredDevice Remembered = new(501, "Remembered Sterzo Smart", [SteeringDiscoveryPolicy.ServiceUuid], -48);
		public static readonly BleDiscoveredDevice Alternative = new(502, "Other Sterzo Smart", [SteeringDiscoveryPolicy.ServiceUuid], -28);
		private readonly IReadOnlyList<BleDiscoveredDevice> _devices;
		private bool _connected;
		private bool _holdNextScan;
		public FakeSteering(IReadOnlyList<BleDiscoveredDevice> devices) => _devices = devices;
		public int ScanCount { get; private set; }
		public bool ScanPending { get; private set; }
		public int ScanCancellationCount { get; private set; }
		public bool HoldNextScan { get => _holdNextScan; set => _holdNextScan = value; }
		public ulong? ConnectedBluetoothAddress { get; private set; }
		public override bool IsConnected => _connected;
		public override string? ConnectedDeviceName => _devices.FirstOrDefault(device => device.BluetoothAddress == ConnectedBluetoothAddress)?.Name;
		public override async Task<IReadOnlyList<BleDiscoveredDevice>> ScanAsync(TimeSpan timeout, CancellationToken cancellationToken)
		{
			ScanCount++;
			if (_holdNextScan)
			{
				_holdNextScan = false;
				ScanPending = true;
				try { await Task.Delay(Timeout.Infinite, cancellationToken); }
				catch (OperationCanceledException) { ScanCancellationCount++; ScanPending = false; throw; }
			}
			return _devices;
		}
		public override Task ConnectAsync(BleDiscoveredDevice candidate, CancellationToken cancellationToken = default)
		{
			ConnectedBluetoothAddress = candidate.BluetoothAddress;
			_connected = true;
			return Task.CompletedTask;
		}
		public override Task DisconnectAsync() { _connected = false; ConnectedBluetoothAddress = null; return Task.CompletedTask; }
		public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
	}
}

