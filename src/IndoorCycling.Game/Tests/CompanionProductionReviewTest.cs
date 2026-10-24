using Godot;
using IndoorCycling.Core.Models;
using IndoorCycling.Trainer.Implementations;
using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

/// <summary>Manual local E2E driver for the real RideScreen, PHP relay and browser companion.</summary>
public partial class CompanionProductionReviewTest : Node
{
	private string _output = Path.Combine(Path.GetTempPath(), "WodraCompanionProductionReview");
	private string? _relay;
	private CompanionRelayClient? _client;
	private RideSessionNode? _session;
	private Control? _screen;

	public override async void _Ready()
	{
		try
		{
			Directory.CreateDirectory(_output);
			_output = System.Environment.GetEnvironmentVariable("WODRA_COMPANION_REVIEW_OUTPUT") ?? _output;
			_relay = System.Environment.GetEnvironmentVariable("WODRA_COMPANION_REVIEW_RELAY") ?? "http://127.0.0.1:8798";
			DisplayServer.WindowSetSize(new Vector2I(1280, 720));
			_screen = GD.Load<PackedScene>("res://Scenes/RideScreen.tscn").Instantiate<Control>();
			AddChild(_screen);
			await Frames(12);
			if (GameBootstrap.Settings.CompanionEnabled || !string.IsNullOrEmpty(GameBootstrap.Settings.CompanionRelayBaseUrl))
				throw new InvalidOperationException("A new isolated settings profile must leave the companion disabled and unconfigured by default.");
			var profile = new RiderProfileRecord(Guid.NewGuid(), "Companion Review Test", DateTimeOffset.UtcNow,
				DateTimeOffset.UtcNow, 75, 9, true, 50);
			await GameBootstrap.SaveProfileAsync(profile);
			await GameBootstrap.SetActiveProfileAsync(profile.Id);
			GameBootstrap.Settings.DebugModeEnabled = true;
			GameBootstrap.Settings.OnlineRidingEnabled = false;
			GameBootstrap.Settings.CompanionEnabled = true;
			GameBootstrap.Settings.CompanionRelayBaseUrl = _relay;
			GameBootstrap.Settings.DummyTrainer.Mode = "Watts";
			GameBootstrap.Settings.DummyTrainer.TargetWatts = 180;
			GameBootstrap.Settings.DummyTrainer.CadenceRpm = 88;
			GameBootstrap.Settings.Recording.OutputFolder = Path.Combine(ProjectSettings.GlobalizePath("user://"), "companion-review-fit");
			GameBootstrap.Settings.Save();
			GameBootstrap.ConfigureFakeTrainerMode();
			_session = _screen.GetNode<RideSessionNode>("RideSessionNode");
			_client = _screen.GetNode<CompanionRelayClient>("StartPanel/CompanionRelayClient");
			_client.Configure(_session, true, _relay);
			var start = _screen.GetNode<StartPanelController>("StartPanel");
			foreach (var window in _screen.FindChildren("*", "Window", recursive: true, owned: false).OfType<Window>())
				window.Hide();
			start.ShowOptions(false);
			await Frames(3);
			var createPairing = _screen.FindChildren("StartCompanionPairing", "Button", recursive: true, owned: false).OfType<Button>().Single();
			createPairing.EmitSignal(Button.SignalName.Pressed);
			for (var wait = 0; wait < 300 && !_client.HasInvitation; wait++) await Frames(1);
			if (!_client.HasInvitation) throw new InvalidOperationException("Production Options could not create a pairing invitation through its actual button.");
			await Frames(3);
			var options = _screen.FindChildren("OptionsWindow", "Window", recursive: true, owned: false).OfType<Window>().FirstOrDefault()
				?? throw new InvalidOperationException("Production Options window was not created.");
			CaptureWindow(options, "pairing-desktop.png");
			GD.Print($"[CompanionProductionReview] PAIR_CODE={_client.ManualCode} EXPIRES_IN=180s");
			GD.Print("[CompanionProductionReview] Waiting for the actual browser to request pairing; the test will invoke the same explicit desktop approval action after the pending request appears.");

			var approved = false;
			for (var frame = 0; frame < 18000 && !approved; frame++)
			{
				await Frames(1);
				if (!_client.PhonePending) continue;
				await Frames(2);
				CaptureWindow(options, "pairing-phone-pending.png");
				var approve = options.FindChildren("ApproveCompanion", "Button", recursive: true, owned: false).OfType<Button>().Single();
				approve.EmitSignal(Button.SignalName.Pressed);
				for (var wait = 0; wait < 120 && !_client.IsPaired; wait++) await Frames(1);
				approved = _client.IsPaired;
			}
			if (!approved) throw new TimeoutException("No browser pairing request arrived within five minutes.");
			GD.Print("[CompanionProductionReview] PAIR_APPROVED");
			options.Hide();
			await RunRideAsync("dunedin-one-way-oval-gardens", "road-live.png");
			await RunRideAsync("wodra-velodrome-250", "velodrome-live.png");
			await _session.ExitToRouteSelectionAsync();
			await _client.CancelAsync();
			CaptureViewport("ride-ended.png");
			GD.Print("[CompanionProductionReview] PASS: production RideScreen paired to a real local browser through the local PHP relay; road and velodrome dummy snapshots sent; isolated profile, recording and artifacts only.");
			GetTree().Quit(0);
		}
		catch (Exception error)
		{
			GD.PushError($"[CompanionProductionReview] FAIL: {error}");
			GetTree().Quit(1);
		}
	}

