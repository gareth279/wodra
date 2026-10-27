using Godot;
using IndoorCycling.Core.Models;
using IndoorCycling.Trainer.Implementations;
using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
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
	private (string Id, string Token)? _phonePair;
	private static readonly System.Net.Http.HttpClient RelayHttp = new() { Timeout = TimeSpan.FromSeconds(10) };

	public override async void _Ready()
	{
		try
		{
			_output = System.Environment.GetEnvironmentVariable("WODRA_COMPANION_REVIEW_OUTPUT") ?? _output;
			Directory.CreateDirectory(_output);
			_relay = System.Environment.GetEnvironmentVariable("WODRA_COMPANION_REVIEW_RELAY") ?? "http://127.0.0.1:8798";
			DisplayServer.WindowSetSize(new Vector2I(1280, 720));
			_screen = GD.Load<PackedScene>("res://Scenes/RideScreen.tscn").Instantiate<Control>();
			AddChild(_screen);
			await Frames(12);
			if ((GameBootstrap.Settings.CompanionEnabled || !string.IsNullOrEmpty(GameBootstrap.Settings.CompanionRelayBaseUrl)) &&
				System.Environment.GetEnvironmentVariable("WODRA_COMPANION_REVIEW_ALLOW_CONFIGURED_SETTINGS") != "1")
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
			GameBootstrap.Settings.Recording.OutputFolder = Path.Combine(_output, "fit");
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
			GD.Print("[CompanionProductionReview] Pairing code is visible in the captured Options window; it is intentionally omitted from logs.");
			if (System.Environment.GetEnvironmentVariable("WODRA_COMPANION_REVIEW_AUTO_PAIR") == "1")
				_phonePair = await ClaimPhoneAsync(_relay, _client.ManualCode);
			GD.Print("[CompanionProductionReview] Waiting for the actual browser or isolated phone protocol client to request pairing.");

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
			if (!approved) throw new TimeoutException("No companion pairing request arrived within five minutes.");
			GD.Print("[CompanionProductionReview] PAIR_APPROVED");
			options.Hide();
			await RunRideAsync("dunedin-one-way-oval-gardens", "road-live.png");
			await RunRideAsync("wodra-velodrome-250", "velodrome-live.png");
			await _session.ExitToRouteSelectionAsync();
			await _client.CancelAsync();
			if (_phonePair is { } disconnectedPhone && await ReadPhoneRouteAsync(_relay, disconnectedPhone) is not null)
				throw new InvalidOperationException("A disconnected phone session still received ride data.");
			if (_phonePair is not null)
			{
				if (!await _client.CreateInvitationAsync()) throw new InvalidOperationException("The production client could not create a replacement invitation after disconnect.");
				_phonePair = await ClaimPhoneAsync(_relay, _client.ManualCode);
				for (var wait = 0; wait < 600 && !_client.PhonePending; wait++) await Frames(1);
				if (!await _client.DecideAsync(true) || !_client.IsPaired)
					throw new InvalidOperationException("The production client could not reconnect a new phone session after disconnect.");
				await _client.CancelAsync();
			}
			CaptureViewport("ride-ended.png");
			GD.Print("[CompanionProductionReview] PASS: production RideScreen paired through the production PHP relay; road and velodrome dummy snapshots sent; disconnect and reconnect verified; isolated profile, recording and artifacts only.");
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
		if (_phonePair is { } phone)
		{
			string? observedRoute = null;
			for (var attempt = 0; attempt < 6 && observedRoute is null; attempt++)
			{
				await Frames(60);
				observedRoute = await ReadPhoneRouteAsync(_relay!, phone);
			}
			if (observedRoute != route.Name)
				throw new InvalidOperationException($"The production phone session did not receive the {routeId} snapshot.");
			GD.Print($"[CompanionProductionReview] PHONE_READ route={observedRoute}");
		}
		await _session.ExitToRouteSelectionAsync();
	}

	private static async Task<(string Id, string Token)> ClaimPhoneAsync(string relay, string code)
	{
		var token = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
		var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
		var api = new Uri(new Uri(relay.EndsWith('/') ? relay : relay + "/"), "api.php");
		using var response = await RelayHttp.PostAsJsonAsync(api, new { protocol = 1, action = "claim", code, companion_hash = hash });
		if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"The isolated phone protocol client received HTTP {(int)response.StatusCode} while claiming.");
		using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		return (body.RootElement.GetProperty("id").GetString()!, token);
	}

	private static async Task<string?> ReadPhoneRouteAsync(string relay, (string Id, string Token) phone)
	{
		var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(phone.Token))).ToLowerInvariant();
		var api = new Uri(new Uri(relay.EndsWith('/') ? relay : relay + "/"), "api.php");
		using var response = await RelayHttp.PostAsJsonAsync(api, new { protocol = 1, action = "read", id = phone.Id, token_hash = hash });
		if (!response.IsSuccessStatusCode) return null;
		using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		var snapshot = body.RootElement.GetProperty("snapshot");
		return snapshot.ValueKind == JsonValueKind.Object && snapshot.TryGetProperty("route", out var route) ? route.GetString() : null;
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
