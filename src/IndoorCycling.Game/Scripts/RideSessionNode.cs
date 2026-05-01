using Godot;
using IndoorCycling.Core.Models;
using IndoorCycling.Core.Simulation;
using IndoorCycling.Trainer.Models;
using IndoorCycling.Trainer.Implementations;
using System;
using System.Threading.Tasks;

public partial class RideSessionNode : Node
{
	private HudController? _hudController;
	private bool _sessionStarted;
	private bool _trainerSessionActive;
	private bool _fitReplaySessionActive;

	private FakeTrainerClient? _fitReplayClient;

	private RideState _rideState = RideState.Initial;
	private double _elapsedSeconds;

	private double _trainerCommandElapsedSeconds;
	private double? _lastSentTrainerGradientPercent;
	private bool _trainerCommandDirty = true;

	private const double TrainerCommandMinIntervalSeconds = 1.0;
	private const double TrainerGradientChangeThresholdPercent = 0.1;

	private double _manualGradientOffsetPercent;
	private const double GradientStepPercent = 0.5;
	private const double MinManualGradientOffsetPercent = -10.0;
	private const double MaxManualGradientOffsetPercent = 10.0;

	public override void _Ready()
	{
		_hudController = GetNodeOrNull<HudController>("../HudController");
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
		_manualGradientOffsetPercent = 0;

		_trainerCommandElapsedSeconds = TrainerCommandMinIntervalSeconds;
		_lastSentTrainerGradientPercent = null;
		_trainerCommandDirty = true;

		_hudController?.SetModeText("Trainer");
		_hudController?.SetStatus("Trainer session started.");

		GD.Print("Trainer ride session started.");
	}

	public async Task StartFitReplaySessionAsync(string fitPath)
	{
		_fitReplayClient = new FakeTrainerClient();
		await _fitReplayClient.ConnectAsync();

		_sessionStarted = true;
		_trainerSessionActive = false;
		_fitReplaySessionActive = true;
		_rideState = RideState.Initial;
		_elapsedSeconds = 0;
		_manualGradientOffsetPercent = 0;

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
		_manualGradientOffsetPercent = 0;

		if (GameBootstrap.TrainerClient is not null && GameBootstrap.TrainerClient.IsConnected)
		{
			await GameBootstrap.TrainerClient.DisconnectAsync();
		}

		if (_fitReplayClient is not null && _fitReplayClient.IsConnected)
		{
			await _fitReplayClient.DisconnectAsync();
		}

		_fitReplayClient = null;

		GameBootstrap.ResetSession();

		_hudController?.ResetHud();
		_hudController?.SetStatus("Choose a mode to begin.");

		GD.Print("Returned to main menu.");
	}

	public void IncreaseManualGradient()
	{
		_manualGradientOffsetPercent = Math.Min(
			MaxManualGradientOffsetPercent,
			_manualGradientOffsetPercent + GradientStepPercent
		);

		_trainerCommandDirty = true;

		_hudController?.SetStatus($"Manual gradient offset: {_manualGradientOffsetPercent:+0.0;-0.0;0.0}%");
	}

	public void DecreaseManualGradient()
	{
		_manualGradientOffsetPercent = Math.Max(
			MinManualGradientOffsetPercent,
			_manualGradientOffsetPercent - GradientStepPercent
		);

		_trainerCommandDirty = true;

		_hudController?.SetStatus($"Manual gradient offset: {_manualGradientOffsetPercent:+0.0;-0.0;0.0}%");
	}

	private double GetCurrentGradient()
	{
		var routeGradient = GetGradientForCurrentDistance(_rideState.DistanceMeters);
		return routeGradient + _manualGradientOffsetPercent;
	}

	private bool ShouldSendTrainerSimulationCommand(double gradientPercent)
	{
		if (_trainerCommandElapsedSeconds < TrainerCommandMinIntervalSeconds)
		{
			return false;
		}

		if (_trainerCommandDirty)
		{
			return true;
		}

		if (_lastSentTrainerGradientPercent is null)
		{
			return true;
		}

		var gradientChange = Math.Abs(gradientPercent - _lastSentTrainerGradientPercent.Value);

		return gradientChange >= TrainerGradientChangeThresholdPercent;
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
			await ProcessFitReplayMode(delta);
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
		//var gradient = GetGradientForCurrentDistance(_rideState.DistanceMeters);
		var gradient = GetCurrentGradient();

		var input = new SimulationTickInput(
			Telemetry: telemetry,
			GradientPercent: gradient,
			DeltaSeconds: delta,
			RiderProfile: GameBootstrap.RiderProfile,
			BikeProfile: GameBootstrap.BikeProfile
		);

		var result = GameBootstrap.SimulationEngine.Tick(_rideState, input);
		_rideState = result.NewState;

		_trainerCommandElapsedSeconds += delta;

		if (ShouldSendTrainerSimulationCommand(gradient))
		{
			GameLogger.Log("trainer_simulation_command_send", new
			{
				gradientPercent = gradient,
				manualGradientOffsetPercent = _manualGradientOffsetPercent,
				elapsedSeconds = _elapsedSeconds
			});

			await GameBootstrap.TrainerClient.SetSimulationAsync(
				new SimulationCommand(
					GradePercent: gradient,
					RiderWeightKg: GameBootstrap.RiderProfile.WeightKg,
					BikeWeightKg: GameBootstrap.BikeProfile.WeightKg
				)
			);

			_lastSentTrainerGradientPercent = gradient;
			_trainerCommandElapsedSeconds = 0;
			_trainerCommandDirty = false;
		}
		_hudController?.UpdateFromRideState(_rideState, telemetry, _elapsedSeconds);
	}

	private async Task ProcessFitReplayMode(double delta)
	{
		if (_fitReplayClient is null || !_fitReplayClient.IsConnected)
		{
			_hudController?.SetStatus("FIT replay client not connected.");
			return;
		}

		//var gradient = GetGradientForCurrentDistance(_rideState.DistanceMeters);
		var gradient = GetCurrentGradient();

		var telemetry = _fitReplayClient.GetLatestTelemetry();

		var input = new SimulationTickInput(
			Telemetry: telemetry,
			GradientPercent: gradient,
			DeltaSeconds: delta,
			RiderProfile: GameBootstrap.RiderProfile,
			BikeProfile: GameBootstrap.BikeProfile
		);

		var result = GameBootstrap.SimulationEngine.Tick(_rideState, input);
		_rideState = result.NewState;

		await _fitReplayClient.SetSimulationAsync(
			new SimulationCommand(
				GradePercent: gradient,
				RiderWeightKg: GameBootstrap.RiderProfile.WeightKg,
				BikeWeightKg: GameBootstrap.BikeProfile.WeightKg
			)
		);

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
