using Godot;
using IndoorCycling.Core.Models;
using IndoorCycling.Core.Simulation;
using IndoorCycling.Trainer.Abstractions;
using IndoorCycling.Trainer.Implementations;

public partial class GameBootstrap : Node
{
	public enum SessionMode
	{
		None,
		Trainer,
		FitReplay
	}

	public static ITrainerClient? TrainerClient { get; private set; }
	public static ISimulationEngine SimulationEngine { get; private set; } = default!;
	public static RiderProfile RiderProfile { get; private set; } = default!;
	public static BikeProfile BikeProfile { get; private set; } = default!;

	public static SessionMode Mode { get; private set; } = SessionMode.None;
	public static string? FitReplayPath { get; private set; }

	public override void _Ready()
	{
		SimulationEngine = new SimulationEngine();
		RiderProfile = new RiderProfile("Gareth", 88.0);
		BikeProfile = new BikeProfile("Indoor Bike", 9.0);

		GD.Print("GameBootstrap initialised.");
	}

	public static void ConfigureTrainerMode()
	{
		Mode = SessionMode.Trainer;
		FitReplayPath = null;

		// Swap this if needed later, but this is what you want on the cycling PC.
		//TrainerClient = new WindowsBleTrainerClient();
		TrainerClient = new FakeTrainerClient();

		GD.Print($"Configured trainer mode. Trainer type: {TrainerClient.GetType().Name}");
	}

	public static void ConfigureFitReplayMode(string fitPath)
	{
		Mode = SessionMode.FitReplay;
		FitReplayPath = fitPath;

		// Keep this null for now unless/until you wire replay into a trainer-like source.
		TrainerClient = null;

		GD.Print($"Configured FIT replay mode. File: {fitPath}");
	}
}
