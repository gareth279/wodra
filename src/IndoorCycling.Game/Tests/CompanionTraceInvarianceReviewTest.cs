using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using IndoorCycling.Core.Models;
using IndoorCycling.Trainer.Abstractions;
using IndoorCycling.Trainer.Implementations;
using IndoorCycling.Trainer.Models;
using IndoorCycling.Trainer.Windows;

/// <summary>Compares the production ride and recording path with companion publishing off and under relay outcomes.</summary>
public partial class CompanionTraceInvarianceReviewTest : Node
{
	private const string RouteId = "dunedin-one-way-oval-gardens";
	private const int FrameCount = 360;
	private const double FrameSeconds = .05;

	public override async void _Ready()
	{
		try
		{
			DisplayServer.WindowSetSize(new Vector2I(1280, 720));
			var screen = GD.Load<PackedScene>("res://Scenes/RideScreen.tscn").Instantiate<Control>();
			AddChild(screen);
			await Settle(8);
			if (GameBootstrap.Settings.CompanionEnabled || !string.Equals(GameBootstrap.Settings.CompanionRelayBaseUrl,
				AppSettings.ProductionCompanionRelayBaseUrl, StringComparison.OrdinalIgnoreCase))
				throw new InvalidOperationException("The isolated profile must keep companion disabled while using the production relay default.");
			GameBootstrap.Settings.DebugModeEnabled = true;
			GameBootstrap.Settings.OnlineRidingEnabled = false;
			GameBootstrap.Settings.DummyTrainer.Mode = "Watts";
			GameBootstrap.Settings.DummyTrainer.TargetWatts = 220;
			GameBootstrap.Settings.DummyTrainer.CadenceRpm = 91;
			GameBootstrap.Settings.Recording.OutputFolder = Path.Combine(ProjectSettings.GlobalizePath("user://"), "companion-trace-fit");
			GameBootstrap.Settings.Save();
			GameBootstrap.ConfigureFakeTrainerMode();
			var profile = new RiderProfileRecord(Guid.NewGuid(), "Companion Trace Isolation Test",
				DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 74, 9, true, 50);
			await GameBootstrap.SaveProfileAsync(profile);
			await GameBootstrap.SetActiveProfileAsync(profile.Id);

			var session = screen.GetNode<RideSessionNode>("RideSessionNode");
			var world = screen.GetNode<RideWorldView>("RideWorldView");
			var client = screen.GetNode<CompanionRelayClient>("StartPanel/CompanionRelayClient");
			var route = RouteCatalog.GetById(RouteId) ?? throw new InvalidOperationException($"Missing production route {RouteId}.");

			await using var relay = new LocalRelayStub();
			var disabled = await RunTrace(screen, session, world, client, route, relay, "disabled", pair: false);
			var healthy = await RunTrace(screen, session, world, client, route, relay, "healthy", pair: true);
			AssertEquivalent("healthy relay", disabled, healthy);
			var rejected = await RunTrace(screen, session, world, client, route, relay, "rejected", pair: true);
			AssertEquivalent("relay error", disabled, rejected);
			var timedOut = await RunTrace(screen, session, world, client, route, relay, "timeout", pair: true);
			AssertEquivalent("relay timeout", disabled, timedOut);
			AssertCompanionDiagnostics();
			await AssertCompanionHeartRateSnapshot(session);

			if (relay.PublishSuccesses == 0 || relay.PublishFailures == 0 || relay.PublishTimeouts == 0)
				throw new InvalidOperationException($"Relay outcomes were not all exercised: success={relay.PublishSuccesses}, failure={relay.PublishFailures}, timeout={relay.PublishTimeouts}.");
			await session.ExitToRouteSelectionAsync();
			GD.Print($"[CompanionTraceInvarianceReview] PASS: identical {FrameCount}-sample production dummy traces produced identical authoritative ride state, trainer grade commands and {disabled.Recordings.Count} recording inputs with companion disabled, relay success, relay HTTP failure and 5s timeout. Relay outcomes: success={relay.PublishSuccesses}, failure={relay.PublishFailures}, timeout={relay.PublishTimeouts}; route={RouteId}.");
			GetTree().Quit(0);
		}
		catch (Exception error)
		{
			GD.PushError($"[CompanionTraceInvarianceReview] FAIL: {error}");
			GetTree().Quit(1);
		}
	}

