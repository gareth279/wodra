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
	private readonly RidePauseGate _pauseGate = new();
	private readonly DebugRideDiagnostics _debugDiagnostics = new();
	private RideWorldView? _rideWorldView;
	private TrainerTelemetry? _lastTelemetry;
	private double? _lastRequestedTrainerGradientPercent;
	private DateTimeOffset? _lastTrainerCommandAtUtc;
	private int _lastLoggedLapNumber = 1;
	// Read-only rendering input. This remains the only authority for ride progress.
	public RideState CurrentRideState => _rideState;
	public RouteDefinition? CurrentRoute => _currentRoute;
	public bool IsRideActive => _sessionStarted;
	public bool IsRidePaused => _pauseGate.IsPaused;
	private double _elapsedSeconds;

	private RideFitRecorder? _rideRecorder;

	private string? _lastStoppedRecordingPath;
	public string? LastStoppedRecordingPath => _lastStoppedRecordingPath;
	private Task _trainerCommandTask = Task.CompletedTask;
	private bool _quitting;
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
		GetTree().AutoAcceptQuit = false;
		_hudController = GetNodeOrNull<HudController>("../HudController");
		_rideWorldView = GetNodeOrNull<RideWorldView>("../RideWorldView");
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
		if (GameBootstrap.DebugModeEnabled)
			GameLogger.Log("trainer_connected", new { mode = DebugTrainerMode, connected = GameBootstrap.TrainerClient.IsConnected });
		_currentRoute = route ?? RouteCatalog.GetById("test-route");
		_sessionStarted = true;
		_trainerSessionActive = true;
		_fitReplaySessionActive = false;
		_rideState = RideState.Initial;
		_pauseGate.Resume();
		_hudController?.SetRidePaused(false);
		_elapsedSeconds = 0;
		_manualGradientOffsetPercent = 0;

		_trainerCommandElapsedSeconds = TrainerCommandMinIntervalSeconds;
		_lastSentTrainerGradientPercent = null;
		_trainerCommandDirty = true;
		BeginDebugRide("trainer");

		_hudController?.SetModeText("Trainer");
		//_hudController?.SetStatus("Trainer session started.");
		_hudController?.SetStatus($"Trainer session started: {_currentRoute?.Name ?? "No route"}");
		if (_debugDiagnostics.Enabled)
			GameLogger.Log("ride_started", new { mode = DebugTrainerMode, routeId = _currentRoute?.Id, routeName = _currentRoute?.Name });

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
		_pauseGate.Resume();
		_hudController?.SetRidePaused(false);
		_elapsedSeconds = 0;
		_manualGradientOffsetPercent = 0;
		BeginDebugRide("fit_replay");

		_hudController?.SetModeText("FIT Replay");
		_hudController?.SetStatus($"FIT loaded: {fitPath}");
		if (_debugDiagnostics.Enabled) GameLogger.Log("ride_started", new { mode = "fit_replay", fitPath });

		GD.Print($"FIT replay session selected: {fitPath}");
	}

	public async Task ExitToMainMenuAsync()
	{
		await EndRideAsync(disconnectTrainer: true);
		GameBootstrap.ResetSession();
		_hudController?.ResetHud();
		_hudController?.SetStatus("Choose a mode to begin.");
	}

	public async Task ExitToRouteSelectionAsync()
	{
		// Keep the paired trainer ready for another route.
		await EndRideAsync(disconnectTrainer: false);
		_hudController?.ResetHud();
		_hudController?.SetStatus("Choose a route.");
		GameLogger.Log("returned_to_route_selection", new { });
	}

	private async Task EndRideAsync(bool disconnectTrainer)
	{
		_sessionStarted = false;
		try
		{
			FinaliseRecording();
		}
		finally
		{
			_trainerSessionActive = false;
			_fitReplaySessionActive = false;
			// Finish the existing write before disposing its BLE connection.
			await _trainerCommandTask;
			try
			{
				if (disconnectTrainer && GameBootstrap.TrainerClient is not null)
				{
					await GameBootstrap.TrainerClient.DisconnectAsync();
					if (_debugDiagnostics.Enabled) GameLogger.Log("trainer_disconnected", new { mode = DebugTrainerMode });
				}
			}
			finally
			{
				if (_fitReplayClient is not null)
					await _fitReplayClient.DisconnectAsync();
				_fitReplayClient = null;
				_rideState = RideState.Initial;
				_pauseGate.Resume();
				_hudController?.SetRidePaused(false);
				_elapsedSeconds = 0;
				_manualGradientOffsetPercent = 0;
			}
		}
	}

	public async Task QuitAsync()
	{
		if (_quitting) return;
		_quitting = true;
		try
		{
			await ExitToMainMenuAsync();
			GetTree().Quit();
		}
		catch (Exception ex)
		{
			_quitting = false;
			GameLogger.Log("quit_cleanup_failed", new { ex.Message });
			_hudController?.SetStatus($"Could not finish session: {ex.Message}");
		}
	}

	public override void _Notification(int what)
	{
		if (what == NotificationWMCloseRequest)
			_ = QuitAsync();
	}

	private void RecordCurrentSample()
	{
		if (_rideRecorder?.IsRecording != true) return;
		var trainer = _trainerSessionActive ? GameBootstrap.TrainerClient : _fitReplayClient;
		if (trainer is null) return;
		_rideRecorder.RecordSample(_rideState, trainer.GetLatestTelemetry(),
			GetCurrentGradient(), _currentRoute?.GetSampleAt(_rideState.DistanceMeters));
	}

	private string? FinaliseRecording()
	{
		if (_rideRecorder is null) return _lastStoppedRecordingPath;
		try
		{
			RecordCurrentSample();
		}
		finally
		{
			_lastStoppedRecordingPath = _rideRecorder.Stop();
			_rideRecorder = null;
			_recordingSampleElapsedSeconds = 0;
		}
		return _lastStoppedRecordingPath;
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

	private string DebugTrainerMode => GameBootstrap.Mode switch
	{
		GameBootstrap.SessionMode.FitReplay => "fit_replay",
		GameBootstrap.SessionMode.Trainer when GameBootstrap.TrainerClient is FakeTrainerClient => "dummy",
		GameBootstrap.SessionMode.Trainer => "real",
		_ => "unknown"
	};

	private void BeginDebugRide(string mode)
	{
		_debugDiagnostics.Begin(GameBootstrap.DebugModeEnabled);
		_lastLoggedLapNumber = 1;
		_lastTelemetry = null;
		_lastRequestedTrainerGradientPercent = null;
		_lastSentTrainerGradientPercent = null;
		_lastTrainerCommandAtUtc = null;
		_hudController?.SetDebugMode(GameBootstrap.DebugModeEnabled);
		if (_debugDiagnostics.Enabled)
			GameLogger.Log("debug_session_started", new { debugSessionId = _debugDiagnostics.SessionId, mode });
	}

	private void LogLapWraps(double previousDistance, double newDistance)
	{
		if (!_debugDiagnostics.Enabled || _currentRoute is null || _currentRoute.DistanceMeters <= 0) return;
		var lap = (int)Math.Floor(Math.Max(0, newDistance) / _currentRoute.DistanceMeters) + 1;
		while (_lastLoggedLapNumber < lap)
		{
			_lastLoggedLapNumber++;
			GameLogger.Log("route_lap_wrapped", new
			{
				debugSessionId = _debugDiagnostics.SessionId,
				completedLap = _lastLoggedLapNumber - 1,
				totalRideDistanceMeters = newDistance,
				routeLengthMeters = _currentRoute.DistanceMeters
			});
		}
	}

	private RideDebugSnapshot BuildDebugSnapshot(int? marker = null)
	{
		var route = _currentRoute;
		var routeSample = route?.GetSampleAt(_rideState.DistanceMeters);
		var world = _rideWorldView?.GetDiagnosticState();
		var telemetry = _lastTelemetry;
		var now = DateTimeOffset.UtcNow;
		var trainer = _trainerSessionActive ? GameBootstrap.TrainerClient : _fitReplayClient;
		var recordingState = _rideRecorder is null ? "inactive" : _rideRecorder.IsRecording ? "recording" : _rideRecorder.IsPaused ? "paused" : "stopped";
		var pose = world?.Pose ?? default;
		var fitState = _fitReplaySessionActive ? "fit_replay" : DebugTrainerMode;
		var data = new RideDebugSnapshot
		{
			marker = marker,
			trainerMode = fitState,
			rideState = _pauseGate.IsPaused ? "paused" : "running",
			ridePaused = _pauseGate.IsPaused,
			fitRecordingState = recordingState,
			elapsedRidingSeconds = _elapsedSeconds,
			trainerConnected = trainer?.IsConnected,
			rawTrainerPowerWatts = telemetry?.PowerWatts,
			rawTrainerCadenceRpm = telemetry?.CadenceRpm,
			rawTrainerSpeedKph = telemetry?.SpeedKph,
			telemetryTimestamp = telemetry?.Timestamp,
			telemetryAgeSeconds = telemetry is null ? null : Math.Max(0, (now - telemetry.Timestamp).TotalSeconds),
			virtualSpeedKph = _rideState.VirtualSpeedKph,
			totalRideDistanceMeters = _rideState.DistanceMeters,
			currentPowerWatts = _rideState.CurrentPowerWatts,
			totalSystemMassKg = GameBootstrap.RiderProfile.WeightKg + GameBootstrap.BikeProfile.WeightKg,
			routeGradientPercent = routeSample?.GradientPercent ?? 0,
			manualGradientOffsetPercent = _manualGradientOffsetPercent,
			routeId = route?.Id,
			routeName = route?.Name,
			routeLengthMeters = route?.DistanceMeters ?? 0,
			routeSegment = routeSample?.SegmentLabel,
			routeLatitude = routeSample?.Latitude,
			routeLongitude = routeSample?.Longitude,
			routeElevationMeters = routeSample?.ElevationMeters,
			rawRouteElevationMeters = routeSample?.RawElevationMeters,
			visualRelativeElevationMeters = pose.Position.Y,
			nearbyLandmarkCount = world?.NearbyLandmarkCount ?? 0,
			nearestLandmarkName = world?.NearestLandmarkName,
			nearestLandmarkRouteDistanceMeters = world?.NearestLandmarkRouteDistanceMeters,
			nearestLandmarkWorldPosition = world?.NearestLandmarkWorldPosition is { } landmarkWorld
				? new DebugVector3(landmarkWorld.X, landmarkWorld.Y, landmarkWorld.Z) : null,
			nearestLandmarkDistanceMeters = world?.NearestLandmarkDistanceMeters,
			buildingsLoaded = world?.BuildingsLoaded ?? 0,
			visibleBuildingChunks = world?.VisibleBuildingChunks ?? 0,
			vegetationAreas = world?.VegetationAreas ?? 0,
		treesLoaded = world?.TreesLoaded ?? 0,
		visibleVegetationChunks = world?.VisibleVegetationChunks ?? 0,
			streetFurnitureLoaded = world?.StreetFurnitureLoaded ?? 0,
			nearbyStreetFurniture = world?.NearbyStreetFurniture ?? 0,
			visualSampleBefore = pose.SampleIndexBefore,
			visualSampleAfter = pose.SampleIndexAfter,
			visualInterpolation = pose.Interpolation,
			visualRoutePosition = new DebugVector3(pose.Position.X, pose.Position.Y, pose.Position.Z),
			visualTangent = new DebugVector3(pose.Forward.X, pose.Forward.Y, pose.Forward.Z),
			riderWorldPosition = world is null ? default : ToDebugVector(world.Value.RiderWorldPosition),
			riderForward = world is null ? default : ToDebugVector(world.Value.RiderForward),
			riderRotation = world is null ? default : ToDebugVector(world.Value.RiderRotation),
			cameraWorldPosition = world is null ? null : ToDebugVector(world.Value.CameraWorldPosition),
			requestedTrainerGradientPercent = _lastRequestedTrainerGradientPercent,
			successfulTrainerGradientPercent = _lastSentTrainerGradientPercent,
			secondsSinceTrainerCommand = _lastTrainerCommandAtUtc is null ? null : Math.Max(0, (now - _lastTrainerCommandAtUtc.Value).TotalSeconds),
			trainerCommandInFlight = !_trainerCommandTask.IsCompleted
		};
		return _debugDiagnostics.CreateSnapshot(data, now, marker);
	}

	private static DebugVector3 ToDebugVector(Godot.Vector3 v) => new(v.X, v.Y, v.Z);

	private void WriteDebugSnapshot()
	{
		if (!_debugDiagnostics.Enabled) return;
		var snapshot = BuildDebugSnapshot();
		GameLogger.Log("debug_ride_snapshot", snapshot);
		_hudController?.UpdateDebugOverlay(snapshot);
	}

	public void WriteDebugMarker()
	{
		if (!_debugDiagnostics.Enabled || !_sessionStarted) return;
		var marker = _debugDiagnostics.NextMarkerNumber();
		var snapshot = BuildDebugSnapshot(marker);
		GameLogger.Log("debug_marker", snapshot);
		GD.Print($"DEBUG MARKER {marker} @ ride distance {_rideState.DistanceMeters:F1} m");
		_hudController?.UpdateDebugOverlay(snapshot);
	}

	public void JumpToRouteDistance(string distanceText)
	{
		if (!_debugDiagnostics.Enabled || !_sessionStarted) return;
		if (!double.TryParse(distanceText, System.Globalization.NumberStyles.Float,
			System.Globalization.CultureInfo.InvariantCulture, out var distance))
		{
			_hudController?.SetStatus("Enter a valid route distance in metres (for example 5830).");
			return;
		}
		var before = _rideState.DistanceMeters;
		var hasActiveRecording = _rideRecorder is not null;
		if (!_debugDiagnostics.TryApplyDistanceJump(_rideState, distance,
			hasActiveRecording, out var jumped))
		{
			_hudController?.SetStatus(hasActiveRecording
				? "Stop FIT recording before jumping route distance."
				: "Enter a non-negative distance to jump.");
			return;
		}
		_rideState = jumped;
		_recordingSampleElapsedSeconds = 0;
		if (_currentRoute?.DistanceMeters > 0)
			_lastLoggedLapNumber = (int)Math.Floor(distance / _currentRoute.DistanceMeters) + 1;
		UpdateRouteHud();
		var snapshot = BuildDebugSnapshot();
		GameLogger.Log("debug_distance_jump", new { fromDistanceMeters = before, toDistanceMeters = distance, snapshot });
		_hudController?.SetStatus($"Debug jump: {before:F1} m → {distance:F1} m; virtual speed reset.");
		_hudController?.UpdateDebugOverlay(snapshot);
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

		var created = _rideRecorder is null;
		_rideRecorder ??= new RideFitRecorder();
		var path = _rideRecorder.FilePath;
		if (created)
		{
			path = _rideRecorder.StartOrResume(_rideState.DistanceMeters,
				_currentRoute?.GetSampleAt(_rideState.DistanceMeters).ElevationMeters);
			if (_pauseGate.IsPaused) _rideRecorder.Pause();
		}
		else if (!_pauseGate.IsPaused)
		{
			path = _rideRecorder.StartOrResume(_rideState.DistanceMeters,
				_currentRoute?.GetSampleAt(_rideState.DistanceMeters).ElevationMeters);
		}
		RecordCurrentSample();
		_recordingSampleElapsedSeconds = 0;

		_hudController?.SetStatus($"Recording: {path}");
	}

	public void ToggleRidePause()
	{
		if (!_sessionStarted) return;
		if (_pauseGate.IsPaused)
		{
			ResumeRide();
			return;
		}
		if (_rideRecorder?.IsRecording == true)
		{
			RecordCurrentSample();
			_rideRecorder.Pause();
		}
		_rideState = _pauseGate.Pause(_rideState);
		if (_debugDiagnostics.Enabled) GameLogger.Log("ride_paused", new { distanceMeters = _rideState.DistanceMeters, elapsedSeconds = _elapsedSeconds });
		_hudController?.SetRidePaused(true);
		_hudController?.SetStatus("Ride paused.");
	}

	public void ResumeRide()
	{
		if (!_sessionStarted || !_pauseGate.IsPaused) return;
		_pauseGate.Resume();
		_hudController?.SetRidePaused(false);
		if (_rideRecorder?.IsPaused == true)
		{
			_rideRecorder.StartOrResume(_rideState.DistanceMeters,
				_currentRoute?.GetSampleAt(_rideState.DistanceMeters).ElevationMeters);
			RecordCurrentSample();
		}
		if (_debugDiagnostics.Enabled) GameLogger.Log("ride_resumed", new { distanceMeters = _rideState.DistanceMeters, elapsedSeconds = _elapsedSeconds });
		_recordingSampleElapsedSeconds = 0;
		_hudController?.SetStatus("Ride resumed.");
	}

	public void StopRideRecording()
	{
		if (_rideRecorder is null)
		{
			_hudController?.SetStatus("No active recording to stop.");
			return;
		}

		var path = FinaliseRecording();

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

	public override void _Process(double delta)
	{
		if (!_sessionStarted)
		{
			return;
		}

		if (!_pauseGate.IsPaused) _elapsedSeconds += delta;

		if (_trainerSessionActive) ProcessTrainerMode(delta);
		else if (_fitReplaySessionActive) ProcessFitReplayMode(delta);

		if (_debugDiagnostics.Enabled && _debugDiagnostics.SnapshotDue(delta)) WriteDebugSnapshot();
	}

	private void ProcessTrainerMode(double delta)
	{
		if (GameBootstrap.TrainerClient is null || !GameBootstrap.TrainerClient.IsConnected)
		{
			_hudController?.SetStatus("Trainer not connected.");
			return;
		}

		var telemetry = GameBootstrap.TrainerClient.GetLatestTelemetry();
		_lastTelemetry = telemetry;
		if (_pauseGate.IsPaused)
		{
			_rideState = _pauseGate.Tick(GameBootstrap.SimulationEngine, _rideState,
				new SimulationTickInput(telemetry, GetCurrentGradient(), 0,
					GameBootstrap.RiderProfile, GameBootstrap.BikeProfile));
			UpdateRouteHud();
			_hudController?.UpdateFromRideState(_rideState, telemetry, _elapsedSeconds);
			return;
		}
		//var gradient = GetGradientForCurrentDistance(_rideState.DistanceMeters);
		var gradient = GetCurrentGradient();

		var input = new SimulationTickInput(
			Telemetry: telemetry,
			GradientPercent: gradient,
			DeltaSeconds: delta,
			RiderProfile: GameBootstrap.RiderProfile,
			BikeProfile: GameBootstrap.BikeProfile
		);

		var previousDistance = _rideState.DistanceMeters;
		_rideState = _pauseGate.Tick(GameBootstrap.SimulationEngine, _rideState, input);
		LogLapWraps(previousDistance, _rideState.DistanceMeters);

		_trainerCommandElapsedSeconds += delta;

		if (_trainerCommandTask.IsCompleted && ShouldSendTrainerSimulationCommand(gradient))
		{
			_trainerCommandTask = SendTrainerSimulationCommandAsync(gradient);
		}
		RecordRideSampleIfNeeded(delta, telemetry, gradient);
		UpdateRouteHud();
		_hudController?.UpdateFromRideState(_rideState, telemetry, _elapsedSeconds);
	}

	private async Task SendTrainerSimulationCommandAsync(double gradient)
	{
		_trainerCommandDirty = false;
		_lastRequestedTrainerGradientPercent = gradient;
		_lastTrainerCommandAtUtc = DateTimeOffset.UtcNow;
		GameLogger.Log("trainer_simulation_command_send", new
		{
			gradientPercent = gradient,
			manualGradientOffsetPercent = _manualGradientOffsetPercent,
			elapsedSeconds = _elapsedSeconds
		});
		if (_debugDiagnostics.Enabled)
			GameLogger.Log("trainer_simulation_command_requested", new { gradientPercent = gradient });

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
			if (_debugDiagnostics.Enabled) GameLogger.Log("trainer_simulation_command_succeeded", new { gradientPercent = gradient });
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

	private void ProcessFitReplayMode(double delta)
	{
		if (_fitReplayClient is null || !_fitReplayClient.IsConnected)
		{
			_hudController?.SetStatus("FIT replay client not connected.");
			return;
		}

		//var gradient = GetGradientForCurrentDistance(_rideState.DistanceMeters);
		var gradient = GetCurrentGradient();

		var telemetry = _fitReplayClient.GetLatestTelemetry();
		_lastTelemetry = telemetry;
		if (_pauseGate.IsPaused)
		{
			_rideState = _pauseGate.Tick(GameBootstrap.SimulationEngine, _rideState,
				new SimulationTickInput(telemetry, gradient, 0,
					GameBootstrap.RiderProfile, GameBootstrap.BikeProfile));
			_hudController?.UpdateFromRideState(_rideState, telemetry, _elapsedSeconds);
			return;
		}

		var input = new SimulationTickInput(
			Telemetry: telemetry,
			GradientPercent: gradient,
			DeltaSeconds: delta,
			RiderProfile: GameBootstrap.RiderProfile,
			BikeProfile: GameBootstrap.BikeProfile
		);

		var previousDistance = _rideState.DistanceMeters;
		_rideState = _pauseGate.Tick(GameBootstrap.SimulationEngine, _rideState, input);
		LogLapWraps(previousDistance, _rideState.DistanceMeters);

		_ = _fitReplayClient.SetSimulationAsync(
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
