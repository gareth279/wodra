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
	private static readonly System.Net.Http.HttpClient RelayHttp = new(new HttpClientHandler { UseCookies = true,
		CookieContainer = new System.Net.CookieContainer() }) { Timeout = TimeSpan.FromSeconds(10) };

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
			if ((GameBootstrap.Settings.CompanionEnabled || !string.Equals(GameBootstrap.Settings.CompanionRelayBaseUrl, AppSettings.ProductionCompanionRelayBaseUrl, StringComparison.OrdinalIgnoreCase)) &&
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
			var companionButton = _screen.FindChildren("CompanionConnectButton", "Button", recursive: true, owned: false).OfType<Button>().Single();
			companionButton.EmitSignal(Button.SignalName.Pressed);
			await Frames(3);
			var options = _screen.FindChildren("OptionsWindow", "Window", recursive: true, owned: false).OfType<Window>().FirstOrDefault()
				?? throw new InvalidOperationException("In-ride phone action did not open the production Options window.");
			var done = options.FindChildren("DoneButton", "Button", recursive: true, owned: false).OfType<Button>().Single();
			done.EmitSignal(Button.SignalName.Pressed);
			await Frames(2);
			if (options.Visible || start.Visible) throw new InvalidOperationException("Closing in-ride Options must return to the ride without showing the startup panel.");
			companionButton.EmitSignal(Button.SignalName.Pressed);
			await Frames(3);
			if (!options.Visible) throw new InvalidOperationException("In-ride phone action could not reopen Options after closing it.");
			var advancedRelay = _screen.FindChildren("CompanionAdvancedOverride", "Button", recursive: true, owned: false).OfType<Button>().Single();
			advancedRelay.EmitSignal(Button.SignalName.Pressed);
			var relayOverride = _screen.FindChildren("CompanionRelayUrl", "LineEdit", recursive: true, owned: false).OfType<LineEdit>().Single();
			relayOverride.Text = _relay;
			var createPairing = _screen.FindChildren("StartCompanionPairing", "Button", recursive: true, owned: false).OfType<Button>().Single();
			createPairing.EmitSignal(Button.SignalName.Pressed);
			for (var wait = 0; wait < 300 && !_client.HasInvitation; wait++) await Frames(1);
			if (!_client.HasInvitation) throw new InvalidOperationException("Production Options could not create a pairing invitation through its actual button.");
			await Frames(3);
			CaptureWindow(options, "pairing-desktop.png");
			GD.Print("[CompanionProductionReview] Pairing code is visible in the captured Options window; it is intentionally omitted from logs.");
			if (System.Environment.GetEnvironmentVariable("WODRA_COMPANION_REVIEW_AUTO_PAIR") == "1")
				_phonePair = await ClaimPhoneAsync(_relay, _client.ManualCode);
			GD.Print("[CompanionProductionReview] Waiting for the actual browser or isolated phone protocol client to request pairing.");

			for (var frame = 0; frame < 18000 && !_client.IsPaired; frame++)
			{
				await Frames(1);
			}
			if (!_client.IsPaired) throw new TimeoutException("No companion device completed pairing within five minutes.");
			GD.Print("[CompanionProductionReview] PHONE_REMEMBERED");
			options.Hide();
			await RunRideAsync("dunedin-one-way-oval-gardens", "road-live.png");
			await RunRideAsync("wodra-velodrome-250", "velodrome-live.png");
			await _session.ExitToRouteSelectionAsync();
			if (_phonePair is { } rememberedPhone)
			{
				(bool Authorized, string? Route) state = (false, null);
				for (var attempt = 0; attempt < 10; attempt++)
				{
					await Frames(30);
					state = await ReadPhoneStateAsync(_relay, rememberedPhone);
					if (!state.Authorized || state.Route is null) break;
				}
				if (!state.Authorized || state.Route is not null)
					throw new InvalidOperationException("Trusted phone should remain authorised but receive no live route snapshot while Wodra is idle.");
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
				observedRoute = (await ReadPhoneStateAsync(_relay!, phone)).Route;
			}
			if (observedRoute != route.Name)
				throw new InvalidOperationException($"The production phone session did not receive the {routeId} snapshot.");
			GD.Print($"[CompanionProductionReview] PHONE_READ route={observedRoute}");
		}
		await _session.ExitToRouteSelectionAsync();
	}

	private static async Task<(string Id, string Token)> ClaimPhoneAsync(string relay, string code)
	{
		if (code.Length != 10 || code.Any(character => !Uri.IsHexDigit(character)))
			throw new InvalidOperationException($"Production pairing UI did not provide a valid invitation (length={code.Length}, hexadecimal={code.All(Uri.IsHexDigit)}).");
		var api = new Uri(new Uri(relay.EndsWith('/') ? relay : relay + "/"), "api.php");
		using var request = new HttpRequestMessage(HttpMethod.Post, api);
		request.Headers.Add("Origin", new Uri(relay).GetLeftPart(UriPartial.Authority));
		request.Content = System.Net.Http.Json.JsonContent.Create(new { protocol = 2, action = "remembered_claim", code });
		using var response = await RelayHttp.SendAsync(request);
		if (!response.IsSuccessStatusCode)
		{
			using var failure = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
			var reason = failure.RootElement.TryGetProperty("error", out var error) ? error.GetString() : "unknown";
			throw new InvalidOperationException($"The isolated phone protocol client received HTTP {(int)response.StatusCode} ({reason}) while claiming.");
		}
		using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		return (body.RootElement.GetProperty("device_id").GetString()!, "");
	}

	private static async Task<(bool Authorized, string? Route)> ReadPhoneStateAsync(string relay, (string Id, string Token) phone)
	{
		var api = new Uri(new Uri(relay.EndsWith('/') ? relay : relay + "/"), "api.php");
		using var request = new HttpRequestMessage(HttpMethod.Post, api);
		request.Headers.Add("Origin", new Uri(relay).GetLeftPart(UriPartial.Authority));
		request.Content = System.Net.Http.Json.JsonContent.Create(new { protocol = 2, action = "remembered_read" });
		using var response = await RelayHttp.SendAsync(request);
		if (!response.IsSuccessStatusCode) return (false, null);
		using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		var snapshot = body.RootElement.GetProperty("snapshot");
		var routeName = snapshot.ValueKind == JsonValueKind.Object && snapshot.TryGetProperty("route", out var route)
			? route.GetString() : null;
		return (true, routeName);
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