	private static async Task AssertCompanionHeartRateSnapshot(RideSessionNode session)
	{
		var now = DateTimeOffset.UtcNow;
		var trainer = new FakeAntFeCTrainerClient(new TrainerTelemetry(now, 212, 88, 31,
			HasPowerMeasurement: true, HasCadenceMeasurement: true, HasReceivedMeasurement: true, HasSpeedMeasurement: true));
		var heartRate = new FakeHeartRateClient { Connected = true, Bpm = 146, MeasurementAt = now };
		GameBootstrap.ConfigureRealTrainerMode(trainer);
		GameBootstrap.HeartRateClient = heartRate;
		var route = RouteCatalog.GetById(RouteId) ?? throw new InvalidOperationException($"Missing production route {RouteId}.");
		if (!await session.StartTrainerSessionAsync(route)) throw new InvalidOperationException("Could not start the FE-C and separate BLE heart-rate snapshot regression ride.");
		session.SetProcess(false);
		var recorded = new List<TrainerTelemetry>();
		void OnRecording(RideState _, TrainerTelemetry telemetry, double __) => recorded.Add(telemetry);
		session.RecordingSampleObservedForReview += OnRecording;
		try
		{
			for (var i = 0; i < 30; i++)
			{
				heartRate.MeasurementAt = DateTimeOffset.UtcNow;
				trainer.Telemetry = trainer.Telemetry with { Timestamp = DateTimeOffset.UtcNow };
				session._Process(.05);
			}
			var rideTelemetry = session.LatestRideTelemetry ?? throw new InvalidOperationException("Ride did not expose processed telemetry.");
			if (rideTelemetry.HeartRateBpm != heartRate.Bpm)
				throw new InvalidOperationException("The ride telemetry was not enriched with the connected BLE heart-rate value.");
			var snapshot = JsonSerializer.Serialize(CompanionRelayClient.CreateSnapshot(session));
			using (var json = JsonDocument.Parse(snapshot))
			{
				var root = json.RootElement;
				foreach (var field in new[] { "state", "connected", "paused", "data_stale", "route", "speed_kph", "power_w", "cadence_rpm", "heart_rate_bpm", "distance_m", "elapsed_s", "gradient_pct", "environment", "route_type", "route_distance_m", "route_progress", "lap", "remaining_m", "riders", "captured_at" })
					if (!root.TryGetProperty(field, out _)) throw new InvalidOperationException($"Companion snapshot lost existing protocol field '{field}'.");
				if (root.GetProperty("heart_rate_bpm").GetInt32() != rideTelemetry.HeartRateBpm ||
					root.GetProperty("power_w").GetInt32() != 212 || root.GetProperty("cadence_rpm").GetInt32() != 88 ||
					root.GetProperty("speed_kph").ValueKind != JsonValueKind.Number || root.TryGetProperty("heartRateBpm", out _))
					throw new InvalidOperationException("Companion did not serialize the same fresh HR-enriched ride telemetry with compatible JSON names and unaffected trainer metrics.");
			}
			if (recorded.Count == 0 || recorded[^1].HeartRateBpm != rideTelemetry.HeartRateBpm)
				throw new InvalidOperationException("Companion heart rate differs from the FIT recording input telemetry.");

			heartRate.Connected = false;
			trainer.Telemetry = trainer.Telemetry with { Timestamp = DateTimeOffset.UtcNow };
			session._Process(.05);
			if (session.LatestRideTelemetry?.HeartRateBpm != heartRate.Bpm)
				throw new InvalidOperationException("The disconnected-monitor test did not retain the client's cached ride sample to verify companion filtering.");
			AssertHeartRateNullAndTrainerMetrics(session, "disconnected BLE monitor");

			heartRate.Connected = true;
			heartRate.Bpm = 153;
			heartRate.MeasurementAt = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(6);
			trainer.Telemetry = trainer.Telemetry with { Timestamp = DateTimeOffset.UtcNow };
			session._Process(.05);
			AssertHeartRateNullAndTrainerMetrics(session, "stale BLE measurement");

			heartRate.Bpm = 159;
			heartRate.MeasurementAt = DateTimeOffset.UtcNow;
			trainer.Telemetry = trainer.Telemetry with { Timestamp = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(6) };
			session._Process(.05);
			using var staleTrainerJson = JsonDocument.Parse(JsonSerializer.Serialize(CompanionRelayClient.CreateSnapshot(session)));
			var staleTrainer = staleTrainerJson.RootElement;
			if (!staleTrainer.GetProperty("data_stale").GetBoolean() || staleTrainer.GetProperty("heart_rate_bpm").GetInt32() != 159 ||
				staleTrainer.GetProperty("power_w").ValueKind != JsonValueKind.Null ||
				staleTrainer.GetProperty("cadence_rpm").ValueKind != JsonValueKind.Null ||
				staleTrainer.GetProperty("speed_kph").ValueKind != JsonValueKind.Null)
				throw new InvalidOperationException("Fresh BLE heart rate was suppressed by stale trainer measurements or stale trainer values leaked into the snapshot.");

			GameBootstrap.HeartRateClient = null;
			var trainerHeartRateAt = DateTimeOffset.UtcNow;
			trainer.Telemetry = new TrainerTelemetry(trainerHeartRateAt, 212, 88, 31, HeartRateBpm: 132,
				HasPowerMeasurement: true, HasCadenceMeasurement: true, HasReceivedMeasurement: true,
				HasSpeedMeasurement: true, HeartRateTimestamp: trainerHeartRateAt);
			session._Process(.05);
			using var trainerHeartRateJson = JsonDocument.Parse(JsonSerializer.Serialize(CompanionRelayClient.CreateSnapshot(session)));
			if (trainerHeartRateJson.RootElement.GetProperty("heart_rate_bpm").GetInt32() != 132)
				throw new InvalidOperationException("Fresh trainer-reported heart rate was lost when no separate BLE monitor is configured.");
		}
		finally
		{
			session.RecordingSampleObservedForReview -= OnRecording;
			GameBootstrap.HeartRateClient = null;
			await session.ExitToRouteSelectionAsync();
		}
		GD.Print("[CompanionTraceInvarianceReview] PASS: connected FE-C trainer plus independent BLE heart rate is published from ride/FIT telemetry; stale/disconnected HR clears; trainer metrics and protocol-2 JSON fields remain independent.");
	}

