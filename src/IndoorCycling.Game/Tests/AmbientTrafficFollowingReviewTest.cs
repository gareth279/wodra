using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using IndoorCycling.Core.Models;
using IndoorCycling.Core.Simulation;
using NVector3 = System.Numerics.Vector3;

/// <summary>Exercises production traffic control around cyclists on narrow and curved road paths.</summary>
public partial class AmbientTrafficFollowingReviewTest : Node
{
	private const double DeltaSeconds = .05;
	private const float CyclistLaneOffsetMeters = 1.75f;
	private static readonly NVector3[] Straight = [new(0, 0, 0), new(1000, 0, 0)];
	private static readonly NVector3[] Bend = [new(0, 0, 0), new(40, 0, 0), new(40, 0, -2000)];

	public override void _Ready()
	{
		try
		{
			var following = RunVehicleScenario(roadWidthMeters: 5.8, Straight, addGroup: true);
			if (following.Passed || following.MinimumClearance < -.02f
				|| Math.Abs(following.FinalSpeed - 5) > .2f || following.FinalGap is < 16 or > 24)
				throw new InvalidOperationException($"Narrow road did not keep a fast car safely behind the group: {following}.");

			var overtake = RunVehicleScenario(roadWidthMeters: 7.5, Bend, addGroup: false);
			if (!overtake.Passed || overtake.MinimumClearance < -.02f || Math.Abs(overtake.FinalLaneOffset - 1.7f) > .05f)
				throw new InvalidOperationException($"Wide curved road did not complete a clear overtake: {overtake}.");

			VerifyOccupiedPassLaneBlocksOvertake();
			VerifyFollowingHeadwayControl();
			VerifyPredictionFollowsBend();
			VerifyLowFrameRateFollowing();
			VerifySameLaneCarsDoNotUseJunctionPriority();
			VerifyCarFollowsPedestrianObstacle();
			VerifyCrossingCarsDoNotDeadlock();
			VerifyPlayerPhysicsIsInvariantToAmbientTraffic();
			VerifyFasterPlayerCausesSafeCarYield();
			VerifyCarYieldAvoidsOccupiedOpposingLane();
			GD.Print($"[AmbientTrafficFollowingReview] PASS: narrow road follows group (clearance={following.MinimumClearance:F3}m, final gap={following.FinalGap:F2}m, speed={following.FinalSpeed:F2}m/s); curved 7.5m road safely overtakes (clearance={overtake.MinimumClearance:F3}m); 12s obstacle prediction follows the bend.");
			GetTree().Quit(0);
		}
		catch (Exception ex)
		{
			GD.PushError($"[AmbientTrafficFollowingReview] FAIL: {ex}");
			GetTree().Quit(1);
		}
	}

