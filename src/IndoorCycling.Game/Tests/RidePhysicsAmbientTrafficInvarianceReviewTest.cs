using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using IndoorCycling.Core.Models;
using IndoorCycling.Trainer.Implementations;

/// <summary>Runs paired deterministic traces through the production RideScreen/session path.</summary>
public partial class RidePhysicsAmbientTrafficInvarianceReviewTest : Node
{
	private const string RouteId = "dunedin-one-way-oval-gardens";
	private const int FrameCount = 144;
	private const double NormalFrameSeconds = .05;
	private const double LowFrameRateSeconds = .65;
	private const double NarrowRoadMeters = 4.6;

	public override async void _Ready()
	{
		try
		{
			DisplayServer.WindowSetSize(new Vector2I(1280, 720));
			var screen = GD.Load<PackedScene>("res://Scenes/RideScreen.tscn").Instantiate();
			AddChild(screen);
			await Settle(6);
			GameBootstrap.Settings.DebugModeEnabled = true;
			GameBootstrap.Settings.OnlineRidingEnabled = false;
			GameBootstrap.Settings.DummyTrainer.Mode = "Watts";
			GameBootstrap.Settings.DummyTrainer.TargetWatts = 1200;
			GameBootstrap.Settings.DummyTrainer.CadenceRpm = 92;
			GameBootstrap.Settings.Recording.OutputFolder = Path.Combine(ProjectSettings.GlobalizePath("user://"), "invariance-fit");
			GameBootstrap.ConfigureFakeTrainerMode();

			var session = screen.GetNode<RideSessionNode>("RideSessionNode");
			var world = screen.GetNode<RideWorldView>("RideWorldView");
			var route = RouteCatalog.GetById(RouteId) ?? throw new InvalidOperationException($"Missing production route {RouteId}.");
			var withTraffic = await RunTrace(screen, session, world, route, includeCar: true);
			await session.ExitToRouteSelectionAsync();
			world.SetProcess(true);
			await Settle(2); // Let the production world reset its route-load/steering state.
			world.SetProcess(false);
			var withoutTraffic = await RunTrace(screen, session, world, route, includeCar: false);

			AssertEquivalent(withTraffic, withoutTraffic);
			var routeGeometry = world.RouteGeometry ?? throw new InvalidOperationException("Production route geometry disappeared during the paired trace.");
			double accumulatedTurnRadians = 0;
			var previousForward = routeGeometry.GetPoseAtDistance(withTraffic.Frames[0].State.DistanceMeters).Forward;
			for (var index = 1; index < withTraffic.Frames.Count; index++)
			{
				var nextForward = routeGeometry.GetPoseAtDistance(withTraffic.Frames[index].State.DistanceMeters).Forward;
				accumulatedTurnRadians += Math.Acos(Math.Clamp(System.Numerics.Vector3.Dot(
					System.Numerics.Vector3.Normalize(previousForward), System.Numerics.Vector3.Normalize(nextForward)), -1, 1));
				previousForward = nextForward;
			}
			if (accumulatedTurnRadians < 0.08)
				throw new InvalidOperationException($"The deterministic ride trace did not traverse a meaningful route bend ({accumulatedTurnRadians:F3} rad).");
			if (withTraffic.RecordedSamples.Count < 3 || withoutTraffic.RecordedSamples.Count != withTraffic.RecordedSamples.Count)
				throw new InvalidOperationException($"Expected paired production FIT samples; traffic={withTraffic.RecordedSamples.Count}, none={withoutTraffic.RecordedSamples.Count}.");
			for (var index = 0; index < withTraffic.RecordedSamples.Count; index++)
			{
				var a = withTraffic.RecordedSamples[index];
				var b = withoutTraffic.RecordedSamples[index];
				if (a.State != b.State || a.Gradient != b.Gradient || a.Telemetry.PowerWatts != b.Telemetry.PowerWatts
					|| a.Telemetry.CadenceRpm != b.Telemetry.CadenceRpm || a.Telemetry.HeartRateBpm != b.Telemetry.HeartRateBpm)
					throw new InvalidOperationException($"Production FIT sample {index} changed with ambient traffic: traffic={a}, none={b}.");
			}

			await session.ExitToRouteSelectionAsync();
			GD.Print($"[RidePhysicsAmbientTrafficInvarianceReview] PASS: actual RideScreen/RideSession path; traffic car started 18 m ahead at 2 m/s on a {NarrowRoadMeters:F1} m fixture road; {withTraffic.Frames.Count} identical production ticks traversed {accumulatedTurnRadians:F3} rad of route bends with 1,200 W catch-up, steering inputs and {LowFrameRateSeconds:F2} s frame hitches; RideState, HUD-authoritative distance, lateral steering, power/cadence, trainer grade commands and {withTraffic.RecordedSamples.Count} FIT inputs matched traffic-off exactly.");
			GetTree().Quit(0);
		}
		catch (Exception error)
		{
			GD.PushError($"[RidePhysicsAmbientTrafficInvarianceReview] FAIL: {error}");
			GetTree().Quit(1);
		}
	}

