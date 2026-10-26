using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using NVector3 = System.Numerics.Vector3;

/// <summary>Forces a close ambient car interaction on the production Dunedin RideScreen route.</summary>
public partial class AmbientCarRideScreenReviewTest : Node
{
	private const double StepSeconds = .05;
	[Export] public string RouteId { get; set; } = "dunedin-rolling-loop";
	[Export] public bool PlayerCatchesCar { get; set; }

	public override async void _Ready()
	{
		try
		{
			DisplayServer.WindowSetSize(new Vector2I(1280, 720));
			var routeName = RouteId == "dunedin-rolling-loop" ? "rolling-city" : "oval-gardens";
			var output = System.Environment.GetEnvironmentVariable("WODRA_REVIEW_OUTPUT")
				?? Path.Combine(ProjectSettings.GlobalizePath("res://../.."), "artifacts", $"alpha53-ambient-car-review-{routeName}");
			Directory.CreateDirectory(output);
			var screen = GD.Load<PackedScene>("res://Scenes/RideScreen.tscn").Instantiate();
			AddChild(screen);
			await Settle(6);
			// A scratch user-data root has no saved rider profile, so the real first-run
			// profile editor would otherwise obscure the chase-camera review captures.
			foreach (var window in GetTree().Root.FindChildren("", "Window", true, false).OfType<Window>())
				if (window.Title == "Manage Rider Profiles") window.Hide();
			GameBootstrap.Settings.DebugModeEnabled = true;
			GameBootstrap.Settings.DummyTrainer.Mode = "Watts";
			GameBootstrap.Settings.DummyTrainer.TargetWatts = PlayerCatchesCar ? 750 : 220;
			GameBootstrap.Settings.DummyTrainer.CadenceRpm = 86;
			GameBootstrap.Settings.Recording.OutputFolder = Path.Combine(ProjectSettings.GlobalizePath("user://"), "ambient-car-review-fit");
			GameBootstrap.ConfigureFakeTrainerMode();
			var world = screen.GetNode<RideWorldView>("RideWorldView");
			var requireRiderGroup = RouteId == "dunedin-rolling-loop";
			world.AiRiderTargetCount = requireRiderGroup ? 4 : 0;
			world.AiRiderMaximumCount = requireRiderGroup ? 4 : 0;
			var session = screen.GetNode<RideSessionNode>("RideSessionNode");
			if (!await session.StartTrainerSessionAsync(RouteCatalog.GetById(RouteId)))
				throw new InvalidOperationException($"Could not start production route {RouteId} for the ambient-car review.");
			var hud = screen.GetNode<HudController>("HudController");
			hud.Show();
			screen.GetNode<StartPanelController>("StartPanel").Hide();
			await Settle(8);
			if (world.RouteGeometry is null) throw new InvalidOperationException("Production RideScreen route did not load.");
			session.JumpToRouteDistance("720");
			session.ConfirmDebugDistanceJump();
			hud.HideDebugJumpConfirmation();
			hud.SetRecordingState(false, false);
			await Settle(8);

			var riders = world.GetRiderCollisionReviewSamples().Where(rider => rider.RiderId != "ghost").ToArray();
			var player = riders.SingleOrDefault(rider => rider.RiderId == "player");
			if (player.RiderId is null) throw new InvalidOperationException("Production RideScreen did not expose the active player for the close car test.");
			if (requireRiderGroup && riders.Count(rider => rider.RiderId != "player") < 2)
				throw new InvalidOperationException("Production RideScreen did not populate nearby AI riders for the ambient-car interaction test.");

			var route = world.RouteGeometry;
			var points = route.Samples.Select(sample =>
			{
				var global = world.ToGlobal(ProceduralRoad.ToGodot(sample.Position));
				var forward = world.GlobalBasis * ProceduralRoad.ToGodot(sample.Forward);
				var left = forward.Cross(Vector3.Up).Normalized();
				global += left * 1.7f;
				return ToNumerics(global);
			}).ToArray();
			var carPath = new AmbientActorPath(AmbientActorKind.Car, 53001, 1, points,
				new AmbientSpeedRange(12, 12), "RideScreen close-review route", "residential",
				roadWidthMeters: 7.5, baseLaneOffsetMeters: 1.7f);
			if (!carPath.IsUsable) throw new InvalidOperationException("Could not build the close-interaction car path from the production route geometry.");
			var manager = new AmbientActorManager { Name = "ControlledProductionRideScreenCar" };
			world.AddChild(manager);
			var startDistance = PlayerCatchesCar
				? Math.Clamp(player.RouteDistanceMeters + 45, 0, carPath.LengthMeters - 80)
				: Math.Clamp(player.RouteDistanceMeters - 55, 0, carPath.LengthMeters - 30);
			manager.ConfigureTrafficTestFixture(carPath, startDistance, PlayerCatchesCar ? 4 : 12);
			var minimumClearance = float.PositiveInfinity;
			var closestApproach = float.PositiveInfinity;
			var carPassed = false;
			var baseLane = carPath.BaseLaneOffsetMeters;
			var captured = 0;
			var scriptedPlayerDistance = player.RouteDistanceMeters;
			for (var frame = 0; frame < 720; frame++)
			{
				await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
				if (PlayerCatchesCar)
				{
					// Move the actual RideScreen rider through production route geometry at a
					// deterministic test pace; this changes only the fixture's route position.
					scriptedPlayerDistance += 12 * StepSeconds;
					session.JumpToRouteDistance(scriptedPlayerDistance.ToString("F3", System.Globalization.CultureInfo.InvariantCulture));
					session.ConfirmDebugDistanceJump();
					await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
				}
				var trafficRiders = BuildTrafficRiders(world, manager, PlayerCatchesCar
					? (float)(session.CurrentRideState.VirtualSpeedKph / 3.6) : null);
				manager.UpdateTrafficTestFixture(trafficRiders, StepSeconds);
				var car = manager.FirstCarState;
				if (car is null) continue;
				foreach (var rider in world.GetRiderCollisionReviewSamples().Where(r => r.RiderId != "ghost"))
				{
					var clearance = AmbientActorEnvelopeMath.SignedClearance(car.Value.WorldPosition, car.Value.Heading,
						AmbientActorEnvelopeMath.CarLengthMeters, AmbientActorEnvelopeMath.CarWidthMeters,
						AmbientActorEnvelopeMath.CarHeightMeters,
						new AmbientTrafficPose(0, ToNumerics(rider.WorldPosition), ToNumerics(rider.WorldForward)),
						rider.ModelLengthMeters, rider.ModelWidthMeters, rider.WorldBounds.Size.Y);
					minimumClearance = Math.Min(minimumClearance, clearance);
					if (clearance < -.02f)
						throw new InvalidOperationException($"Production RideScreen car/rider envelopes intersected: rider={rider.RiderId}, clearance={clearance:F3}m, frame={frame}.");
					if (rider.RiderId == "player") closestApproach = Math.Min(closestApproach, clearance);
				}
				var currentPlayer = world.GetRiderCollisionReviewSamples().Single(rider => rider.RiderId == "player");
				var riderPosition = ToNumerics(currentPlayer.WorldPosition);
				var forwardGap = NVector3.Dot(riderPosition - car.Value.WorldPosition, ToNumerics(currentPlayer.WorldForward));
				var riderHasPassed = PlayerCatchesCar ? forwardGap > 6 : forwardGap < -6;
				if (riderHasPassed && Math.Abs((manager.FirstCarLaneOffset ?? baseLane) - baseLane) < .05f)
					carPassed = true;
				if (frame % 12 == 0 && captured < 80)
				{
					await Capture(Path.Combine(output, $"frame-{captured:D4}.png"));
					captured++;
				}
			}
			if (minimumClearance == float.PositiveInfinity)
				throw new InvalidOperationException("No production car/rider clearance samples were measured.");
			if (closestApproach > 20)
				throw new InvalidOperationException($"Controlled vehicle never came close to the production player (minimum clearance={closestApproach:F2}m).");
			if (!carPassed)
			{
				var finalCar = manager.FirstCarState;
				var finalPlayer = world.GetRiderCollisionReviewSamples().Single(rider => rider.RiderId == "player");
				var finalGap = finalCar is null ? double.NaN : NVector3.Dot(ToNumerics(finalPlayer.WorldPosition)
					- finalCar.Value.WorldPosition, ToNumerics(finalPlayer.WorldForward));
				var playerSpeed = (float)(session.CurrentRideState.VirtualSpeedKph / 3.6);
				var safelyWaitingForGroup = requireRiderGroup && finalGap > 0
					&& closestApproach >= AmbientTrafficFollowingMath.MinimumFollowingGapMeters - .25f
					&& finalCar is not null && finalCar.Value.SpeedMetersPerSecond <= playerSpeed + .5f;
				if (!safelyWaitingForGroup)
					throw new InvalidOperationException($"Controlled car neither passed safely nor waited behind an occupied pass lane on route {RouteId}: gap={finalGap:F2}m, car-speed={finalCar?.SpeedMetersPerSecond:F2}m/s, player-speed={playerSpeed:F2}m/s, lane={manager.FirstCarLaneOffset:F2}m, clearance={closestApproach:F2}m.");
				GD.Print($"[AmbientCarRideScreenReview] PASS: production route={RouteId}, direction=car held behind rider group, controlled safe-following car, AI={world.GetDiagnosticState().AiRiders}, minimum envelope clearance={minimumClearance:F3}m, player clearance={closestApproach:F2}m, gap={finalGap:F2}m.");
				GetTree().Quit(0);
				return;
			}
			var diagnostic = world.GetDiagnosticState();
			if (requireRiderGroup && diagnostic.AiRiders < 3) throw new InvalidOperationException($"RideScreen AI population changed during car interaction: {diagnostic.AiRiders}.");
			GD.Print($"[AmbientCarRideScreenReview] PASS: production route={RouteId}, direction={(PlayerCatchesCar ? "player caught car" : "car caught player")}, controlled close car, AI={diagnostic.AiRiders}, minimum envelope clearance={minimumClearance:F3}m, player closest approach={closestApproach:F2}m, passed-and-returned={carPassed}, frames={captured}, evidence={output}");
			GetTree().Quit(0);
		}
		catch (Exception error)
		{
			GD.PushError($"[AmbientCarRideScreenReview] FAIL: {error}");
			GetTree().Quit(1);
		}
	}