	private void VerifyFollowingHeadwayControl()
	{
		var path = new AmbientActorPath(AmbientActorKind.Car, 705, 1, Straight,
			new AmbientSpeedRange(12, 12), roadWidthMeters: 5.8, baseLaneOffsetMeters: 1.7f);
		var car = path.SampleLane(100, path.BaseLaneOffsetMeters);
		foreach (var riderSpeed in new[] { 1.5f, 5f, 11f })
		{
			var desiredClearance = riderSpeed * AmbientTrafficFollowingMath.FollowingHeadwaySeconds;
			var riderDistance = 100 + AmbientActorEnvelopeMath.CarLengthMeters * .5f
				+ (float)IndoorCycling.Core.Simulation.RiderTrafficSeparation.RiderLengthMeters * .5f
				+ AmbientTrafficFollowingMath.MinimumFollowingGapMeters + desiredClearance;
			var rider = CreatePathRider(path, $"headway-{riderSpeed}", riderDistance, riderSpeed, 2.7f);
			var target = AmbientTrafficFollowingMath.TargetSpeed(car.Position, car.Heading, 20, [rider]);
			if (Math.Abs(target - riderSpeed) > .02f)
				throw new InvalidOperationException($"Following controller missed the {AmbientTrafficFollowingMath.FollowingHeadwaySeconds:F1}s gap for rider speed {riderSpeed:F1}m/s: target={target:F2}m/s.");
		}

		var manager = new AmbientActorManager { Name = "CatchUpFollowingHeadwayFixture" };
		AddChild(manager);
		manager.ConfigureTrafficTestFixture(path, 100, 12);
		var riderDistanceNow = 140d;
		var minimumClearance = float.PositiveInfinity;
		for (var frame = 0; frame < 1000; frame++)
		{
			var rider = CreatePathRider(path, "player", riderDistanceNow, 2.5f, 2.7f);
			manager.UpdateTrafficTestFixture([rider], .05);
			var state = manager.FirstCarState ?? throw new InvalidOperationException("Catch-up car disappeared while following a slow rider.");
			var clearance = AmbientActorEnvelopeMath.SignedClearance(state.WorldPosition, state.Heading,
				AmbientActorEnvelopeMath.CarLengthMeters, AmbientActorEnvelopeMath.CarWidthMeters,
				AmbientActorEnvelopeMath.CarHeightMeters, rider.PoseAt(0), rider.LengthMeters,
				rider.WidthMeters, rider.HeightMeters);
			minimumClearance = Math.Min(minimumClearance, clearance);
			if (clearance < AmbientTrafficFollowingMath.MinimumFollowingGapMeters - .25f)
				throw new InvalidOperationException($"Car crowded a laterally-steered slow rider during catch-up at frame {frame}: clearance={clearance:F2}m.");
			riderDistanceNow += 2.5 * .05;
		}
		var final = manager.FirstCarState!.Value;
		var finalRider = CreatePathRider(path, "player", riderDistanceNow, 2.5f, 2.7f);
		var finalClearance = AmbientActorEnvelopeMath.SignedClearance(final.WorldPosition, final.Heading,
			AmbientActorEnvelopeMath.CarLengthMeters, AmbientActorEnvelopeMath.CarWidthMeters,
			AmbientActorEnvelopeMath.CarHeightMeters, finalRider.PoseAt(0), finalRider.LengthMeters,
			finalRider.WidthMeters, finalRider.HeightMeters);
		var expected = AmbientTrafficFollowingMath.MinimumFollowingGapMeters
			+ 2.5f * AmbientTrafficFollowingMath.FollowingHeadwaySeconds;
		if (Math.Abs(finalClearance - expected) > 2.5f || minimumClearance < AmbientTrafficFollowingMath.MinimumFollowingGapMeters - .25f)
			throw new InvalidOperationException($"Catch-up following gap was not stable and comfortable: min={minimumClearance:F2}m final={finalClearance:F2}m expected={expected:F2}m.");
	}

	private void VerifyPlayerPhysicsIsInvariantToAmbientTraffic()
	{
		var scenarios = new[]
		{
			(Width: 7.5, Points: Straight, Power: 1050, Grade: -8d, Delta: .05),
			(Width: 5.8, Points: Straight, Power: 650, Grade: 0d, Delta: .05),
			(Width: 7.5, Points: Bend, Power: 900, Grade: 4d, Delta: .1),
			(Width: 5.8, Points: Bend, Power: 1200, Grade: -10d, Delta: 1.25)
		};
		var riderProfile = new RiderProfile("Invariant rider", 76, BikeWeightKg: 8);
		var bikeProfile = new BikeProfile("Invariant bike", 8);
		foreach (var scenario in scenarios)
		{
			var path = new AmbientActorPath(AmbientActorKind.Car, 701, 1, scenario.Points,
				new AmbientSpeedRange(3, 3), roadWidthMeters: scenario.Width, baseLaneOffsetMeters: 1.7f);
			var traffic = new AmbientActorManager { Name = $"PhysicsInvariantTraffic_{scenario.Width}_{scenario.Delta}" };
			AddChild(traffic);
			traffic.ConfigureTrafficTestFixture(path, 65, 3);
			var withTraffic = RideState.Initial with { VirtualSpeedKph = 43 };
			var withoutTraffic = withTraffic;
			var withTrafficSteering = new SteeringLateralController();
			var withoutTrafficSteering = new SteeringLateralController();
			var engine = new SimulationEngine();
			for (var frame = 0; frame < 96; frame++)
			{
				var delta = frame % 12 == 0 ? scenario.Delta : .05;
				var watts = scenario.Power + frame % 4 * 15;
				var cadence = 78 + frame % 3;
				var timestamp = DateTimeOffset.UnixEpoch.AddMilliseconds(frame * 50);
				var telemetry = new TrainerTelemetry(timestamp, watts, cadence, withTraffic.VirtualSpeedKph,
					HeartRateBpm: 142 + frame % 2);
				var input = new SimulationTickInput(telemetry, scenario.Grade, delta, riderProfile, bikeProfile);
				withoutTraffic = engine.Tick(withoutTraffic, input).NewState;
				withTraffic = engine.Tick(withTraffic, input).NewState;
				var left = frame % 48 is >= 4 and < 11;
				var right = frame % 48 is >= 29 and < 35;
				var noTrafficOffset = withoutTrafficSteering.Update(delta, left, right, null, false, false,
					roadWidthMeters: (float)scenario.Width);
				var trafficOffset = withTrafficSteering.Update(delta, left, right, null, false, false,
					roadWidthMeters: (float)scenario.Width);
				if (withoutTraffic != withTraffic || Math.Abs(noTrafficOffset - trafficOffset) > 1e-12)
					throw new InvalidOperationException($"Ambient cars changed authoritative ride state in scenario {scenario.Width}m/{scenario.Delta}s at frame {frame}: traffic={withTraffic}, none={withoutTraffic}.");

				var player = CreatePathRider(path, "player", withTraffic.DistanceMeters,
					(float)(withTraffic.VirtualSpeedKph / 3.6), (float)trafficOffset);
				traffic.UpdateTrafficTestFixture([player], delta);
				var recordedWithTraffic = (withTraffic.ElapsedSeconds, withTraffic.DistanceMeters,
					withTraffic.CurrentPowerWatts, withTraffic.CurrentCadenceRpm,
					telemetry.ForRecording(timestamp).HeartRateBpm);
				var recordedWithoutTraffic = (withoutTraffic.ElapsedSeconds, withoutTraffic.DistanceMeters,
					withoutTraffic.CurrentPowerWatts, withoutTraffic.CurrentCadenceRpm,
					telemetry.ForRecording(timestamp).HeartRateBpm);
				if (recordedWithTraffic != recordedWithoutTraffic)
					throw new InvalidOperationException("Ambient cars changed the power/cadence/distance/heart-rate recording inputs.");
			}
		}
		GD.Print("[AmbientTrafficFollowingReview] PASS: identical watts, gradient, elapsed time, bike/rider parameters and steering produced exactly identical speed, distance, lateral position, full RideState and recording inputs with a slow car active versus traffic absent (narrow road, bends, fast downhill and large frame deltas).");
	}