	private static void AssertHeartRateNullAndTrainerMetrics(RideSessionNode session, string scenario)
	{
		using var json = JsonDocument.Parse(JsonSerializer.Serialize(CompanionRelayClient.CreateSnapshot(session)));
		var root = json.RootElement;
		if (root.GetProperty("heart_rate_bpm").ValueKind != JsonValueKind.Null ||
			root.GetProperty("power_w").GetInt32() != 212 || root.GetProperty("cadence_rpm").GetInt32() != 88 ||
			root.GetProperty("speed_kph").ValueKind != JsonValueKind.Number)
			throw new InvalidOperationException($"{scenario} should clear only heart rate while retaining fresh trainer metrics.");
	}

	private async Task<TraceResult> RunTrace(Control screen, RideSessionNode session, RideWorldView world,
		CompanionRelayClient client, RouteDefinition route, LocalRelayStub relay, string mode, bool pair)
	{
		client.Configure(session, false, "");
		if (pair)
		{
			// This review drives invitation polling directly. Stop the production frame
			// poller during setup so it cannot race the explicit pending/approve steps.
			client.SetProcess(false);
			relay.Mode = "healthy";
			client.Configure(session, true, relay.BaseUrl);
			if (!await client.CreateInvitationAsync()) throw new InvalidOperationException($"Could not create the local fake pairing for {mode}: {client.StatusText}");
			var poll = typeof(CompanionRelayClient).GetMethod("PollInvitationAsync", BindingFlags.Instance | BindingFlags.NonPublic)
				?? throw new MissingMethodException(nameof(CompanionRelayClient), "PollInvitationAsync");
			await (Task)(poll.Invoke(client, null) ?? throw new InvalidOperationException("Could not start companion invitation status poll."));
			if (!client.IsPaired) throw new InvalidOperationException($"The fake relay did not complete the explicitly requested remembered-device pairing: {client.StatusText}");
			relay.Mode = mode;
			client.SetProcess(true);
		}
		else client.Configure(session, false, "");

		if (!await session.StartTrainerSessionAsync(route)) throw new InvalidOperationException($"Could not start the production ride for {mode}.");
		screen.GetNode<HudController>("HudController").Show();
		screen.GetNode<StartPanelController>("StartPanel").Hide();
		session.SetProcess(false);
		world.SetProcess(true);
		await Settle(8);
		world.SetProcess(false);
		if (session.CurrentRideState.DistanceMeters != 0) throw new InvalidOperationException("A paired trace did not begin at zero distance.");

		var frames = new List<FrameSample>(FrameCount);
		var recordings = new List<RecordingSample>();
		void OnRecording(RideState state, TrainerTelemetry telemetry, double gradient) => recordings.Add(new RecordingSample(state, telemetry, gradient));
		session.RecordingSampleObservedForReview += OnRecording;
		try
		{
			var trainer = GameBootstrap.TrainerClient as FakeTrainerClient
				?? throw new InvalidOperationException("Production ride is not using the dummy trainer.");
			for (var index = 0; index < FrameCount; index++)
			{
				var delta = index % 24 == 23 ? .7 : FrameSeconds;
				var watts = index % 32 < 20 ? 220 : 175;
				var cadence = 88 + index % 7;
				trainer.SetDummyOutput(watts, cadence);
				var gradient = route.GetSampleAt(session.CurrentRideState.DistanceMeters).GradientPercent;
				session._Process(delta);
				frames.Add(new FrameSample(session.CurrentRideState, gradient, trainer.LastRequestedSimulationGradePercent));
				if (pair && index == 16)
				{
					client._Process(3.1);
					await Settle(2);
				}
				world._Process(delta);
				await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
				if (mode == "timeout") await Task.Delay(16);
			}
		}
		finally { session.RecordingSampleObservedForReview -= OnRecording; }
		if (frames.All(sample => sample.State.DistanceMeters <= 1) || recordings.Count < 3)
			throw new InvalidOperationException($"The {mode} production ride did not advance and record dummy input.");
		if (mode == "rejected" && !client.StatusText.Contains("server is unavailable", StringComparison.OrdinalIgnoreCase))
			throw new InvalidOperationException($"Expected relay HTTP failure to remain a non-blocking client error, got '{client.StatusText}'.");
		if (mode == "timeout" && !client.StatusText.Contains("timed out", StringComparison.OrdinalIgnoreCase))
			throw new InvalidOperationException($"Expected bounded relay timeout to be reported during the ride, got '{client.StatusText}'.");
		if (mode == "rejected")
		{
			relay.Mode = "healthy";
			var priorSuccesses = relay.PublishSuccesses;
			client._Process(3.1);
			for (var attempt = 0; attempt < 300
				&& (relay.PublishSuccesses <= priorSuccesses
					|| !client.StatusText.Contains("live ride data is updating", StringComparison.OrdinalIgnoreCase)); attempt++)
			{
				await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
				await Task.Delay(25);
			}
			if (!client.StatusText.Contains("live ride data is updating", StringComparison.OrdinalIgnoreCase))
				throw new InvalidOperationException($"A recovered relay did not restore the companion state: {client.StatusText}.");
		}
		await session.ExitToRouteSelectionAsync();
		world.SetProcess(true); await Settle(3); world.SetProcess(false);
		client.Configure(session, false, "");
		return new TraceResult(frames, recordings);
	}

