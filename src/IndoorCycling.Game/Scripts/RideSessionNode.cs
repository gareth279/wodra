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

	private RouteDefinition? _currentRoute;
	private bool _sessionStarted;
	private bool _trainerSessionActive;
	private bool _fitReplaySessionActive;

	private FakeTrainerClient? _fitReplayClient;

	private RideState _rideState = RideState.Initial;
	private double _elapsedSeconds;

	private RideFitRecorder? _rideRecorder;

	private string? _lastStoppedRecordingPath;
	private double _recordingSampleElapsedSeconds;
	private const double RecordingSampleIntervalSeconds = 1.0;
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

	public async Task StartTrainerSessionAsync(RouteDefinition? route = null)
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
		_currentRoute = route ?? RouteCatalog.GetById("test-route");
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
		//_hudController?.SetStatus("Trainer session started.");
		_hudController?.SetStatus($"Trainer session started: {_currentRoute?.Name ?? "No route"}");

		GD.Print("Trainer ride session started.");
	}

	private void UpdateRouteHud()
	{
		if (_currentRoute is null || _hudController is null)
			return;

		var sample = _currentRoute.GetSampleAt(_rideState.DistanceMeters);

		_hudController.SetRouteInfo(
			_currentRoute.Name,
			sample.SegmentLabel,
			sample.LoopDistanceMeters,
			_currentRoute.DistanceMeters
		);
	}

	public async Task StartFitReplaySessionAsync(string fitPath)
	{
		_fitReplayClient = new FakeTrainerClient();
		await _fitReplayClient.ConnectAsync();

		_currentRoute = RouteCatalog.GetById("test-route");
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

		if (_rideRecorder is not null)
		{
			_rideRecorder.Stop();
			_rideRecorder = null;
			_recordingSampleElapsedSeconds = 0;
		}
		GameBootstrap.ResetSession();

		_hudController?.ResetHud();
		_hudController?.SetStatus("Choose a mode to begin.");

		GD.Print("Returned to main menu.");
	}

	public async Task ExitToRouteSelectionAsync()
	{
		_sessionStarted = false;
		_trainerSessionActive = false;
		_fitReplaySessionActive = false;
		_rideState = RideState.Initial;
		_elapsedSeconds = 0;
		_manualGradientOffsetPercent = 0;

		if (_rideRecorder is not null)
		{
			_rideRecorder.Stop();
			_rideRecorder = null;
			_recordingSampleElapsedSeconds = 0;
		}

		if (_fitReplayClient is not null && _fitReplayClient.IsConnected)
		{
			await _fitReplayClient.DisconnectAsync();
		}

		_fitReplayClient = null;

		_hudController?.ResetHud();
		_hudController?.SetStatus("Choose a route.");

		GameLogger.Log("returned_to_route_selection", new { });

		GD.Print("Returned to route selection.");
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

	private double GetRouteGradient()
	{
		if (_currentRoute is null)
			return 0;

		return _currentRoute.GetSampleAt(_rideState.DistanceMeters).GradientPercent;
	}

	private double GetCurrentGradient()
	{
		return GetRouteGradient() + _manualGradientOffsetPercent;
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

	public void StartRideRecording()
	{
		if (!_sessionStarted)
		{
			_hudController?.SetStatus("Start a ride before recording.");
			return;
		}

		_rideRecorder ??= new RideFitRecorder();

		var path = _rideRecorder.StartOrResume();
		_recordingSampleElapsedSeconds = RecordingSampleIntervalSeconds;

		_hudController?.SetStatus($"Recording: {path}");
	}

	public void PauseRideRecording()
	{
		if (_rideRecorder is null)
		{
			_hudController?.SetStatus("No active recording to pause.");
			return;
		}

		_rideRecorder.Pause();
		_hudController?.SetStatus("Recording paused.");
	}

	public void StopRideRecording()
	{
		if (_rideRecorder is null)
		{
			_hudController?.SetStatus("No active recording to stop.");
			return;
		}

		var path = _rideRecorder.Stop();
		_lastStoppedRecordingPath = path;

		_rideRecorder = null;
		_recordingSampleElapsedSeconds = 0;

		_hudController?.SetStatus($"Recording saved: {path}");
	}

	public void SaveLastRecordingToStrava()
	{
		if (string.IsNullOrWhiteSpace(_lastStoppedRecordingPath))
		{
			_hudController?.SetStatus("No stopped recording to upload.");
			return;
		}

		if (!System.IO.File.Exists(_lastStoppedRecordingPath))
		{
			_hudController?.SetStatus("Recorded FIT file not found.");
			return;
		}

		GameLogger.Log("strava_upload_requested", new
		{
			filePath = _lastStoppedRecordingPath
		});

		_hudController?.SetStatus($"Strava upload not wired yet: {_lastStoppedRecordingPath}");
	}

	private void RecordRideSampleIfNeeded(double delta, TrainerTelemetry telemetry, double gradient)
	{
		if (_rideRecorder is null || !_rideRecorder.IsRecording)
			return;

		_recordingSampleElapsedSeconds += delta;

		if (_recordingSampleElapsedSeconds < RecordingSampleIntervalSeconds)
			return;

		_recordingSampleElapsedSeconds = 0;

		var routeSample = _currentRoute?.GetSampleAt(_rideState.DistanceMeters);

		_rideRecorder.RecordSample(_rideState, telemetry, gradient, routeSample);
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
			await SendTrainerSimulationCommandAsync(gradient);
		}
		RecordRideSampleIfNeeded(delta, telemetry, gradient);
		UpdateRouteHud();
		_hudController?.UpdateFromRideState(_rideState, telemetry, _elapsedSeconds);
	}

	private async Task SendTrainerSimulationCommandAsync(double gradient)
	{
		GameLogger.Log("trainer_simulation_command_send", new
		{
			gradientPercent = gradient,
			manualGradientOffsetPercent = _manualGradientOffsetPercent,
			elapsedSeconds = _elapsedSeconds
		});

		try
		{
			await GameBootstrap.TrainerClient!.SetSimulationAsync(
				new SimulationCommand(
					GradePercent: gradient,
					RiderWeightKg: GameBootstrap.RiderProfile.WeightKg,
					BikeWeightKg: GameBootstrap.BikeProfile.WeightKg
				)
			);

			_lastSentTrainerGradientPercent = gradient;
			_trainerCommandDirty = false;
		}
		catch (Exception ex)
		{
			GameLogger.Log("trainer_simulation_command_failed", new
			{
				gradientPercent = gradient,
				message = ex.Message,
				exceptionType = ex.GetType().FullName
			});

			_hudController?.SetStatus($"Trainer command failed: {ex.Message}");

			// Keep dirty=true so it retries later, but not every frame.
			_trainerCommandDirty = true;
		}
		finally
		{
			_trainerCommandElapsedSeconds = 0;
		}
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

		RecordRideSampleIfNeeded(delta, telemetry, gradient);
		_hudController?.UpdateFromRideState(_rideState, telemetry, _elapsedSeconds);
	}

}
