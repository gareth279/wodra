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
using IndoorCycling.Trainer.Implementations;

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
		if (mode == "rejected" && !client.StatusText.StartsWith("Relay unavailable", StringComparison.Ordinal))
			throw new InvalidOperationException($"Expected relay HTTP failure to remain a non-blocking client error, got '{client.StatusText}'.");
		if (mode == "timeout" && !client.StatusText.StartsWith("Relay request timed out", StringComparison.Ordinal))
			throw new InvalidOperationException($"Expected bounded relay timeout to be reported during the ride, got '{client.StatusText}'.");
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
				string result = "{}"; int status = 200;
				if (action == "remembered_create") result = "{\"id\":\"review-pair-id-0000000000000000000000000000\",\"code\":\"TRACE12345\"}";
				else if (action == "remembered_invite_status") result = "{\"status\":\"claimed\"}";
				else if (action == "remembered_devices") result = "{\"devices\":[{\"id\":\"review-device-id-0000000000000000000000000000\",\"label\":\"Phone 1\",\"created_at\":1,\"last_seen\":1,\"expires_at\":4102444800}]}";
				else if (action == "remembered_publish")
				{
					if (Mode == "timeout") { await Task.Delay(TimeSpan.FromSeconds(5.2), _stop.Token); Interlocked.Increment(ref _publishTimeouts); }
					else if (Mode == "rejected") { status = 503; result = "{\"error\":\"relay_unavailable\"}"; Interlocked.Increment(ref _publishFailures); }
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