	private static void AssertEquivalent(string scenario, TraceResult baseline, TraceResult observed)
	{
		if (baseline.Frames.Count != observed.Frames.Count || baseline.Recordings.Count != observed.Recordings.Count)
			throw new InvalidOperationException($"Companion {scenario} changed ride/recording sample counts: base={baseline.Frames.Count}/{baseline.Recordings.Count}, observed={observed.Frames.Count}/{observed.Recordings.Count}.");
		for (var i = 0; i < baseline.Frames.Count; i++)
			if (baseline.Frames[i] != observed.Frames[i])
				throw new InvalidOperationException($"Companion {scenario} changed authoritative ride state or trainer command at frame {i}: base={baseline.Frames[i]}, observed={observed.Frames[i]}.");
		for (var i = 0; i < baseline.Recordings.Count; i++)
		{
			var a = baseline.Recordings[i]; var b = observed.Recordings[i];
			if (a.State != b.State || a.Gradient != b.Gradient || a.Telemetry.PowerWatts != b.Telemetry.PowerWatts ||
				a.Telemetry.CadenceRpm != b.Telemetry.CadenceRpm || a.Telemetry.HeartRateBpm != b.Telemetry.HeartRateBpm ||
				a.Telemetry.SpeedKph != b.Telemetry.SpeedKph || a.Telemetry.HasPowerMeasurement != b.Telemetry.HasPowerMeasurement ||
				a.Telemetry.HasCadenceMeasurement != b.Telemetry.HasCadenceMeasurement || a.Telemetry.HasReceivedMeasurement != b.Telemetry.HasReceivedMeasurement ||
				a.Telemetry.HasSpeedMeasurement != b.Telemetry.HasSpeedMeasurement)
				throw new InvalidOperationException($"Companion {scenario} changed recording input sample {i}: base={a}, observed={b}.");
		}
	}