	private async Task RunRideAsync(string routeId, string screenshot)
	{
		var route = RouteCatalog.GetById(routeId) ?? throw new InvalidOperationException($"Production route {routeId} is missing.");
		if (!await _session!.StartTrainerSessionAsync(route)) throw new InvalidOperationException($"Could not start dummy ride on {routeId}.");
		_screen!.GetNode<HudController>("HudController").Show();
		_screen.GetNode<StartPanelController>("StartPanel").Hide();
		await Frames(180);
		CaptureViewport(screenshot);
		var payload = JsonSerializer.Serialize(CompanionRelayClient.CreateSnapshot(_session));
		GD.Print($"[CompanionProductionReview] AUTHORITATIVE_SNAPSHOT route={routeId} {payload}");
		var telemetry = GameBootstrap.TrainerClient?.GetLatestTelemetry();
		GD.Print($"[CompanionProductionReview] DUMMY_SAMPLE connected={GameBootstrap.TrainerClient?.IsConnected} received={telemetry?.HasReceivedMeasurement} power={telemetry?.HasPowerMeasurement} cadence={telemetry?.HasCadenceMeasurement} age_ms={(telemetry is null ? -1 : (DateTimeOffset.UtcNow - telemetry.Timestamp).TotalMilliseconds):F0} fresh={telemetry?.IsFreshAt(DateTimeOffset.UtcNow)}");
		if (GameBootstrap.TrainerClient is not FakeTrainerClient fake || !fake.IsConnected)
			throw new InvalidOperationException("Dummy trainer was not connected during companion ride.");
		using (var snapshot = JsonDocument.Parse(payload))
		{
			var root = snapshot.RootElement;
			if (root.GetProperty("data_stale").GetBoolean() || root.GetProperty("power_w").ValueKind != JsonValueKind.Number ||
				root.GetProperty("cadence_rpm").ValueKind != JsonValueKind.Number)
				throw new InvalidOperationException("Fresh dummy trainer samples must remain visible in the companion snapshot.");
		}
		await Frames(720);
		await _session.ExitToRouteSelectionAsync();
	}

	private void CaptureWindow(Window window, string name)
	{
		RenderingServer.ForceDraw(false);
		var image = window.GetTexture().GetImage();
		if (image.IsEmpty() || image.SavePng(Path.Combine(_output, name)) != Error.Ok)
			throw new IOException($"Could not capture production UI window {name}.");
	}

	private void CaptureViewport(string name)
	{
		RenderingServer.ForceDraw(false);
		var image = GetViewport().GetTexture().GetImage();
		if (image.IsEmpty() || image.SavePng(Path.Combine(_output, name)) != Error.Ok)
			throw new IOException($"Could not capture RideScreen {name}.");
	}

	private async Task Frames(int count)
	{
		for (var i = 0; i < count; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
	}
}