	private void VerifyFasterPlayerCausesSafeCarYield()
	{
		var manager = new AmbientActorManager { Name = "FasterPlayerYieldFixture" };
		AddChild(manager);
		var path = new AmbientActorPath(AmbientActorKind.Car, 702, 1, Straight,
			new AmbientSpeedRange(4, 4), roadWidthMeters: 7.5, baseLaneOffsetMeters: 1.7f);
		manager.ConfigureTrafficTestFixture(path, 90, 4);
		var playerDistance = 45d;
		var minimumClearance = float.PositiveInfinity;
		var passed = false;
		var returned = false;
		for (var frame = 0; frame < 500; frame++)
		{
			var player = CreatePathRider(path, "player", playerDistance, 12, 1.75f);
			manager.UpdateTrafficTestFixture([player], .05);
			var car = manager.FirstCarState ?? throw new InvalidOperationException("Yielding car disappeared during the rider pass.");
			var clearance = AmbientActorEnvelopeMath.SignedClearance(car.WorldPosition, car.Heading,
				AmbientActorEnvelopeMath.CarLengthMeters, AmbientActorEnvelopeMath.CarWidthMeters,
				AmbientActorEnvelopeMath.CarHeightMeters, player.PoseAt(0), player.LengthMeters,
				player.WidthMeters, player.HeightMeters);
			minimumClearance = Math.Min(minimumClearance, clearance);
			if (clearance < -.02f)
				throw new InvalidOperationException($"Yielding car intersected a faster player at frame {frame} (clearance={clearance:F3}m).");
			var forwardGap = NVector3.Dot(player.Position - car.WorldPosition, player.Forward);
			passed |= forwardGap < -5;
			returned |= passed && Math.Abs((manager.FirstCarLaneOffset ?? path.BaseLaneOffsetMeters)
				- path.BaseLaneOffsetMeters) < .05f;
			playerDistance += 12 * .05;
		}
		if (!passed || !returned || minimumClearance < -.02f)
			throw new InvalidOperationException($"Car did not yield and return cleanly for a faster local rider: passed={passed}, returned={returned}, minimum clearance={minimumClearance:F3}m.");
	}

