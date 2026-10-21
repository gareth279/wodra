using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using IndoorCycling.Trainer.Models;
using IndoorCycling.Trainer.Windows;

/// <summary>Exercises first-click discovery through the production launch and connection screens with radio fakes.</summary>
public partial class DeviceConnectionAutomationReviewTest : Node
{
	public override async void _Ready()
	{
		try
		{
			var screen = GD.Load<PackedScene>("res://Scenes/RideScreen.tscn").Instantiate();
			AddChild(screen);
			await Settle(3);
			var trainer = new FakeTrainer();
			var heartRate = new FakeHeartRate();
			var steering = new FakeSteering();
			var connection = screen.GetNode<DeviceConnectionPanelController>("DeviceConnectionPanel");
			connection.TrainerClientFactoryForTesting = () => trainer;
			connection.HeartRateClientFactoryForTesting = () => heartRate;
			connection.SteeringClientFactoryForTesting = () => steering;
			var heartRateAction = connection.HeartRateActionForTesting;
			var steeringAction = connection.SteeringActionForTesting;
			var connect = screen.GetNode<Button>("StartPanel/PanelContainer/MarginContainer/StartupScroll/VBoxContainer/ConnectTrainerButton");
			GameBootstrap.Settings.Trainer.ProtocolPreference = TrainerProtocolPreference.BluetoothFtms;
			connect.EmitSignal(BaseButton.SignalName.Pressed);
			connect.EmitSignal(BaseButton.SignalName.Pressed); // A rapid second activation must not replace the active attempt.
			await WaitUntil(() => trainer.ControlRequestStarted && heartRate.ScanCount == 1 && steering.ScanCount == 1);
			if (trainer.ScanCount != 1 || heartRate.ScanCount != 1 || steering.ScanCount != 1)
				throw new InvalidOperationException($"First Connect click did not launch independent trainer/HR/steering scans: trainer={trainer.ScanCount}, HR={heartRate.ScanCount}, steering={steering.ScanCount}.");
			var continueButton = connection.GetNode<Button>("PanelContainer/MarginContainer/VBoxContainer/ActionsRow/ContinueButton");
			if (!continueButton.Disabled || trainer.IsReady) throw new InvalidOperationException("Trainer was shown ready before FTMS control acknowledgement completed.");
			heartRateAction.EmitSignal(BaseButton.SignalName.Pressed);
			await WaitUntil(() => heartRate.ScanCancellationCount == 1);
			heartRateAction.EmitSignal(BaseButton.SignalName.Pressed);
			await WaitUntil(() => heartRate.ScanCount == 2 && heartRate.ScanPending);
			steeringAction.EmitSignal(BaseButton.SignalName.Pressed);
			await WaitUntil(() => steering.ScanCancellationCount == 1);
			steeringAction.EmitSignal(BaseButton.SignalName.Pressed);
			await WaitUntil(() => steering.ScanCount == 2 && steering.ScanPending);
			if (trainer.ScanCount != 1 || !continueButton.Disabled)
				throw new InvalidOperationException("Rapid Connect or optional-device retry started another trainer attempt or bypassed trainer readiness.");
			trainer.CompleteControlRequest();
			await WaitUntil(() => !continueButton.Disabled);
			if (!trainer.IsReady || !heartRate.ScanPending || !steering.ScanPending)
				throw new InvalidOperationException("Trainer readiness waited for optional scans or optional scans ended before the delayed trainer became ready.");
			connection.GetNode<Button>("PanelContainer/MarginContainer/VBoxContainer/ActionsRow/BackButton").EmitSignal(BaseButton.SignalName.Pressed);
			await WaitUntil(() => heartRate.ScanCancellationCount == 2 && steering.ScanCancellationCount == 2);
			GD.Print("[DeviceConnectionAutomationReview] PASS: production RideScreen starts all three scans independently, ignores rapid duplicate Connect, gates readiness on FTMS setup only, retries/cancels optional scans independently, and cancels them on Back.");
			GetTree().Quit(0);
		}
		catch (Exception ex)
		{
			GD.PushError($"[DeviceConnectionAutomationReview] FAIL: {ex}");
			GetTree().Quit(1);
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
		private static readonly BleDiscoveredDevice Candidate = new(301, "Test FTMS trainer", [TrainerDiscoveryPolicy.FtmsServiceUuid], -42);
		private readonly TaskCompletionSource _controlRequest = new(TaskCreationOptions.RunContinuationsAsynchronously);
		private bool _connected;
		private bool _ready;
		public int ScanCount { get; private set; }
		public bool ControlRequestStarted { get; private set; }
		public override bool IsConnected => _connected;
		public override bool IsReady => _ready;
		public override string? ConnectedDeviceName => Candidate.Name;
		public override Task<IReadOnlyList<BleDiscoveredDevice>> ScanForDevicesAsync(TimeSpan duration, CancellationToken cancellationToken = default)
		{
			ScanCount++;
			return Task.FromResult<IReadOnlyList<BleDiscoveredDevice>>([Candidate]);
		}
		public override Task ConnectToDeviceAsync(ulong bluetoothAddress, CancellationToken cancellationToken = default)
		{
			if (bluetoothAddress != Candidate.BluetoothAddress) throw new InvalidOperationException("Unexpected fake trainer identity.");
			_connected = true;
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
		public int ScanCount { get; private set; }
		public bool ScanPending { get; private set; }
		public int ScanCancellationCount { get; private set; }
		public override async Task<IReadOnlyList<BleDiscoveredDevice>> ScanAsync(TimeSpan duration, CancellationToken cancellationToken = default)
		{
			ScanCount++;
			ScanPending = true;
			try { await Task.Delay(Timeout.Infinite, cancellationToken); }
			catch (OperationCanceledException) { ScanCancellationCount++; ScanPending = false; throw; }
			return Array.Empty<BleDiscoveredDevice>();
		}
		public override void Dispose() { }
	}

	private sealed class FakeSteering : EliteSterzoSmartClient
	{
		public int ScanCount { get; private set; }
		public bool ScanPending { get; private set; }
		public int ScanCancellationCount { get; private set; }
		public override async Task<IReadOnlyList<BleDiscoveredDevice>> ScanAsync(TimeSpan timeout, CancellationToken cancellationToken)
		{
			ScanCount++;
			ScanPending = true;
			try { await Task.Delay(Timeout.Infinite, cancellationToken); }
			catch (OperationCanceledException) { ScanCancellationCount++; ScanPending = false; throw; }
			return Array.Empty<BleDiscoveredDevice>();
		}
		public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
	}
}