	private static void AssertCompanionDiagnostics()
	{
		if (string.IsNullOrWhiteSpace(GameLogger.LogPath) || !File.Exists(GameLogger.LogPath))
			throw new InvalidOperationException("Companion event logging did not create a diagnostic session log.");
		var entries = File.ReadLines(GameLogger.LogPath).Select(line => JsonDocument.Parse(line)).ToArray();
		try
		{
			var companion = entries.Select(document => document.RootElement)
				.Where(entry => entry.TryGetProperty("type", out var type) && type.GetString()?.StartsWith("companion_", StringComparison.Ordinal) == true)
				.ToArray();
			var types = companion.Select(entry => entry.GetProperty("type").GetString()).ToHashSet(StringComparer.Ordinal);
			var required = new[] { "companion_configuration_changed", "companion_invitation_created", "companion_pairing_result",
				"companion_trusted_devices_discovered", "companion_publishing_started", "companion_http_response",
				"companion_publishing_succeeded", "companion_publishing_failed", "companion_connection_restored" };
			var missing = required.Where(type => !types.Contains(type)).ToArray();
			if (missing.Length > 0) throw new InvalidOperationException($"Companion diagnostics omitted required safe lifecycle events: {string.Join(", ", missing)}.");
			foreach (var entry in companion)
				if (entry.TryGetProperty("payload", out var payload) && payload.ValueKind == JsonValueKind.Object
				&& payload.EnumerateObject().Any(property => property.Name != "errorCode"
					&& (property.Name.Contains("code", StringComparison.OrdinalIgnoreCase)
						|| property.Name.Contains("token", StringComparison.OrdinalIgnoreCase)
						|| property.Name.Contains("secret", StringComparison.OrdinalIgnoreCase)
						|| property.Name.Contains("hash", StringComparison.OrdinalIgnoreCase)
						|| property.Name.Contains("deviceId", StringComparison.OrdinalIgnoreCase)
						|| property.Name.Contains("inviteId", StringComparison.OrdinalIgnoreCase)
						|| property.Name.Contains("route", StringComparison.OrdinalIgnoreCase))))
					throw new InvalidOperationException("A companion diagnostic event contained a forbidden credential, identifier, or ride field.");
		}
		finally { foreach (var entry in entries) entry.Dispose(); }
	}

	private sealed class FakeAntFeCTrainerClient(TrainerTelemetry telemetry) : ITrainerClient
	{
		public TrainerTelemetry Telemetry { get; set; } = telemetry;
		public bool IsConnected => true;
		public bool IsReady => true;
		public Task ConnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
		public Task DisconnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
		public Task SetSimulationAsync(SimulationCommand command, CancellationToken cancellationToken = default) => Task.CompletedTask;
		public Task SetResistanceAsync(ResistanceCommand command, CancellationToken cancellationToken = default) => Task.CompletedTask;
		public TrainerTelemetry GetLatestTelemetry() => Telemetry;
	}

	private sealed class FakeHeartRateClient : WindowsBleHeartRateClient
	{
		public bool Connected { get; set; }
		public int? Bpm { get; set; }
		public DateTimeOffset MeasurementAt { get; set; }
		public override bool IsConnected => Connected;
		public override DateTimeOffset LastMeasurementAt => MeasurementAt;
		public override int? GetFreshBpm(DateTimeOffset now, TimeSpan? maxAge = null)
		{
			var age = maxAge ?? TimeSpan.FromSeconds(8);
			return Bpm.HasValue && MeasurementAt <= now && now - MeasurementAt <= age ? Bpm : null;
		}
	}