	private void VerifyCarYieldAvoidsOccupiedOpposingLane()
	{
		var manager = new AmbientActorManager { Name = "BlockedYieldFixture" };
		AddChild(manager);
		var path = new AmbientActorPath(AmbientActorKind.Car, 703, 1, Straight,
			new AmbientSpeedRange(4, 4), roadWidthMeters: 7.5, baseLaneOffsetMeters: 1.7f);
		var opposing = new AmbientActorPath(AmbientActorKind.Car, 704, 1,
			new[] { new NVector3(1000, 0, 0), NVector3.Zero }, new AmbientSpeedRange(8, 8),
			roadWidthMeters: 7.5, baseLaneOffsetMeters: 1.7f);
		manager.ConfigureTrafficTestFixture(path, 90, 4);
		manager.AddTrafficTestFixture(AmbientActorKind.Car, opposing, 885, 8);
		var player = CreatePathRider(path, "player", 45, 12, 1.75f);
		manager.UpdateTrafficTestFixture([player], .05);
		if (Math.Abs((manager.FirstCarLaneOffset ?? path.BaseLaneOffsetMeters) - path.BaseLaneOffsetMeters) > .05f)
			throw new InvalidOperationException("The car began a centreward yield despite an oncoming vehicle occupying the swept corridor.");
		var immediateSpawn = path.SampleLane(80, path.BaseLaneOffsetMeters);
		if (!AmbientActorManager.WouldSpawnIntoImmediatePlayerCatch(immediateSpawn.Position,
			immediateSpawn.Heading, 4, [player]))
			throw new InvalidOperationException("Car population accepted a slow spawn inside the player's immediate catch window.");
		var distantSpawn = path.SampleLane(250, path.BaseLaneOffsetMeters);
		if (AmbientActorManager.WouldSpawnIntoImmediatePlayerCatch(distantSpawn.Position,
			distantSpawn.Heading, 4, [player]))
			throw new InvalidOperationException("The catch-window spawn policy rejected a distant car unnecessarily.");
	}

	private (bool Passed, float MinimumClearance, float FinalGap, float FinalSpeed, float FinalLaneOffset) RunVehicleScenario(
		double roadWidthMeters, IReadOnlyList<NVector3> points, bool addGroup)
	{
		var manager = new AmbientActorManager { Name = $"TrafficFixture_{roadWidthMeters}" };
		AddChild(manager);
		var path = new AmbientActorPath(AmbientActorKind.Car, 42, 1, OffsetRoutePoints(points, 1.7f),
			new AmbientSpeedRange(12, 12), roadWidthMeters: roadWidthMeters, baseLaneOffsetMeters: 1.7f);
		manager.ConfigureTrafficTestFixture(path, 0, 12);
		var cyclistDistance = 40d;
		var otherCyclistDistance = 58d;
		var fartherCyclistDistance = 77d;
		var minimumClearance = float.PositiveInfinity;
		for (var frame = 0; frame < 600; frame++)
		{
			var riders = new List<AmbientTrafficRider>
			{
				CreatePathRider(path, "player", cyclistDistance, 5, CyclistLaneOffsetMeters)
			};
			if (addGroup)
			{
				riders.Add(CreatePathRider(path, "ai-pacer", otherCyclistDistance, 5.5f, CyclistLaneOffsetMeters));
				riders.Add(CreatePathRider(path, "remote-rider", fartherCyclistDistance, 6, CyclistLaneOffsetMeters));
			}
			manager.UpdateTrafficTestFixture(riders, DeltaSeconds);
			var car = manager.FirstCarState ?? throw new InvalidOperationException($"The production traffic manager lost its test car at frame {frame} (road width {roadWidthMeters}, path length {path.LengthMeters:F1}m).");
			var player = riders[0];
			var clearance = AmbientActorEnvelopeMath.SignedClearance(car.WorldPosition, car.Heading,
				AmbientActorEnvelopeMath.CarLengthMeters, AmbientActorEnvelopeMath.CarWidthMeters,
				AmbientActorEnvelopeMath.CarHeightMeters,
				new AmbientTrafficPose(0, player.Position, player.Forward), player.LengthMeters,
				player.WidthMeters, player.HeightMeters);
			minimumClearance = Math.Min(minimumClearance, clearance);
			if (clearance < -.02f)
				throw new InvalidOperationException($"Actual car/bicycle model envelopes overlapped at frame {frame}: {clearance:F3}m.");
			var passedCurrentCyclist = NVector3.Dot(car.WorldPosition - player.Position, player.Forward) > 4;
			cyclistDistance += 5 * DeltaSeconds;
			otherCyclistDistance += 5.5 * DeltaSeconds;
			fartherCyclistDistance += 6 * DeltaSeconds;
			if (!addGroup && passedCurrentCyclist && Math.Abs((manager.FirstCarLaneOffset ?? path.BaseLaneOffsetMeters)
				- path.BaseLaneOffsetMeters) < .05f) break;
		}
		var finalCar = manager.FirstCarState!.Value;
		var finalRider = CreatePathRider(path, "player", cyclistDistance, 5, CyclistLaneOffsetMeters);
		var forwardGap = NVector3.Dot(finalRider.Position - finalCar.WorldPosition, finalRider.Forward);
		var passed = NVector3.Dot(finalCar.WorldPosition - finalRider.Position, finalRider.Forward) > 4;
		return (passed, minimumClearance, forwardGap, finalCar.SpeedMetersPerSecond,
			manager.FirstCarLaneOffset ?? float.NaN);
	}

