using Godot;
using IndoorCycling.Core.Models;
using IndoorCycling.Core.Simulation;
using IndoorCycling.Trainer.Abstractions;
using IndoorCycling.Trainer.Implementations;

namespace IndoorCycling.Game.Scripts;

public partial class GameBootstrap : Node
{
	public static ITrainerClient TrainerClient { get; private set; } = default!;
	public static ISimulationEngine SimulationEngine { get; private set; } = default!;
	public static RiderProfile RiderProfile { get; private set; } = default!;
	public static BikeProfile BikeProfile { get; private set; } = default!;

	public override void _Ready()
	{
		TrainerClient = new FakeTrainerClient();
		SimulationEngine = new SimulationEngine();
		RiderProfile = new RiderProfile("Gareth", 88.0);
		BikeProfile = new BikeProfile("Indoor Bike", 9.0);

		GD.Print("GameBootstrap initialised.");
	}
}
