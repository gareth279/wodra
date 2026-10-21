using System;
using Godot;

/// <summary>Exercises the production ambient car manager in a deterministic scene with a faster car and slower rider.</summary>
public partial class AmbientTrafficFollowingReviewTest : Node
{
	public override void _Ready()
	{
		try
		{
			var manager = new AmbientActorManager { Name = "AmbientTrafficFixture" };
			AddChild(manager);
			var path = new AmbientActorPath(AmbientActorKind.Car, 42, 1,
				[new System.Numerics.Vector3(0, 0, 0), new System.Numerics.Vector3(1000, 0, 0)],
				new AmbientSpeedRange(12, 12));
			manager.ConfigureTrafficTestFixture(path, 0, 12);
			var cyclist = new AmbientTrafficRider("player", new System.Numerics.Vector3(40, 0, 0),
				System.Numerics.Vector3.UnitX, 5);
			const double delta = .05;
			for (var frame = 0; frame < 1200; frame++)
			{
				var ai = cyclist with { RiderId = "ai-pacer", Position = cyclist.Position + new System.Numerics.Vector3(75, 0, 0), SpeedMetersPerSecond = 8 };
				var remote = cyclist with { RiderId = "remote-rider", Position = cyclist.Position + new System.Numerics.Vector3(90, 0, 0), SpeedMetersPerSecond = 7 };
				manager.UpdateTrafficTestFixture([cyclist, ai, remote], delta);
				cyclist = cyclist with { Position = cyclist.Position + cyclist.Forward * (cyclist.SpeedMetersPerSecond * (float)delta) };
				var car = manager.FirstCarState ?? throw new InvalidOperationException("The traffic manager lost its test car.");
				var gap = System.Numerics.Vector3.Distance(cyclist.Position, car.WorldPosition);
				if (gap < AmbientTrafficFollowingMath.MinimumFollowingGapMeters - .03f)
					throw new InvalidOperationException($"The production traffic manager allowed overlap at frame {frame}: gap={gap:F2}m.");
				if (car.WorldPosition.X >= cyclist.Position.X)
					throw new InvalidOperationException($"The car passed the cyclist without an overtaking path at frame {frame}.");
			}
			var finalCar = manager.FirstCarState!.Value;
			var finalGap = cyclist.Position.X - finalCar.WorldPosition.X;
			if (Math.Abs(finalCar.SpeedMetersPerSecond - cyclist.SpeedMetersPerSecond) > .15f || finalGap > 12)
				throw new InvalidOperationException($"The car did not settle behind the cyclist: speed={finalCar.SpeedMetersPerSecond:F2}, gap={finalGap:F2}m.");
			GD.Print("[AmbientTrafficFollowingReview] PASS: production AmbientActorManager keeps a deterministic faster car behind a slower cyclist and settles to safe following speed/gap.");
			GetTree().Quit(0);
		}
		catch (Exception ex)
		{
			GD.PushError($"[AmbientTrafficFollowingReview] FAIL: {ex}");
			GetTree().Quit(1);
		}
	}
}
