using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
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
				|| Math.Abs(following.FinalSpeed - 5) > .2f || following.FinalGap > 12)
				throw new InvalidOperationException($"Narrow road did not keep a fast car safely behind the group: {following}.");

			var overtake = RunVehicleScenario(roadWidthMeters: 7.5, Bend, addGroup: false);
			if (!overtake.Passed || overtake.MinimumClearance < -.02f)
				throw new InvalidOperationException($"Wide curved road did not complete a clear overtake: {overtake}.");

			VerifyOccupiedPassLaneBlocksOvertake();
			VerifyPredictionFollowsBend();
			GD.Print($"[AmbientTrafficFollowingReview] PASS: narrow road follows group (clearance={following.MinimumClearance:F3}m, final gap={following.FinalGap:F2}m, speed={following.FinalSpeed:F2}m/s); curved 7.5m road safely overtakes (clearance={overtake.MinimumClearance:F3}m); 12s obstacle prediction follows the bend.");
			GetTree().Quit(0);
		}
		catch (Exception ex)
		{
			GD.PushError($"[AmbientTrafficFollowingReview] FAIL: {ex}");
			GetTree().Quit(1);
		}
	}

	private (bool Passed, float MinimumClearance, float FinalGap, float FinalSpeed) RunVehicleScenario(
		double roadWidthMeters, IReadOnlyList<NVector3> points, bool addGroup)
	{
		var manager = new AmbientActorManager { Name = $"TrafficFixture_{roadWidthMeters}" };
		AddChild(manager);
		var path = new AmbientActorPath(AmbientActorKind.Car, 42, 1, points,
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
		return (passed, minimumClearance, forwardGap, finalCar.SpeedMetersPerSecond);
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
		var roadPoints = new[] { new NVector3(0, 0, 0), new NVector3(1000, 0, 0) };
		var carPath = new AmbientActorPath(AmbientActorKind.Car, 91, 1, roadPoints,
			new AmbientSpeedRange(12, 12), roadWidthMeters: 7.5, baseLaneOffsetMeters: 1.7f);
		var passLanePath = new AmbientActorPath(AmbientActorKind.Car, 92, 1, roadPoints,
			new AmbientSpeedRange(5, 5), roadWidthMeters: 7.5, baseLaneOffsetMeters: .15f);
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
}
