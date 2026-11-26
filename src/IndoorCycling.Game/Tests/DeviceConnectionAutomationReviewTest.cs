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
		if (System.Environment.GetEnvironmentVariable("WODRA_INTERACTIVE_DEVICE_REVIEW") == "1")
		{
			await ShowInteractiveDeviceReviewAsync();
			return;
		}
		var previousSettingsPath = AppSettings.SettingsPathForTesting;
		var isolatedSettingsDirectory = Path.Combine(Path.GetTempPath(), $"Wodra-DeviceConnectionReview-{Guid.NewGuid():N}");
		AppSettings.SettingsPathForTesting = Path.Combine(isolatedSettingsDirectory, "settings.json");
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

			await VerifyAntConfigurationSurfacesAndKeyEditing();
			await VerifyMissingTrainerCanBeRetried();
			await VerifyAutoFallbackAfterFtmsFailure();
			await VerifyExplicitProtocolFailureOffersAlternateAction();
			await VerifyMultipleTrainersRequireAnExplicitChoice();
			await VerifyConfiguredTestModesSkipPhysicalDiscovery(GameBootstrap.SessionMode.Trainer, "dummy");
			await VerifyConfiguredTestModesSkipPhysicalDiscovery(GameBootstrap.SessionMode.FitReplay, "replay.fit");
			await VerifyConnectedDevicesReleasedBeforeTestModes();
			await VerifyTestModeReleaseWaitIsBounded();
			GD.Print("[DeviceConnectionAutomationReview] PASS: production RideScreen retained recovery behavior and ANT+ configuration remained accessible from Settings and trainer connection with missing/present keys and Bluetooth selected; ANT+ remained selectable with prerequisites explained; key entry, replacement, invalid replacement, clear and independent receiver/network action passed.");
			GetTree().Quit(0);
		}
		catch (Exception ex)
		{
			GD.PushError($"[DeviceConnectionAutomationReview] FAIL: {ex}");
			GetTree().Quit(1);
		}
		finally
		{
			AppSettings.SettingsPathForTesting = previousSettingsPath;
			if (Directory.Exists(isolatedSettingsDirectory)) Directory.Delete(isolatedSettingsDirectory, recursive: true);
		}
	}

	private async Task ShowInteractiveDeviceReviewAsync()
	{
		var reviewDirectory = Path.Combine(Path.GetTempPath(), $"Wodra-InteractiveDeviceReview-{Guid.NewGuid():N}");
		Directory.CreateDirectory(reviewDirectory);
		AppSettings.SettingsPathForTesting = Path.Combine(reviewDirectory, "settings.json");
		GameBootstrap.ResetSession();
		var keyPath = Path.Combine(reviewDirectory, "ant-config.json");
		var screen = CreateProductionRideScreen(new FakeTrainer([]), new FakeHeartRate([]), new FakeSteering([]), connection =>
		{
			connection.ProtocolPreferenceForTesting = TrainerProtocolPreference.BluetoothFtms;
			connection.AntNetworkStatusForTesting = () => AntNetworkKeyMaterial.GetStatus(keyPath);
			connection.AntNetworkKeyForTesting = () => Convert.ToHexString(AntNetworkKeyMaterial.Load(keyPath));
			connection.AntNetworkKeySaveForTesting = value => AntNetworkKeyMaterial.Save(value, keyPath);
			connection.AntNetworkKeyClearForTesting = () => AntNetworkKeyMaterial.Clear(keyPath);
			connection.AntNetworkInitializeForTesting = () => Task.CompletedTask;
		});
		GameBootstrap.Settings.Trainer.ProtocolPreference = TrainerProtocolPreference.BluetoothFtms;
		await Settle(2);
		screen.GetNode<DeviceConnectionPanelController>("DeviceConnectionPanel").ShowTrainerConnectionScreen();
		await WaitUntil(() => screen.GetNode<DeviceConnectionPanelController>("DeviceConnectionPanel").PreRideTrainerStatus.Contains("No trainer found", StringComparison.Ordinal));
		GD.Print($"[DeviceConnectionInteractiveReview] Ready with isolated settings and key storage at {reviewDirectory}; fake BLE/ANT transports only.");
	}

	private async Task VerifyAntConfigurationSurfacesAndKeyEditing()
	{
		GameBootstrap.ResetSession();
		GameBootstrap.Settings.Trainer.ProtocolPreference = TrainerProtocolPreference.BluetoothFtms;
		var trainer = new FakeTrainer([]);
		var keyConfigured = false;
		var screen = CreateProductionRideScreen(trainer, new FakeHeartRate([]), new FakeSteering([]), connection =>
		{
			connection.AntNetworkStatusForTesting = () => keyConfigured ? "Configured" : "Not configured";
		});
		try
		{
			await WaitUntil(() => trainer.ScanCount == 1 && screen.GetNode<DeviceConnectionPanelController>("DeviceConnectionPanel").PreRideTrainerStatus.Contains("No trainer found", StringComparison.Ordinal));
			var connection = screen.GetNode<DeviceConnectionPanelController>("DeviceConnectionPanel");
			connection.ShowTrainerConnectionScreen();
			if (connection.AntSetupPanelForTesting is not { Visible: true } connectionSetup ||
				connectionSetup.FindChild("AntNetworkKeyInput", true, false) is not LineEdit)
				throw new InvalidOperationException("The Devices screen did not keep ANT+ configuration directly accessible alongside device status.");

			var protocol = connection.ProtocolPreferenceOptionForTesting;
			var antIndex = Enumerable.Range(0, protocol.ItemCount).FirstOrDefault(i => protocol.GetItemId(i) == (int)TrainerProtocolPreference.AntFeC, -1);
			if (antIndex < 0) throw new InvalidOperationException("ANT+ FE-C was hidden from the trainer protocol selector.");
			protocol.Select(antIndex);
			protocol.EmitSignal(OptionButton.SignalName.ItemSelected, (long)antIndex);
			var overallStatus = connection.FindChild("OverallStatusLabel", true, false) as Label;
			if (GameBootstrap.Settings.Trainer.ProtocolPreference != TrainerProtocolPreference.AntFeC ||
				overallStatus?.Text.Contains("network key", StringComparison.OrdinalIgnoreCase) != true ||
				!overallStatus.Text.Contains("ANTUSB-m", StringComparison.Ordinal) ||
				!overallStatus.Text.Contains("FE-C trainer", StringComparison.Ordinal))
				throw new InvalidOperationException($"Selecting ANT+ FE-C with missing prerequisites did not stay selected and explain the missing network key, receiver, and trainer requirements. preference={GameBootstrap.Settings.Trainer.ProtocolPreference}, status={overallStatus?.Text}");
			var reloadedSettings = AppSettings.LoadOrCreate();
			var appInstallPath = Path.GetFullPath(AppContext.BaseDirectory);
			if (reloadedSettings.Trainer.ProtocolPreference != TrainerProtocolPreference.AntFeC ||
				Path.GetFullPath(AntNetworkKeyMaterial.ConfigurationPath).StartsWith(appInstallPath, StringComparison.OrdinalIgnoreCase) ||
				!Path.GetFullPath(AntNetworkKeyMaterial.ConfigurationPath).StartsWith(Path.GetFullPath(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
				Path.GetFullPath(AppSettings.SettingsPath).StartsWith(appInstallPath, StringComparison.OrdinalIgnoreCase))
				throw new InvalidOperationException("The saved ANT+ protocol preference, user settings, or key location is not preserved outside the versioned application payload.");
			keyConfigured = true;
			connection.RefreshAntSetupForTesting();
			if (connection.AntSetupPanelForTesting.StatusForTestingLabel?.Text != "Configured")
				throw new InvalidOperationException("ANT+ key status did not refresh after configuration changed.");

			var start = screen.GetNode<StartPanelController>("StartPanel");
			start.ShowOptions(false);
			var settings = screen.GetNode<Window>("OptionsWindow");
			if (settings.Title != "Settings" || settings.FindChild("AntNetworkSetup", true, false) is AntNetworkSetupPanel)
				throw new InvalidOperationException("ANT+ configuration was duplicated outside the unified Devices screen.");

			var testDirectory = Path.Combine(Path.GetTempPath(), $"Wodra-AntSetupUi-{Guid.NewGuid():N}");
			var configPath = Path.Combine(testDirectory, "ant-config.json");
			var initializationCalls = 0;
			var setup = new AntNetworkSetupPanel
			{
				Name = "AntNetworkEditingReview",
				StatusForTesting = () => AntNetworkKeyMaterial.GetStatus(configPath),
				LoadForTesting = () => Convert.ToHexString(AntNetworkKeyMaterial.Load(configPath)),
				SaveForTesting = value => AntNetworkKeyMaterial.Save(value, configPath),
				ClearForTesting = () => AntNetworkKeyMaterial.Clear(configPath),
				InitializeForTesting = () => { initializationCalls++; return Task.CompletedTask; }
			};
			AddChild(setup);
			try
			{
				await Settle(1);
				setup.KeyInputForTesting!.Text = "0123456789abcdef";
				setup.SaveForTestingButton!.EmitSignal(BaseButton.SignalName.Pressed);
				if (!AntNetworkKeyMaterial.Load(configPath).SequenceEqual(Convert.FromHexString("0123456789ABCDEF")) || setup.KeyInputForTesting.Text.Length != 0)
					throw new InvalidOperationException("ANT+ key entry/save did not persist and clear the secret input.");
				setup.KeyInputForTesting.Text = "ffeeddccbbaa9988";
				setup.SaveForTestingButton.EmitSignal(BaseButton.SignalName.Pressed);
				if (!AntNetworkKeyMaterial.Load(configPath).SequenceEqual(Convert.FromHexString("FFEEDDCCBBAA9988")))
					throw new InvalidOperationException("ANT+ key replacement did not persist the new key.");
				setup.RevealForTestingButton.EmitSignal(BaseButton.SignalName.Pressed);
				if (setup.KeyInputForTesting!.Secret || setup.KeyInputForTesting.Text != "ffeeddccbbaa9988")
					throw new InvalidOperationException("Deliberately revealing the saved key did not show the existing configuration.");
				setup.RevealForTestingButton.EmitSignal(BaseButton.SignalName.Pressed);
				if (!setup.KeyInputForTesting.Secret || setup.KeyInputForTesting.Text.Length != 0)
					throw new InvalidOperationException("Hiding the saved key did not clear the revealed value.");
				setup.KeyInputForTesting.Text = "invalid";
				setup.SaveForTestingButton.EmitSignal(BaseButton.SignalName.Pressed);
				if (!AntNetworkKeyMaterial.Load(configPath).SequenceEqual(Convert.FromHexString("FFEEDDCCBBAA9988")))
					throw new InvalidOperationException("Rejecting an invalid replacement modified the existing saved key.");
				setup.ClearForTestingButton!.EmitSignal(BaseButton.SignalName.Pressed);
				if (File.Exists(configPath) || setup.StatusForTestingLabel?.Text != "Not configured")
					throw new InvalidOperationException("ANT+ key clearing did not remove the saved configuration.");
				setup.KeyInputForTesting.Text = "0123456789abcdef";
				setup.SaveForTestingButton.EmitSignal(BaseButton.SignalName.Pressed);
				if (!AntNetworkKeyMaterial.Load(configPath).SequenceEqual(Convert.FromHexString("0123456789ABCDEF")))
					throw new InvalidOperationException("ANT+ key could not be saved again after clearing.");
				setup.InitializeForTestingButton!.EmitSignal(BaseButton.SignalName.Pressed);
				await WaitUntil(() => initializationCalls == 1);
				if (setup.StatusForTestingLabel?.Text.Contains("Receiver opened", StringComparison.Ordinal) != true)
					throw new InvalidOperationException("Receiver/network initialization was not independently executable without starting a trainer connection.");
			}
			finally
			{
				setup.QueueFree();
				if (Directory.Exists(testDirectory)) Directory.Delete(testDirectory, recursive: true);
			}
		}
		finally
		{
			screen.QueueFree();
			await Settle(2);
			GameBootstrap.ResetSession();
		}
	}

	private Node CreateProductionRideScreen(FakeTrainer trainer, FakeHeartRate heartRate, FakeSteering steering,
		Action<DeviceConnectionPanelController>? configure = null)
	{
		var screen = GD.Load<PackedScene>("res://Scenes/RideScreen.tscn").Instantiate();
		var connection = screen.GetNode<DeviceConnectionPanelController>("DeviceConnectionPanel");
		connection.TrainerClientFactoryForTesting = () => trainer;
		connection.HeartRateClientFactoryForTesting = () => heartRate;
		connection.SteeringClientFactoryForTesting = () => steering;
		configure?.Invoke(connection);
		AddChild(screen);
		return screen;
	}

	private async Task VerifyAutoFallbackAfterFtmsFailure()
	{
		GameBootstrap.ResetSession();
		GameBootstrap.Settings.Trainer.ProtocolPreference = TrainerProtocolPreference.Auto;
		GameBootstrap.Settings.Trainer.LastSuccessfulProtocol = null;
		var trainer = new FakeTrainer([FakeTrainer.Remembered]) { FailConnect = true };
		var heartRate = new FakeHeartRate([]);
		var steering = new FakeSteering([]);
		var antAttempts = 0;
		var screen = CreateProductionRideScreen(trainer, heartRate, steering,
			connection =>
			{
				connection.ProtocolPreferenceForTesting = TrainerProtocolPreference.Auto;
				connection.AntConnectionForTesting = (_, _) => { antAttempts++; return Task.CompletedTask; };
			});
		var connection = screen.GetNode<DeviceConnectionPanelController>("DeviceConnectionPanel");
		await WaitUntil(() => trainer.ScanCount == 1);
		await WaitUntil(() => antAttempts == 1);
		await WaitUntil(() => connection.AlternativeProtocolButtonForTesting.Visible);
		if (connection.AlternativeProtocolButtonForTesting.Text != "Retry Bluetooth FTMS")
			throw new InvalidOperationException("Auto did not offer Bluetooth recovery after discovered FTMS setup failed and the ANT+ attempt failed.");
		screen.QueueFree();
		await Settle(2);
		GameBootstrap.ResetSession();
	}

	private async Task VerifyExplicitProtocolFailureOffersAlternateAction()
	{
		GameBootstrap.ResetSession();
		GameBootstrap.Settings.Trainer.ProtocolPreference = TrainerProtocolPreference.BluetoothFtms;
		var trainer = new FakeTrainer([FakeTrainer.Remembered]) { FailConnect = true };
		var heartRate = new FakeHeartRate([]);
		var steering = new FakeSteering([]);
		var antAttempts = 0;
		var screen = CreateProductionRideScreen(trainer, heartRate, steering,
			connection => connection.AntConnectionForTesting = (_, _) => { antAttempts++; return Task.CompletedTask; });
		var connection = screen.GetNode<DeviceConnectionPanelController>("DeviceConnectionPanel");
		await WaitUntil(() => trainer.ScanCount == 1 && connection.PreRideTrainerStatus.StartsWith("Connection needs attention", StringComparison.Ordinal));
		connection.ShowTrainerConnectionScreen();
		await WaitUntil(() => connection.AlternativeProtocolButtonForTesting.Visible);
		if (antAttempts != 0 || connection.AlternativeProtocolButtonForTesting.Text != "Try ANT+ FE-C" ||
			GameBootstrap.Settings.Trainer.ProtocolPreference != TrainerProtocolPreference.BluetoothFtms)
			throw new InvalidOperationException("An explicit Bluetooth preference was overridden or its alternate-protocol recovery action was missing.");
		screen.QueueFree();
		await Settle(2);
		GameBootstrap.ResetSession();
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

	private async Task VerifyConnectedDevicesReleasedBeforeTestModes()
	{
		await VerifyConnectedDevicesReleasedBeforeDummyMode();
		await VerifyConnectedDevicesReleasedBeforeFitReplay();
	}

	private async Task<(Node Screen, FakeTrainer Trainer, FakeHeartRate HeartRate, FakeSteering Steering)> CreateAutoConnectedPhysicalSession()
	{
		GameBootstrap.ResetSession();
		GameBootstrap.Settings.Trainer.ProtocolPreference = TrainerProtocolPreference.BluetoothFtms;
		GameBootstrap.Settings.LastTrainerBluetoothAddress = FakeTrainer.Remembered.BluetoothAddress;
		GameBootstrap.Settings.LastHeartRateBluetoothAddress = FakeHeartRate.Remembered.BluetoothAddress;
		GameBootstrap.Settings.LastSteeringBluetoothAddress = FakeSteering.Remembered.BluetoothAddress;
		var trainer = new FakeTrainer([FakeTrainer.Remembered]);
		var heartRate = new FakeHeartRate([FakeHeartRate.Remembered]);
		var steering = new FakeSteering([FakeSteering.Remembered]);
		var screen = CreateProductionRideScreen(trainer, heartRate, steering);
		var connection = screen.GetNode<DeviceConnectionPanelController>("DeviceConnectionPanel");
		await WaitUntil(() => trainer.ControlRequestStarted && heartRate.IsConnected && steering.IsConnected);
		trainer.CompleteControlRequest();
		await WaitUntil(() => trainer.IsReady && connection.PreRideTrainerStatus == "Connected and ready");
		if (GameBootstrap.Settings.LastTrainerBluetoothAddress != FakeTrainer.Remembered.BluetoothAddress ||
			GameBootstrap.Settings.LastHeartRateBluetoothAddress != FakeHeartRate.Remembered.BluetoothAddress ||
			GameBootstrap.Settings.LastSteeringBluetoothAddress != FakeSteering.Remembered.BluetoothAddress ||
			GameBootstrap.Settings.Trainer.ProtocolPreference != TrainerProtocolPreference.BluetoothFtms)
			throw new InvalidOperationException("The physical startup fixture did not retain all remembered-device preferences.");
		return (screen, trainer, heartRate, steering);
	}

	private async Task VerifyConnectedDevicesReleasedBeforeDummyMode()
	{
		var (screen, trainer, heartRate, steering) = await CreateAutoConnectedPhysicalSession();
		var start = screen.GetNode<StartPanelController>("StartPanel");
		var routes = screen.GetNode<RouteSelectionPanelController>("RouteSelectionPanel");
		start.ShowOptions(returnToRoutes: false);
		await Settle(3);
		var options = screen.GetNode<Window>("OptionsWindow");
		(options.FindChild("AdvancedTestingToggle", true, false) as Button
			?? throw new InvalidOperationException("Options did not expose its Advanced toggle.")).ButtonPressed = true;
		await Settle(2);
		options.FindChildren("Test with Fake Trainer", "Button", true, false).OfType<Button>().Single()
			.EmitSignal(BaseButton.SignalName.Pressed);
		await WaitUntil(() => routes.Visible && GameBootstrap.TrainerClient is IndoorCycling.Trainer.Implementations.FakeTrainerClient);
		AssertPhysicalDevicesReleased(screen, trainer, heartRate, steering, "Dummy Trainer");
		if (GameBootstrap.Settings.LastTrainerBluetoothAddress != FakeTrainer.Remembered.BluetoothAddress ||
			GameBootstrap.Settings.LastHeartRateBluetoothAddress != FakeHeartRate.Remembered.BluetoothAddress ||
			GameBootstrap.Settings.LastSteeringBluetoothAddress != FakeSteering.Remembered.BluetoothAddress)
			throw new InvalidOperationException("Switching to Dummy Trainer overwrote remembered physical-device preferences.");
		routes.FindChildren("", "Button", true, false).OfType<Button>().Single(button => button.Text == "Back")
			.EmitSignal(BaseButton.SignalName.Pressed);
		await WaitUntil(() => start.Visible && !routes.Visible);
		await Settle(15);
		AssertNoAutomaticReconnect(screen, trainer, heartRate, steering, "Dummy Trainer return to Home");
		screen.QueueFree();
		await Settle(2);
		GameBootstrap.ResetSession();
	}

	private async Task VerifyConnectedDevicesReleasedBeforeFitReplay()
	{
		var (screen, trainer, heartRate, steering) = await CreateAutoConnectedPhysicalSession();
		var start = screen.GetNode<StartPanelController>("StartPanel");
		var rideSession = screen.GetNode<RideSessionNode>("RideSessionNode");
		var fileDialog = start.GetNode<FileDialog>("FileDialog");
		// Exercise the production FileSelected callback directly. The separate
		// pre-ride layout test covers opening/dismissing the dialog.
		fileDialog.EmitSignal(FileDialog.SignalName.FileSelected, Path.Combine(Path.GetTempPath(), "device-transition.fit"));
		await WaitUntil(() => rideSession.IsRideActive && GameBootstrap.Mode == GameBootstrap.SessionMode.FitReplay);
		AssertPhysicalDevicesReleased(screen, trainer, heartRate, steering, "FIT replay");
		if (GameBootstrap.TrainerClient is not null)
			throw new InvalidOperationException("FIT replay retained the previous physical trainer client.");
		await screen.GetNode<RideMenuPanelController>("RideMenuPanel").EndRideAndShowSummaryAsync();
		var home = screen.GetNode<StartPanelController>("StartPanel");
		await WaitUntil(() => home.Visible && !rideSession.IsRideActive);
		await Settle(15);
		AssertNoAutomaticReconnect(screen, trainer, heartRate, steering, "FIT replay return to Home");
		screen.QueueFree();
		await Settle(2);
		GameBootstrap.ResetSession();
	}

	private async Task VerifyTestModeReleaseWaitIsBounded()
	{
		var (screen, trainer, heartRate, steering) = await CreateAutoConnectedPhysicalSession();
		var connection = screen.GetNode<DeviceConnectionPanelController>("DeviceConnectionPanel");
		var physicalTrainer = GameBootstrap.TrainerClient;
		connection.HeartRateActionForTesting.EmitSignal(BaseButton.SignalName.Pressed);
		heartRate.HoldNextScanIgnoringCancellation();
		connection.HeartRateActionForTesting.EmitSignal(BaseButton.SignalName.Pressed);
		await WaitUntil(() => heartRate.ScanPending);
		connection.DeviceReleaseWaitTimeoutForTesting = TimeSpan.FromMilliseconds(150);
		if (await connection.ReleasePhysicalDevicesForTestModeAsync())
			throw new InvalidOperationException("A non-settling optional-device operation was treated as safely released.");
		if (trainer.IsReady == false || !trainer.IsConnected || !ReferenceEquals(GameBootstrap.TrainerClient, physicalTrainer) ||
			GameBootstrap.Mode != GameBootstrap.SessionMode.Trainer)
			throw new InvalidOperationException("A timed-out device release changed or disconnected the active physical-trainer mode.");
		heartRate.ReleaseHeldScan();
		await WaitUntil(() => !heartRate.ScanPending);
		connection.DeviceReleaseWaitTimeoutForTesting = TimeSpan.FromSeconds(10);
		if (!await connection.ReleasePhysicalDevicesForTestModeAsync())
			throw new InvalidOperationException("A completed optional-device operation did not allow a later safe release retry.");
		AssertPhysicalDevicesReleased(screen, trainer, heartRate, steering, "retried test-mode release");
		screen.QueueFree();
		await Settle(2);
		GameBootstrap.ResetSession();
	}

	private static void AssertPhysicalDevicesReleased(Node screen, FakeTrainer trainer, FakeHeartRate heartRate,
		FakeSteering steering, string targetMode)
	{
		if (trainer.IsConnected || trainer.IsReady || heartRate.IsConnected || steering.IsConnected ||
			trainer.DisconnectCount == 0 || heartRate.DisconnectCount == 0 || steering.DisconnectCount == 0 ||
			GameBootstrap.HeartRateClient is not null)
			throw new InvalidOperationException($"Switching to {targetMode} did not release every connected physical client.");
		var connection = screen.GetNode<DeviceConnectionPanelController>("DeviceConnectionPanel");
		if (connection.PreRideTrainerStatus.Contains("Reconnecting", StringComparison.OrdinalIgnoreCase))
			throw new InvalidOperationException($"Switching to {targetMode} left trainer recovery active.");
	}

	private static void AssertNoAutomaticReconnect(Node screen, FakeTrainer trainer, FakeHeartRate heartRate,
		FakeSteering steering, string transition)
	{
		if (trainer.ScanCount != 1 || heartRate.ScanCount != 1 || steering.ScanCount != 1 ||
			trainer.IsConnected || heartRate.IsConnected || steering.IsConnected ||
			screen.GetNode<DeviceConnectionPanelController>("DeviceConnectionPanel").BeginStartupDiscovery())
			throw new InvalidOperationException($"{transition} caused an unwanted physical-device reconnect or repeated discovery.");
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
		public bool FailConnect { get; init; }
		public int DisconnectCount { get; private set; }
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
			if (FailConnect) throw new GattConnectionUnavailableException("FTMS service discovery returned Unreachable.");
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
			DisconnectCount++;
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
		private TaskCompletionSource? _heldScan;
		public FakeHeartRate(IReadOnlyList<BleDiscoveredDevice> devices) => _devices = devices;
		public int ScanCount { get; private set; }
		public bool ScanPending { get; private set; }
		public int ScanCancellationCount { get; private set; }
		public int DisconnectCount { get; private set; }
		public bool HoldNextScan { get => _holdNextScan; set => _holdNextScan = value; }
		public void HoldNextScanIgnoringCancellation() => _heldScan = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		public void ReleaseHeldScan() => _heldScan?.TrySetResult();
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
			if (_heldScan is { } heldScan)
			{
				ScanPending = true;
				try { await heldScan.Task; }
				finally { ScanPending = false; _heldScan = null; }
			}
			return _devices;
		}
		public override Task ConnectAsync(BleDiscoveredDevice candidate, CancellationToken cancellationToken = default)
		{
			ConnectedBluetoothAddress = candidate.BluetoothAddress;
			_connected = true;
			return Task.CompletedTask;
		}
		public override Task DisconnectAsync() { DisconnectCount++; _connected = false; ConnectedBluetoothAddress = null; return Task.CompletedTask; }
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
		public int DisconnectCount { get; private set; }
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
		public override Task DisconnectAsync() { DisconnectCount++; _connected = false; ConnectedBluetoothAddress = null; return Task.CompletedTask; }
		public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
	}
}