	private void VerifyPredictionFollowsBend()
	{
		var manager = new AmbientActorManager { Name = "TrafficPredictionBendFixture" };
		AddChild(manager);
		var path = new AmbientActorPath(AmbientActorKind.Car, 84, 1, Bend,
			new AmbientSpeedRange(12, 12), roadWidthMeters: 7.5, baseLaneOffsetMeters: 1.7f);
		manager.ConfigureTrafficTestFixture(path, 0, 12);
		manager.UpdateTrafficTestFixture([
			new AmbientTrafficRider("player", new NVector3(0, 0, 100), NVector3.UnitX, 0)
		], DeltaSeconds);
		var predicted = manager.GetTrafficReviewSamples().Single().PoseAt(10);
		var expected = path.SampleLane(12 * 10 + 12 * DeltaSeconds, 1.7f);
		if (MathF.Abs(NVector3.Dot(predicted.Forward, NVector3.UnitX)) > .5f
			|| NVector3.Distance(predicted.Position, expected.Position) > 1)
			throw new InvalidOperationException($"Vehicle prediction stopped following its bend beyond 6 seconds: predicted={predicted}, expected={expected}.");
	}

	private void VerifyOccupiedPassLaneBlocksOvertake()
	{
		var manager = new AmbientActorManager { Name = "TrafficBlockedPassFixture" };
		AddChild(manager);
		var carPoints = new[] { new NVector3(0, 0, -1.7f), new NVector3(1000, 0, -1.7f) };
		var passPoints = new[] { new NVector3(0, 0, 1.7f), new NVector3(1000, 0, 1.7f) };
		var carPath = new AmbientActorPath(AmbientActorKind.Car, 91, 1, carPoints,
			new AmbientSpeedRange(12, 12), roadWidthMeters: 7.5, baseLaneOffsetMeters: 1.7f);
		var passLanePath = new AmbientActorPath(AmbientActorKind.Car, 92, 1, passPoints,
			new AmbientSpeedRange(5, 5), roadWidthMeters: 7.5, baseLaneOffsetMeters: -1.7f);
		manager.ConfigureTrafficTestFixture(carPath, 0, 12);
		manager.AddTrafficTestFixture(AmbientActorKind.Car, passLanePath, 60, 5);
		var cyclistDistance = 40d;
		var minimumClearance = float.PositiveInfinity;
		for (var frame = 0; frame < 220; frame++)
		{
			var rider = CreatePathRider(carPath, "player", cyclistDistance, 5, CyclistLaneOffsetMeters);
			manager.UpdateTrafficTestFixture([rider], DeltaSeconds);
			var cars = manager.TrafficTestStates.Where(state => state.Kind == AmbientActorKind.Car).ToArray();
			if (cars.Length != 2) throw new InvalidOperationException("The occupied pass-lane fixture lost one of its cars.");
			var clearance = AmbientActorEnvelopeMath.SignedClearance(cars[0].WorldPosition, cars[0].Heading,
				AmbientActorEnvelopeMath.CarLengthMeters, AmbientActorEnvelopeMath.CarWidthMeters,
				AmbientActorEnvelopeMath.CarHeightMeters,
				new AmbientTrafficPose(0, cars[1].WorldPosition, cars[1].Heading),
				AmbientActorEnvelopeMath.CarLengthMeters, AmbientActorEnvelopeMath.CarWidthMeters,
				AmbientActorEnvelopeMath.CarHeightMeters);
			minimumClearance = Math.Min(minimumClearance, clearance);
			if (clearance < -.02f) throw new InvalidOperationException($"Cars overlapped with a blocked pass lane at frame {frame}: {clearance:F3}m.");
			cyclistDistance += 5 * DeltaSeconds;
		}
		if ((manager.FirstCarLaneOffset ?? 0) < 1.4f || minimumClearance < -.02f)
			throw new InvalidOperationException($"A car entered an occupied overtake lane or lost clearance: lane={manager.FirstCarLaneOffset:F2}m, clearance={minimumClearance:F3}m.");
	}

