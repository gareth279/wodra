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

	public static AppSettings Settings { get; private set; } = default!;
	public static bool DebugModeEnabled => Settings?.DebugModeEnabled == true;

	public static SessionMode Mode { get; private set; } = SessionMode.None;
	public static string? FitReplayPath { get; private set; }

	public override void _Ready()
	{
		GameLogger.Init();

		Settings = AppSettings.LoadOrCreate();

		SimulationEngine = new SimulationEngine();

		RiderProfile = new RiderProfile(
			Settings.Rider.Name,
			Settings.Rider.WeightKg
		);

		BikeProfile = new BikeProfile(
			Settings.Rider.BikeName,
			Settings.Rider.BikeWeightKg
		);

		GameLogger.Log("game_bootstrap_initialised", new
		{
			rider = RiderProfile.Name,
			riderWeightKg = RiderProfile.WeightKg,
			bike = BikeProfile.Name,
			bikeWeightKg = BikeProfile.WeightKg,
			settingsPath = AppSettings.SettingsPath
		});

		GD.Print("GameBootstrap initialised.");
	}

	public static void ResetSession()
	{
		Mode = SessionMode.None;
		FitReplayPath = null;
		TrainerClient = null;
	}

	public static void ConfigureFakeTrainerMode()
	{
		Mode = SessionMode.Trainer;
		FitReplayPath = null;
		TrainerClient = new FakeTrainerClient();

		GD.Print("Configured fake trainer mode.");
	}

	public static void ConfigureRealTrainerMode(ITrainerClient trainerClient)
	{
		Mode = SessionMode.Trainer;
		FitReplayPath = null;
		TrainerClient = trainerClient;

		GD.Print($"Configured real trainer mode: {trainerClient.GetType().Name}");
	}

	public static void ConfigureFitReplayMode(string fitPath)
	{
		Mode = SessionMode.FitReplay;
		FitReplayPath = fitPath;
		TrainerClient = null;

		GD.Print($"Configured FIT replay mode. File: {fitPath}");
	}
}