	private static IReadOnlyList<AmbientTrafficRider> BuildTrafficRiders(RideWorldView world, AmbientActorManager manager,
		float? playerSpeedOverride = null)
	{
		var result = new List<AmbientTrafficRider>();
		foreach (var sample in world.GetRiderCollisionReviewSamples().Where(rider => rider.RiderId != "ghost"))
		{
			var speed = sample.RiderId == "player" ? playerSpeedOverride ?? 5f : 4.5f;
			var poses = new List<AmbientTrafficPose>(49);
			for (var index = 0; index <= 48; index++)
			{
				var seconds = index * AmbientTrafficFollowingMath.PredictionSampleSeconds;
				if (index == 0)
				{
					poses.Add(new AmbientTrafficPose(0, ToNumerics(sample.WorldPosition), ToNumerics(sample.WorldForward)));
					continue;
				}
				var routePose = world.RouteGeometry!.GetPoseAtDistance(sample.RouteDistanceMeters + speed * seconds);
				var point = world.ToGlobal(ProceduralRoad.ToGodot(routePose.Position));
				var heading = world.GlobalBasis * ProceduralRoad.ToGodot(routePose.Forward);
				point += heading.Cross(Vector3.Up).Normalized() * (float)sample.LateralOffsetMeters;
				point.Y = sample.WorldPosition.Y;
				poses.Add(new AmbientTrafficPose(seconds, ToNumerics(point), ToNumerics(heading)));
			}
			result.Add(new AmbientTrafficRider(sample.RiderId, ToNumerics(sample.WorldPosition), ToNumerics(sample.WorldForward),
				speed, sample.ModelLengthMeters, sample.ModelWidthMeters, sample.WorldBounds.Size.Y, poses));
		}
		return result;
	}

	private async Task Capture(string path)
	{
		if (DisplayServer.GetName() == "headless") return;
		var error = GetViewport().GetTexture().GetImage().SavePng(path);
		if (error != Error.Ok) throw new IOException($"Could not save production car review frame {path}: {error}.");
	}

	private async Task Settle(int frames)
	{
		for (var frame = 0; frame < frames; frame++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
	}

	private static NVector3 ToNumerics(Vector3 value) => new(value.X, value.Y, value.Z);
}