	private void VerifyCrossingCarsDoNotDeadlock()
	{
		var manager = new AmbientActorManager { Name = "TrafficCrossingDeadlockFixture" };
		AddChild(manager);
		const float lane = 1.7f;
		var eastWest = new AmbientActorPath(AmbientActorKind.Car, 101, 1,
			[new NVector3(-2000, 0, -lane), new NVector3(2000, 0, -lane)],
			new AmbientSpeedRange(12, 12), roadWidthMeters: 7, baseLaneOffsetMeters: lane);
		var southNorth = new AmbientActorPath(AmbientActorKind.Car, 102, 1,
			[new NVector3(lane, 0, -2000), new NVector3(lane, 0, 2000)],
			new AmbientSpeedRange(12, 12), roadWidthMeters: 7, baseLaneOffsetMeters: lane);
		manager.ConfigureTrafficTestFixture(eastWest, 1970, 12);
		manager.AddTrafficTestFixture(AmbientActorKind.Car, southNorth, 1970, 12);
		var player = new AmbientTrafficRider("player", new NVector3(100, 0, 100), NVector3.UnitX, 0);
		var minimumClearance = float.PositiveInfinity;
		var passedCrossingAt = new int[2];
		for (var frame = 0; frame < 1200; frame++)
		{
			manager.UpdateTrafficTestFixture([player], DeltaSeconds);
			var cars = manager.TrafficTestStates.Where(state => state.Kind == AmbientActorKind.Car)
				.OrderBy(state => state.ActorId).ToArray();
			if (cars.Length == 0) break;
			if (cars.Length == 1)
			{
				if (cars[0].ActorId == 101 && cars[0].WorldPosition.X > 5) passedCrossingAt[0] = frame;
				if (cars[0].ActorId == 102 && cars[0].WorldPosition.Z > 5) passedCrossingAt[1] = frame;
				continue;
			}
			var clearance = AmbientActorEnvelopeMath.SignedClearance(cars[0].WorldPosition, cars[0].Heading,
				AmbientActorEnvelopeMath.CarLengthMeters, AmbientActorEnvelopeMath.CarWidthMeters,
				AmbientActorEnvelopeMath.CarHeightMeters,
				new AmbientTrafficPose(0, cars[1].WorldPosition, cars[1].Heading),
				AmbientActorEnvelopeMath.CarLengthMeters, AmbientActorEnvelopeMath.CarWidthMeters,
				AmbientActorEnvelopeMath.CarHeightMeters);
			minimumClearance = Math.Min(minimumClearance, clearance);
			if (clearance < -.02f)
				throw new InvalidOperationException($"Vehicles intersected at a junction during frame {frame}: {clearance:F3}m.");
			if (cars[0].WorldPosition.X > 5) passedCrossingAt[0] = passedCrossingAt[0] == 0 ? frame : passedCrossingAt[0];
			if (cars[1].WorldPosition.Z > 5) passedCrossingAt[1] = passedCrossingAt[1] == 0 ? frame : passedCrossingAt[1];
			if (frame == 800 && passedCrossingAt.All(framePassed => framePassed == 0))
				throw new InvalidOperationException($"Crossing traffic deadlocked before the intersection: cars={cars[0].WorldPosition}/{cars[1].WorldPosition}, speeds={cars[0].SpeedMetersPerSecond:F2}/{cars[1].SpeedMetersPerSecond:F2}m/s, clearance={minimumClearance:F3}m.");
		}
		if (passedCrossingAt.Any(framePassed => framePassed == 0))
			throw new InvalidOperationException($"Crossing traffic failed to clear the intersection; minimum clearance={minimumClearance:F3}m, passed={string.Join(',', passedCrossingAt)}, remaining={string.Join(';', manager.TrafficTestStates.Select(car => $"{car.ActorId}:{car.WorldPosition}:{car.SpeedMetersPerSecond:F2}"))}.");
		GD.Print($"[AmbientTrafficFollowingReview] crossing priority cleared in frames {passedCrossingAt[0]}/{passedCrossingAt[1]} with minimum envelope clearance={minimumClearance:F3}m.");
	}