	private async Task Settle(int frames) { for (var i = 0; i < frames; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); }
	private sealed record TraceResult(IReadOnlyList<FrameSample> Frames, IReadOnlyList<RecordingSample> Recordings);
	private readonly record struct FrameSample(RideState State, double Gradient, double? TrainerCommand);
	private readonly record struct RecordingSample(RideState State, TrainerTelemetry Telemetry, double Gradient);

	private sealed class LocalRelayStub : IAsyncDisposable
	{
		private readonly HttpListener _listener = new();
		private readonly CancellationTokenSource _stop = new();
		private readonly Task _loop;
		private string _mode = "healthy";
		private int _publishSuccesses;
		private int _publishFailures;
		private int _publishTimeouts;
		public string Mode { get => Volatile.Read(ref _mode); set => Volatile.Write(ref _mode, value); }
		public int PublishSuccesses => Volatile.Read(ref _publishSuccesses);
		public int PublishFailures => Volatile.Read(ref _publishFailures);
		public int PublishTimeouts => Volatile.Read(ref _publishTimeouts);
		public string BaseUrl { get; }

		public LocalRelayStub()
		{
			var socket = new TcpListener(IPAddress.Loopback, 0); socket.Start(); var port = ((IPEndPoint)socket.LocalEndpoint).Port; socket.Stop();
			BaseUrl = $"http://127.0.0.1:{port}/"; _listener.Prefixes.Add(BaseUrl); _listener.Start(); _loop = RunAsync();
		}

		private async Task RunAsync()
		{
			while (!_stop.IsCancellationRequested)
			{
				HttpListenerContext context;
				try { context = await _listener.GetContextAsync().WaitAsync(_stop.Token); }
				catch (OperationCanceledException) { return; }
				catch (HttpListenerException) when (_stop.IsCancellationRequested) { return; }
				_ = RespondAsync(context);
			}
		}

		private async Task RespondAsync(HttpListenerContext context)
		{
			try
			{
				using var reader = new System.IO.StreamReader(context.Request.InputStream, context.Request.ContentEncoding);
				using var json = JsonDocument.Parse(await reader.ReadToEndAsync());
				var action = json.RootElement.GetProperty("action").GetString();
				string result = "{\"protocol\":2,\"status\":\"accepted\"}"; int status = 200;
				if (action == "remembered_create") result = "{\"protocol\":2,\"id\":\"review-pair-id-0000000000000000000000000000\",\"code\":\"TRACE12345\"}";
				else if (action == "remembered_invite_status") result = "{\"protocol\":2,\"status\":\"claimed\"}";
				else if (action == "remembered_devices") result = "{\"protocol\":2,\"devices\":[{\"id\":\"review-device-id-0000000000000000000000000000\",\"label\":\"Phone 1\",\"created_at\":1,\"last_seen\":1,\"expires_at\":4102444800}]}";
				else if (action == "remembered_publish")
				{
					if (Mode == "timeout") { await Task.Delay(TimeSpan.FromSeconds(5.2), _stop.Token); Interlocked.Increment(ref _publishTimeouts); }
					else if (Mode == "rejected") { status = 503; result = "{\"protocol\":2,\"error\":\"relay_unavailable\"}"; Interlocked.Increment(ref _publishFailures); }
					else Interlocked.Increment(ref _publishSuccesses);
				}
				var bytes = Encoding.UTF8.GetBytes(result); context.Response.StatusCode = status; context.Response.ContentType = "application/json"; context.Response.ContentLength64 = bytes.Length;
				await context.Response.OutputStream.WriteAsync(bytes); context.Response.Close();
			}
			catch (Exception error) when (error is OperationCanceledException or HttpListenerException or IOException or ObjectDisposedException) { try { context.Response.Abort(); } catch { } }
			catch { try { context.Response.Abort(); } catch { } }
		}

		public async ValueTask DisposeAsync()
		{
			_stop.Cancel(); _listener.Close(); try { await _loop; } catch { } _stop.Dispose();
		}
	}
}