	private async Task<TraceResult> RunTrace(Node screen, RideSessionNode session, RideWorldView world,
		RouteDefinition route, bool includeCar)
	{
		if (!await session.StartTrainerSessionAsync(route))
			throw new InvalidOperationException("Could not start the production RideScreen trainer session.");
		screen.GetNode<HudController>("HudController").Show();
		screen.GetNode<StartPanelController>("StartPanel").Hide();
		session.SetProcess(false);
		world.SetProcess(true);
		await Settle(8);
		world.SetProcess(false);
		if (world.RouteGeometry is null) throw new InvalidOperationException("The production route geometry did not load.");
			if (session.CurrentRideState.DistanceMeters != 0)
			throw new InvalidOperationException("The paired production rides did not start at the same route distance.");

		world.ConfigureAmbientTrafficInvariantReviewFixture(includeCar,
			carAheadMeters: 18, carSpeedMetersPerSecond: 2, roadWidthMeters: NarrowRoadMeters);
		if (includeCar)
		{
			var cars = world.GetAmbientTrafficCollisionReviewSamples();
			if (cars.Count != 1)
				throw new InvalidOperationException("The controlled slow car was not installed in the production RideScreen.");
			var player = world.GetRiderCollisionReviewSamples().Single(sample => sample.RiderId == "player");
			var ahead = cars[0].Position - new System.Numerics.Vector3(player.WorldPosition.X, player.WorldPosition.Y, player.WorldPosition.Z);
			var forward = new System.Numerics.Vector3(player.WorldForward.X, player.WorldForward.Y, player.WorldForward.Z);
			if (System.Numerics.Vector3.Dot(ahead, forward) <= 0)
				throw new InvalidOperationException("The controlled car did not start ahead of the production rider.");
		}

		var frames = new List<FrameSample>(FrameCount);
		var recordingSamples = new List<RecordingSample>();
		void OnRecordingSample(RideState state, TrainerTelemetry telemetry, double gradient) =>
			recordingSamples.Add(new RecordingSample(state, telemetry, gradient));
		session.RecordingSampleObservedForReview += OnRecordingSample;
		try
		{
			var trainer = GameBootstrap.TrainerClient as FakeTrainerClient
				?? throw new InvalidOperationException("Production ride did not use the controlled fake trainer.");
			for (var frame = 0; frame < FrameCount; frame++)
			{
				var delta = frame % 24 == 23 ? LowFrameRateSeconds : frame % 12 == 11 ? .25 : NormalFrameSeconds;
				var watts = frame % 48 < 32 ? 1200 : 980;
				var cadence = 88 + frame % 5;
				trainer.SetDummyOutput(watts, cadence);
				var left = frame % 72 is >= 10 and < 26;
				var right = frame % 72 is >= 42 and < 58;
				world.SetDeterministicSteeringInputForReview(left, right);
				var stateBeforeTick = session.CurrentRideState;
				var gradient = route.GetSampleAt(stateBeforeTick.DistanceMeters).GradientPercent;
				session._Process(delta);
				world._Process(delta);
				frames.Add(new FrameSample(session.CurrentRideState, world.CurrentLateralOffsetMeters,
					gradient, trainer.LastRequestedSimulationGradePercent, watts, cadence));
				await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
			}
		}
		finally { session.RecordingSampleObservedForReview -= OnRecordingSample; }

		if (!frames.Any(sample => sample.State.DistanceMeters > 20))
			throw new InvalidOperationException("The production RideSession did not advance during the controlled power trace.");
		if (includeCar && world.GetAmbientTrafficCollisionReviewSamples().Count == 0)
			throw new InvalidOperationException("The controlled car did not remain present during the player catch-up trace.");
		return new TraceResult(frames, recordingSamples);
	}

	private static void AssertEquivalent(TraceResult traffic, TraceResult none)
	{
		if (traffic.Frames.Count != none.Frames.Count) throw new InvalidOperationException("Paired production RideScreen traces have different frame counts.");
		for (var index = 0; index < traffic.Frames.Count; index++)
		{
			var a = traffic.Frames[index];
			var b = none.Frames[index];
			if (a.State != b.State || a.LateralOffset != b.LateralOffset || a.Gradient != b.Gradient
				|| a.TrainerGradeCommand != b.TrainerGradeCommand || a.InputWatts != b.InputWatts || a.InputCadence != b.InputCadence)
				throw new InvalidOperationException($"Ambient scenery changed authoritative RideScreen output on frame {index}: traffic={a}, none={b}.");
		}
	}

	private async Task Settle(int frames)
	{
		for (var frame = 0; frame < frames; frame++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
	}

	private sealed record TraceResult(IReadOnlyList<FrameSample> Frames, IReadOnlyList<RecordingSample> RecordedSamples);
	private readonly record struct FrameSample(RideState State, double LateralOffset, double Gradient,
		double? TrainerGradeCommand, int InputWatts, int InputCadence);
	private readonly record struct RecordingSample(RideState State,
		TrainerTelemetry Telemetry, double Gradient);
}