	private void VerifyLowFrameRateFollowing()
	{
		var manager = new AmbientActorManager { Name = "TrafficLowFrameRateFixture" };
		AddChild(manager);
		var path = new AmbientActorPath(AmbientActorKind.Car, 103, 1, Straight,
			new AmbientSpeedRange(12, 12), roadWidthMeters: 5.8, baseLaneOffsetMeters: 1.7f);
		manager.ConfigureTrafficTestFixture(path, 0, 12);
		var cyclistDistance = 40d;
		var previousSpeed = 12f;
		var minimumClearance = float.PositiveInfinity;
		var maximumSpeedChange = 0f;
		for (var frame = 0; frame < 120; frame++)
		{
			var rider = CreatePathRider(path, "player", cyclistDistance, 5, CyclistLaneOffsetMeters);
			manager.UpdateTrafficTestFixture([rider], .25);
			var car = manager.FirstCarState ?? throw new InvalidOperationException("Low-frame-rate fixture lost its car.");
			var clearance = AmbientActorEnvelopeMath.SignedClearance(car.WorldPosition, car.Heading,
				AmbientActorEnvelopeMath.CarLengthMeters, AmbientActorEnvelopeMath.CarWidthMeters,
				AmbientActorEnvelopeMath.CarHeightMeters,
				new AmbientTrafficPose(0, rider.Position, rider.Forward), rider.LengthMeters, rider.WidthMeters, rider.HeightMeters);
			minimumClearance = Math.Min(minimumClearance, clearance);
			maximumSpeedChange = Math.Max(maximumSpeedChange, Math.Abs(car.SpeedMetersPerSecond - previousSpeed));
			if (clearance < -.02f)
				throw new InvalidOperationException($"Low-frame-rate car/rider envelopes intersected at frame {frame}: clearance={clearance:F3}m.");
			if (Math.Abs(car.SpeedMetersPerSecond - previousSpeed) > 1.26f)
				throw new InvalidOperationException($"Car speed changed faster than the braking limit during a 250ms update: {previousSpeed:F2}->{car.SpeedMetersPerSecond:F2}m/s.");
			previousSpeed = car.SpeedMetersPerSecond;
			cyclistDistance += 5 * .25;
		}
		if (minimumClearance < -.02f || maximumSpeedChange > 1.26f)
			throw new InvalidOperationException($"Low-frame-rate traffic did not preserve the car envelope: minimum clearance={minimumClearance:F3}m, maximum speed change={maximumSpeedChange:F3}m/s.");
	}

	private void VerifySameLaneCarsDoNotUseJunctionPriority()
	{
		var manager = new AmbientActorManager { Name = "TrafficStoppedCarFixture" };
		AddChild(manager);
		var path = new AmbientActorPath(AmbientActorKind.Car, 104, 1, Straight,
			new AmbientSpeedRange(12, 12), roadWidthMeters: 5.8, baseLaneOffsetMeters: 0);
		manager.ConfigureTrafficTestFixture(path, 0, 12);
		manager.AddTrafficTestFixture(AmbientActorKind.Car, path, 35, 0);
		var player = new AmbientTrafficRider("player", new NVector3(0, 0, 100), NVector3.UnitX, 0);
		var minimumClearance = float.PositiveInfinity;
		for (var frame = 0; frame < 240; frame++)
		{
			manager.UpdateTrafficTestFixture([player], DeltaSeconds);
			var cars = manager.TrafficTestStates.Where(state => state.Kind == AmbientActorKind.Car)
				.OrderBy(state => state.ActorId).ToArray();
			if (cars.Length != 2) throw new InvalidOperationException("Same-lane stopped-car fixture lost a vehicle.");
			var clearance = AmbientActorEnvelopeMath.SignedClearance(cars[0].WorldPosition, cars[0].Heading,
				AmbientActorEnvelopeMath.CarLengthMeters, AmbientActorEnvelopeMath.CarWidthMeters,
				AmbientActorEnvelopeMath.CarHeightMeters,
				new AmbientTrafficPose(0, cars[1].WorldPosition, cars[1].Heading),
				AmbientActorEnvelopeMath.CarLengthMeters, AmbientActorEnvelopeMath.CarWidthMeters,
				AmbientActorEnvelopeMath.CarHeightMeters);
			minimumClearance = Math.Min(minimumClearance, clearance);
			if (clearance < -.02f)
				throw new InvalidOperationException($"Same-lane car envelopes intersected at frame {frame}: {clearance:F3}m.");
		}
		var final = manager.TrafficTestStates.OrderBy(state => state.ActorId).ToArray();
		var followingSpeed = final[0].SpeedMetersPerSecond;
		var gap = final[1].WorldPosition.X - final[0].WorldPosition.X;
		if (minimumClearance < 1.15f || followingSpeed > .2f || gap < AmbientActorEnvelopeMath.CarLengthMeters + 1)
			throw new InvalidOperationException($"Priority ordering weakened same-lane following: clearance={minimumClearance:F3}m, speed={followingSpeed:F2}m/s, gap={gap:F2}m.");
	}

