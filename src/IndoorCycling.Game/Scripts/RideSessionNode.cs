using Godot;
using IndoorCycling.Core.Models;
using IndoorCycling.Core.Simulation;
using IndoorCycling.Trainer.Models;

namespace IndoorCycling.Game.Scripts;

public partial class RideSessionNode : Node
{
	[Export] public NodePath? HudControllerPath { get; set; }

	private HudController? _hudController;
	private RideState _rideState = RideState.Initial;

	public override async void _Ready()
	{
		if (HudControllerPath is not null)
		{
			_hudController = GetNodeOrNull<HudController>(HudControllerPath);
		}

		await GameBootstrap.TrainerClient.ConnectAsync();

		GD.Print("Ride session started.");
	}

	public override async void _Process(double delta)
	{
		if (!GameBootstrap.TrainerClient.IsConnected)
		{
			return;
		}

		var telemetry = GameBootstrap.TrainerClient.GetLatestTelemetry();
		var gradient = GetGradientForCurrentDistance(_rideState.DistanceMeters);

		var input = new SimulationTickInput(
			Telemetry: telemetry,
			GradientPercent: gradient,
			DeltaSeconds: delta,
			RiderProfile: GameBootstrap.RiderProfile,
			BikeProfile: GameBootstrap.BikeProfile
		);

		var result = GameBootstrap.SimulationEngine.Tick(_rideState, input);
		_rideState = result.NewState;

		await GameBootstrap.TrainerClient.SetSimulationAsync(
			new SimulationCommand(
				GradePercent: gradient,
				RiderWeightKg: GameBootstrap.RiderProfile.WeightKg,
				BikeWeightKg: GameBootstrap.BikeProfile.WeightKg
			)
		);

		_hudController?.UpdateFromRideState(_rideState, telemetry);
	}

	private static double GetGradientForCurrentDistance(double distanceMeters)
	{
		// crude test route:
		// 0-500m flat
		// 500-1500m 3%
		// 1500-2500m 6%
		// 2500-3500m -2%
		// then repeat
		var d = distanceMeters % 3500.0;

		if (d < 500) return 0;
		if (d < 1500) return 3;
		if (d < 2500) return 6;
		return -2;
	}
}
