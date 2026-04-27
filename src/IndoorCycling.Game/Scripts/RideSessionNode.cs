using Godot;
using IndoorCycling.Core.Models;
using IndoorCycling.Core.Simulation;
using IndoorCycling.Trainer.Models;
using System;
using System.Threading.Tasks;

public partial class RideSessionNode : Node
{
	private HudController? _hudController;
	private bool _sessionStarted;
	private bool _trainerSessionActive;
	private bool _fitReplaySessionActive;

	private RideState _rideState = RideState.Initial;
	private double _elapsedSeconds;

	public override void _Ready()
	{
		_hudController = GetNodeOrNull<HudController>("../CanvasLayer/HudController");
		_hudController?.SetStatus("Choose a mode to begin.");
	}

	public async Task StartTrainerSessionAsync()
	{
		if (GameBootstrap.TrainerClient is null)
		{
			_hudController?.SetStatus("No trainer client configured.");
			return;
		}

		if (!GameBootstrap.TrainerClient.IsConnected)
		{
			_hudController?.SetStatus("Connecting to trainer...");
			await GameBootstrap.TrainerClient.ConnectAsync();
		}
		else
		{
			_hudController?.SetStatus("Trainer already connected.");
		}

		_sessionStarted = true;
		_trainerSessionActive = true;
		_fitReplaySessionActive = false;
		_rideState = RideState.Initial;
		_elapsedSeconds = 0;

		_hudController?.SetModeText("Trainer");
		_hudController?.SetStatus("Trainer session started.");

		GD.Print("Trainer ride session started.");
	}

	public void StartFitReplaySession(string fitPath)
	{
		// Deliberately stubbed for the next step.
		// For now this gets the screen flow right without overbuilding replay internals here.
		_sessionStarted = true;
		_trainerSessionActive = false;
		_fitReplaySessionActive = true;
		_rideState = RideState.Initial;
		_elapsedSeconds = 0;

		_hudController?.SetModeText("FIT Replay");
		_hudController?.SetStatus($"FIT loaded: {fitPath}");

		GD.Print($"FIT replay session selected: {fitPath}");
	}

	public async Task ExitToMainMenuAsync()
	{
		_sessionStarted = false;
		_trainerSessionActive = false;
		_fitReplaySessionActive = false;
		_rideState = RideState.Initial;
		_elapsedSeconds = 0;

		if (GameBootstrap.TrainerClient is not null && GameBootstrap.TrainerClient.IsConnected)
		{
			await GameBootstrap.TrainerClient.DisconnectAsync();
		}

		GameBootstrap.ResetSession();

		_hudController?.ResetHud();
		_hudController?.SetStatus("Choose a mode to begin.");

		GD.Print("Returned to main menu.");
	}

	public override async void _Process(double delta)
	{
		if (!_sessionStarted)
		{
			return;
		}

		_elapsedSeconds += delta;

		if (_trainerSessionActive)
		{
			await ProcessTrainerMode(delta);
			return;
		}

		if (_fitReplaySessionActive)
		{
			ProcessFitReplayMode(delta);
		}
	}

	private async Task ProcessTrainerMode(double delta)
	{
		if (GameBootstrap.TrainerClient is null || !GameBootstrap.TrainerClient.IsConnected)
		{
			_hudController?.SetStatus("Trainer not connected.");
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

		_hudController?.UpdateFromRideState(_rideState, telemetry, _elapsedSeconds);
	}

	private void ProcessFitReplayMode(double delta)
	{
		// Temporary fake telemetry so the HUD/session shell works now.
		// Replace this with real FitRideLoader/RideReplayer wiring next.
		var telemetry = new TrainerTelemetry(
			Timestamp: DateTimeOffset.UtcNow,
			PowerWatts: 120,
			CadenceRpm: 82,
			SpeedKph: 28.0,
			HeartRateBpm: null
		);

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

		_hudController?.UpdateFromRideState(_rideState, telemetry, _elapsedSeconds);
	}

	private static double GetGradientForCurrentDistance(double distanceMeters)
	{
		var d = distanceMeters % 3500.0;

		if (d < 500) return 0;
		if (d < 1500) return 3;
		if (d < 2500) return 6;
		return -2;
	}
}