	private void VerifyCarFollowsPedestrianObstacle()
	{
		var manager = new AmbientActorManager { Name = "TrafficPedestrianObstacleFixture" };
		AddChild(manager);
		var carPath = new AmbientActorPath(AmbientActorKind.Car, 105, 1, Straight,
			new AmbientSpeedRange(12, 12), roadWidthMeters: 5.8, baseLaneOffsetMeters: 0);
		var footpath = new AmbientActorPath(AmbientActorKind.Pedestrian, 106, 1,
			[new NVector3(0, 0, 0), new NVector3(1000, 0, 0)],
			new AmbientSpeedRange(0, 0), roadWidthMeters: 5.8, baseLaneOffsetMeters: 0);
		manager.ConfigureTrafficTestFixture(carPath, 0, 12);
		manager.AddTrafficTestFixture(AmbientActorKind.Pedestrian, footpath, 35, 0);
		var player = new AmbientTrafficRider("player", new NVector3(0, 0, 100), NVector3.UnitX, 0);
		var minimumClearance = float.PositiveInfinity;
		for (var frame = 0; frame < 240; frame++)
		{
			manager.UpdateTrafficTestFixture([player], DeltaSeconds);
			var car = manager.FirstCarState ?? throw new InvalidOperationException("Pedestrian clearance fixture lost its car.");
			var pedestrian = manager.FirstPedestrianState ?? throw new InvalidOperationException("Pedestrian clearance fixture lost its pedestrian.");
			var clearance = AmbientActorEnvelopeMath.SignedClearance(car.WorldPosition, car.Heading,
				AmbientActorEnvelopeMath.CarLengthMeters, AmbientActorEnvelopeMath.CarWidthMeters,
				AmbientActorEnvelopeMath.CarHeightMeters,
				new AmbientTrafficPose(0, pedestrian.WorldPosition, pedestrian.Heading),
				AmbientActorEnvelopeMath.PedestrianLengthMeters, AmbientActorEnvelopeMath.PedestrianWidthMeters,
				AmbientActorEnvelopeMath.PedestrianHeightMeters);
			minimumClearance = Math.Min(minimumClearance, clearance);
			if (clearance < -.02f)
				throw new InvalidOperationException($"Car/pedestrian envelopes intersected at frame {frame}: {clearance:F3}m.");
		}
		if (minimumClearance < -.02f || manager.FirstCarState!.Value.SpeedMetersPerSecond > .25f)
			throw new InvalidOperationException($"Car failed to stop safely for a pedestrian: clearance={minimumClearance:F3}m, speed={manager.FirstCarState!.Value.SpeedMetersPerSecond:F2}m/s.");
	}

	private static AmbientTrafficRider CreatePathRider(AmbientActorPath path, string id,
		double distance, float speed, float laneOffset)
	{
		var samples = new List<AmbientTrafficPose>(49);
		for (var sample = 0; sample <= 48; sample++)
		{
			var seconds = sample * AmbientTrafficFollowingMath.PredictionSampleSeconds;
			var pose = path.SampleLane(distance + speed * seconds, laneOffset);
			samples.Add(new AmbientTrafficPose(seconds, pose.Position, pose.Heading));
		}
		var current = path.SampleLane(distance, laneOffset);
		return new AmbientTrafficRider(id, current.Position, current.Heading, speed,
			(float)IndoorCycling.Core.Simulation.RiderTrafficSeparation.RiderLengthMeters,
			(float)IndoorCycling.Core.Simulation.RiderTrafficSeparation.RiderHalfWidthMeters * 2,
			1.8f, samples);
	}

	private static IReadOnlyList<NVector3> OffsetRoutePoints(IReadOnlyList<NVector3> points, float laneOffset)
	{
		var result = new NVector3[points.Count];
		for (var index = 0; index < points.Count; index++)
		{
			var tangent = index == points.Count - 1 ? points[index] - points[index - 1]
				: points[index + 1] - points[index];
			tangent.Y = 0;
			if (tangent.LengthSquared() < 1e-8f) tangent = NVector3.UnitX;
			var left = NVector3.Normalize(NVector3.Cross(NVector3.UnitY, NVector3.Normalize(tangent)));
			result[index] = points[index] + left * laneOffset;
		}
		return result;
	}
}
