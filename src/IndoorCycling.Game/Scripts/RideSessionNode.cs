using Godot;
using IndoorCycling.Core.Models;
using IndoorCycling.Core.Simulation;
using IndoorCycling.Trainer.Models;
using IndoorCycling.Trainer.Implementations;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Collections.Generic;
using IndoorCycling.Game.Multiplayer;
using Wodra.Multiplayer.Protocol;

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
	public int CurrentAnimationCadenceRpm => _lastTelemetry?.IsFreshAt(DateTimeOffset.UtcNow) == true
		? _rideState.CurrentCadenceRpm : 0;
	public RouteDefinition? CurrentRoute => _currentRoute;
	public bool IsRideActive => _sessionStarted;
	public bool IsRidePaused => _pauseGate.IsPaused;
	private double _elapsedSeconds;
	private Guid? _currentActivityId;
	private Guid? _currentRiderProfileId;
	private DateTimeOffset _activityStartedAtUtc;
	private string? _currentActivityFitPath;
	private readonly RideActivityAccumulator _activityAccumulator = new();
	public Guid? CurrentActivityId => _currentActivityId;
	public Guid? LastCompletedActivityId { get; private set; }
	public RideActivityDraft? LastCompletedActivityDraft { get; private set; }
	public string? LastRecordingError { get; private set; }

	private RideFitRecorder? _rideRecorder;
	private double? _pendingDebugJumpDistanceMeters;
	private bool _debugJumpFinalizationFailed;
	private Task? _endRideTask;
	public GhostRiderStateSource? GhostRider { get; private set; }
	public Guid? GhostActivityId { get; private set; }
	public void SetPendingGhost(GhostRiderStateSource source, Guid activityId) { GhostRider = source; GhostActivityId = activityId; }
	public void ClearPendingGhost() { GhostRider = null; GhostActivityId = null; _hudController?.SetGhostIndicator(null); }
	public void UpdateGhostIndicator(double playerTotalDistanceMeters)
	{
		if (GhostRider is null || GhostRider.Sample is null) { _hudController?.SetGhostIndicator(null); return; }
		var relative = GhostRider.Sample.DistanceMeters - playerTotalDistanceMeters;
		_hudController?.SetGhostIndicator(GhostRider.Finished
			? $"Ghost: Finished {relative:+0;-0;0} m"
			: $"Ghost: {relative:+0;-0;0} m");
	}

	private string? _lastStoppedRecordingPath;
	public string? LastStoppedRecordingPath => _lastStoppedRecordingPath;
	private Task _trainerCommandTask = Task.CompletedTask;
	private bool _quitting;
	private double _recordingSampleElapsedSeconds;
	private const double RecordingSampleIntervalSeconds = 1.0;
	private double _trainerCommandElapsedSeconds;
	private double? _lastSentTrainerGradientPercent;
	private bool _trainerCommandDirty = true;
	private MultiplayerClient? _multiplayerClient;
	private readonly Dictionary<string, MultiplayerRemoteRider> _remoteRiders = new(StringComparer.Ordinal);
	private double _multiplayerPublishElapsed;
	private long _multiplayerSequence;
	public string MultiplayerStatus => _multiplayerClient?.Status ?? "Disconnected";
	public bool IsMultiplayerConnected => _multiplayerClient?.IsConnected == true;
	public bool HasMultiplayerClient => _multiplayerClient?.HasClient == true;
	public IReadOnlyList<MultiplayerRemoteRider> MultiplayerRiders => _remoteRiders.Values.ToArray();

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
		_currentRoute = route ?? RouteCatalog.Routes[0];
		_hudController?.SetDummyTrainerControls(GameBootstrap.TrainerClient as FakeTrainerClient);
		if (GhostRider is not null && GhostRider.RouteId != _currentRoute?.Id)
		{ GhostRider = null; GhostActivityId = null; }
		_currentActivityId = Guid.NewGuid();
		_currentRiderProfileId = null;
		try
		{
			var rider = await GameBootstrap.GetPersistentRiderAsync();
			_currentRiderProfileId = rider.Id;
		}
		catch (Exception ex)
		{
			GameLogger.Log("ride_history_profile_unavailable", new { message = ex.Message });
			GD.PushWarning($"Ride history is unavailable; this ride will still run: {ex.Message}");
		}
		_activityStartedAtUtc = DateTimeOffset.UtcNow;
		_currentActivityFitPath = null;
		_lastStoppedRecordingPath = null;
		LastRecordingError = null;
		_activityAccumulator.Reset();
		LastCompletedActivityId = null;
		LastCompletedActivityDraft = null;
		_sessionStarted = true;
		_trainerSessionActive = true;
		_fitReplaySessionActive = false;
		_rideState = RideState.Initial;
		_hudController?.SetRecordingState(false, false);
		_pauseGate.Resume();
		_hudController?.SetRidePaused(false);
		_elapsedSeconds = 0;
		_manualGradientOffsetPercent = 0;

		_trainerCommandElapsedSeconds = TrainerCommandMinIntervalSeconds;
		_lastSentTrainerGradientPercent = null;
		_trainerCommandDirty = true;
		BeginDebugRide("trainer");
		StartAutomaticRideRecording();

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
		_hudController?.SetDummyTrainerControls(null);
		_fitReplayClient = new FakeTrainerClient();
		await _fitReplayClient.ConnectAsync();

		_currentRoute = RouteCatalog.Routes[0];
		_currentActivityId = null;
		_currentRiderProfileId = null;
		_sessionStarted = true;
		_trainerSessionActive = false;
		_fitReplaySessionActive = true;
		_rideState = RideState.Initial;
		_hudController?.SetRecordingState(false, false);
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

	/// <summary>Stops a ride cleanly when the required route road cannot be constructed.</summary>
	public async Task StopRideAfterWorldLoadFailureAsync(string error)
	{
		_pauseGate.Pause(_rideState);
		_hudController?.SetRidePaused(true);
		await EndRideAsync(disconnectTrainer: false);
		var recordingStatus = LastRecordingError is null
			? " The active FIT recording was finalized when available."
			: $" FIT finalization also failed: {LastRecordingError}";
		_hudController?.SetStatus($"{error} The ride has been stopped.{recordingStatus} Choose another route or restart to retry.");
	}

	private async Task EndRideAsync(bool disconnectTrainer)
	{
		if (_endRideTask is { } pending)
		{
			await pending;
			return;
		}
		if (!_sessionStarted && _rideRecorder is null) return;
		var work = EndRideCoreAsync(disconnectTrainer);
		_endRideTask = work;
		try { await work; }
		finally
		{
			if (ReferenceEquals(_endRideTask, work)) _endRideTask = null;
		}
	}

	private async Task EndRideCoreAsync(bool disconnectTrainer)
	{
		var wasActiveTrainerRide = _sessionStarted && _trainerSessionActive;
		_sessionStarted = false;
		string? fitPath = null;
		try
		{
			try { fitPath = FinaliseRecording(); }
			catch (Exception ex)
			{
				LastRecordingError = ex.Message;
				GameLogger.Log("ride_finalise_failed", new { message = ex.Message });
				GD.PushWarning($"FIT file could not be finalized; the ride will still be saved to ride history: {ex.Message}");
			}
			if (wasActiveTrainerRide && _currentActivityId is { } activityId && _currentRoute is { } route)
			{
				if (_hudController is not null) await _hudController.FlushScreenshotCapturesAsync();
				var draft = _activityAccumulator.CreateDraft(activityId, _currentRiderProfileId ?? Guid.Empty,
					route.Id, route.Name, _activityStartedAtUtc, _rideState.DistanceMeters, route.DistanceMeters,
					_rideState.ElevationGainMeters,
					fitPath is not null && File.Exists(fitPath) ? fitPath : _currentActivityFitPath is not null && File.Exists(_currentActivityFitPath) ? _currentActivityFitPath : null);
				LastCompletedActivityDraft = draft;
				LastCompletedActivityId = activityId;
				if (_currentRiderProfileId is { } riderId && GameBootstrap.RideHistory is { } history)
				{
					var media = (_hudController?.CapturedScreenshots ?? Array.Empty<CapturedScreenshot>())
						.Where(s => s.ActivityId == activityId && File.Exists(s.ImagePath))
						.Select(s => new RideActivityMedia(Guid.NewGuid(), activityId, s.ImagePath, s.SidecarPath,
							s.CapturedAt.ToUniversalTime(), s.Mode.ToString(), s.MarkerNumber)).ToArray();
					try { await history.SaveCompletedActivityAsync(draft, media); }
					catch (Exception ex) { GameLogger.Log("ride_history_save_failed", new { activityId, message = ex.Message }); GD.PushWarning($"Ride finished, but ride history could not be saved: {ex.Message}"); }
				}
			}
		}
		catch (Exception ex)
		{
			GameLogger.Log("ride_history_finalize_failed", new { message = ex.Message });
			GD.PushWarning($"Ride ended, but its history summary could not be completed: {ex.Message}");
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
				await LeaveMultiplayerAsync();
				if (_fitReplayClient is not null)
					await _fitReplayClient.DisconnectAsync();
				_fitReplayClient = null;
				_hudController?.SetDummyTrainerControls(null);
				_rideState = RideState.Initial;
				_pauseGate.Resume();
				_hudController?.SetRidePaused(false);
				_elapsedSeconds = 0;
				_manualGradientOffsetPercent = 0;
				_currentActivityId = null;
				_currentRiderProfileId = null;
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

	public override void _ExitTree()
	{
		if (_rideRecorder is null) return;
		try { FinaliseRecording(); }
		catch (Exception ex)
		{
			GameLogger.Log("ride_exit_recording_finalize_failed", new { message = ex.Message });
			GD.PushWarning($"Wodra could not finalize the active FIT recording during exit: {ex.Message}");
		}
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
		if (_rideRecorder is null) return null;
		var recorder = _rideRecorder;
		Exception? finalSampleError = null;
		try
		{
			RecordCurrentSample();
		}
		catch (Exception ex) { finalSampleError = ex; }
		_rideRecorder = null;
		try
		{
			_lastStoppedRecordingPath = recorder.Stop();
			if (_currentActivityId is not null && _lastStoppedRecordingPath is not null && File.Exists(_lastStoppedRecordingPath))
				_currentActivityFitPath = _lastStoppedRecordingPath;
			if (finalSampleError is not null)
				LastRecordingError = $"The final FIT sample could not be recorded: {finalSampleError.Message}";
		}
		finally
		{
			_recordingSampleElapsedSeconds = 0;
			_hudController?.SetRecordingState(false, false);
		}
		return _lastStoppedRecordingPath;
	}

	private void StartAutomaticRideRecording()
	{
		try
		{
			_rideRecorder = new RideFitRecorder();
			_rideRecorder.StartOrResume(_rideState.DistanceMeters,
				_currentRoute?.GetSampleAt(_rideState.DistanceMeters).ElevationMeters);
			RecordCurrentSample();
			_recordingSampleElapsedSeconds = 0;
			_hudController?.SetRecordingState(true, false);
		}
		catch (Exception ex)
		{
			try { _rideRecorder?.Stop(); }
			catch (Exception cleanupException) { GameLogger.Log("ride_recording_start_cleanup_failed", new { message = cleanupException.Message }); }
			_rideRecorder = null;
			_hudController?.SetRecordingState(false, false);
			LastRecordingError = ex.Message;
			GameLogger.Log("ride_recording_auto_start_failed", new { message = ex.Message });
			GD.PushWarning($"Ride started, but FIT recording could not start: {ex.Message}");
		}
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

	public void AdjustDummyPower(double increment)
	{
		if (GameBootstrap.TrainerClient is not FakeTrainerClient { HasLiveDummyControls: true } dummy) return;
		var settings = GameBootstrap.Settings.DummyTrainer;
		if (settings.Mode == "W/kg") settings.WattsPerKg = Math.Clamp(settings.WattsPerKg + increment, 0, 12);
		else settings.TargetWatts = Math.Clamp(settings.TargetWatts + (int)increment, 0, 1500);
		settings.Validate(); dummy.SetDummyOutput(settings.CalculateWatts(GameBootstrap.RiderProfile.BodyWeightKg), settings.CadenceRpm);
		GameBootstrap.Settings.Save();
		_hudController?.SetDummyTrainerControls(dummy);
	}

	public void AdjustDummyCadence(int increment)
	{
		if (GameBootstrap.TrainerClient is not FakeTrainerClient { HasLiveDummyControls: true } dummy) return;
		var settings = GameBootstrap.Settings.DummyTrainer; settings.CadenceRpm = Math.Clamp(settings.CadenceRpm + increment, 0, 160);
		dummy.SetDummyOutput(settings.CalculateWatts(GameBootstrap.RiderProfile.BodyWeightKg), settings.CadenceRpm);
		GameBootstrap.Settings.Save(); _hudController?.SetDummyTrainerControls(dummy);
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
		_debugJumpFinalizationFailed = false;
		_pendingDebugJumpDistanceMeters = null;
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
			profileId = GameBootstrap.RiderProfile.ProfileId,
			profileName = GameBootstrap.RiderProfile.DisplayName,
			bodyWeightKg = GameBootstrap.RiderProfile.BodyWeightKg,
			bikeWeightKg = GameBootstrap.BikeProfile.WeightKg,
			trainerKind = DebugTrainerMode,
			dummyMode = GameBootstrap.TrainerClient is FakeTrainerClient { HasLiveDummyControls: true } ? GameBootstrap.Settings.DummyTrainer.Mode : null,
			dummyWattsPerKg = GameBootstrap.TrainerClient is FakeTrainerClient { HasLiveDummyControls: true } && GameBootstrap.Settings.DummyTrainer.Mode == "W/kg" ? GameBootstrap.Settings.DummyTrainer.WattsPerKg : null,
			dummyTargetWatts = GameBootstrap.TrainerClient is FakeTrainerClient { HasLiveDummyControls: true } ? GameBootstrap.Settings.DummyTrainer.CalculateWatts(GameBootstrap.RiderProfile.BodyWeightKg) : null,
			ghostActivityId = GhostRider is null ? null : GhostActivityId,
			ghostElapsedSeconds = GhostRider?.ElapsedSeconds,
			ghostTotalDistanceMeters = GhostRider?.Sample?.DistanceMeters,
			ghostWrappedDistanceMeters = GhostRider?.GetRiders().FirstOrDefault()?.WrappedRouteDistanceMeters,
			ghostLap = GhostRider?.GetRiders().FirstOrDefault()?.LapNumber,
			ghostRelativeDistanceMeters = GhostRider?.Sample is { } ghostSample ? ghostSample.DistanceMeters - _rideState.DistanceMeters : null,
			ghostSpeedKph = GhostRider?.Sample?.SpeedMps * 3.6,
			ghostPowerWatts = GhostRider?.Sample?.PowerWatts,
			ghostCadenceRpm = GhostRider?.Sample?.CadenceRpm,
			ghostFinished = GhostRider?.Finished,
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
			streetFurnitureInsideRideRoad = world?.StreetFurnitureInsideRideRoad ?? 0,
			streetFurnitureProxiesSuppressed = world?.StreetFurnitureProxiesSuppressed ?? 0,
			streetFurnitureProxiesRelocatedRoadside = world?.StreetFurnitureProxiesRelocated ?? 0,
			streetFurnitureIntentionallyRetainedInRoad = world?.StreetFurnitureIntentionallyRetainedInRoad ?? 0,
			riderLateralOffsetMeters = world?.RiderLateralOffsetMeters ?? 0,
			nearestStreetFurnitureOsmId = world?.NearestStreetFurniture?.OsmId,
			nearestStreetFurnitureCategory = world?.NearestStreetFurniture?.Category,
			nearestStreetFurnitureSourcePosition = world?.NearestStreetFurniture is { } furnitureSource ? ToDebugVector(furnitureSource.SourcePosition) : null,
			nearestStreetFurnitureVisualPosition = world?.NearestStreetFurniture is { } furnitureVisual ? ToDebugVector(furnitureVisual.VisualPosition) : null,
			nearestStreetFurnitureRelocated = world?.NearestStreetFurniture?.Relocated,
			backgroundStreetsLoaded = world?.BackgroundStreetsLoaded ?? 0,
			visibleStreetChunks = world?.VisibleStreetChunks ?? 0,
			ambientCars = world?.AmbientCars ?? 0,
			ambientCarTarget = AmbientActorTuning.TargetCars,
			ambientPedestrians = world?.AmbientPedestrians ?? 0,
			ambientPedestrianTarget = AmbientActorTuning.TargetPedestrians,
			ambientEligibleCarPaths = world?.EligibleCarPaths ?? 0,
			ambientEligiblePedestrianPaths = world?.EligiblePedestrianPaths ?? 0,
			ambientCarSpawnAttempts = world?.CarSpawnAttempts ?? 0,
			ambientPedestrianSpawnAttempts = world?.PedestrianSpawnAttempts ?? 0,
			ambientCarOutsideSpawnRadius = world?.CarOutsideSpawnRadius ?? 0,
			ambientPedestrianOutsideSpawnRadius = world?.PedestrianOutsideSpawnRadius ?? 0,
			ambientCarTooCloseToRider = world?.CarTooCloseToRider ?? 0,
			ambientPedestrianTooCloseToRider = world?.PedestrianTooCloseToRider ?? 0,
			ambientCarNoEligiblePath = world?.CarNoEligiblePath ?? 0,
			ambientPedestrianNoValidPath = world?.PedestrianNoValidPath ?? 0,
			ambientCarInvalidTransform = world?.CarInvalidTransform ?? 0,
			ambientPedestrianInvalidTransform = world?.PedestrianInvalidTransform ?? 0,
			ambientCarIllegalOnewayDirections = world?.CarIllegalOnewayDirections ?? 0,
			ambientTotalStreetPolylines = world?.TotalStreetPolylines ?? 0,
			ambientEligibleStreetPolylines = world?.EligibleStreetPolylines ?? 0,
			ambientCarInvalidOrShortPaths = world?.InvalidOrShortCarPaths ?? 0,
			ambientPedestrianInvalidOrShortPaths = world?.InvalidOrShortPedestrianPaths ?? 0,
			ambientPedestrianSeamOrEndSamplesExcluded = world?.PedestrianSeamOrEndSamplesExcluded ?? 0,
			firstAmbientCarPosition = world?.FirstCarPosition is { } carPosition ? ToDebugVector(carPosition) : null,
			firstAmbientCarDistanceMeters = world?.FirstCarDistance,
			firstAmbientCarRoadName = world?.FirstCarRoadName,
			firstAmbientCarRoadClass = world?.FirstCarRoadClass,
			firstAmbientCarSpeedMetersPerSecond = world?.FirstCarSpeedMps,
			firstAmbientCarVisible = world?.FirstCarVisible,
			firstAmbientPedestrianPosition = world?.FirstPedestrianPosition is { } pedPosition ? ToDebugVector(pedPosition) : null,
			firstAmbientPedestrianDistanceMeters = world?.FirstPedestrianDistance,
			firstAmbientPedestrianSpeedMetersPerSecond = world?.FirstPedestrianSpeedMps,
			firstAmbientPedestrianVisible = world?.FirstPedestrianVisible,
			aiRiderCount = world?.AiRiders ?? 0,
			nearestAiRiderName = world?.NearestAiName,
			nearestAiRiderRelativeDistanceMeters = world?.NearestAiRelativeDistanceMeters,
			nearestAiRiderSpeedKph = world?.NearestAiSpeedKph,
			nearestAiRiders = world?.NearestAiRiders?.Select(rider => new AiRiderDebugSnapshot(
				rider.Name, rider.RelativeDistanceMeters, rider.RouteDistanceMeters, rider.SpeedKph,
				rider.PowerWatts, rider.CadenceRpm)).ToArray() ?? Array.Empty<AiRiderDebugSnapshot>(),
			terrainNearChunks = world?.NearTerrainChunks ?? 0,
			terrainFarChunks = world?.FarTerrainChunks ?? 0,
			terrainVisibleChunks = world?.VisibleTerrainChunks ?? 0,
			terrainNearSpacingMeters = world?.TerrainSpacingMeters ?? 0,
			terrainFarSpacingMeters = world?.FarTerrainSpacingMeters ?? 0,
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
		ScreenshotCaptureFailurePolicy.TryCapture(
			() => { if (_hudController is not null) _ = _hudController.CaptureDebugScreenshotAsync(snapshot); },
			ex => GD.PushWarning($"Debug marker {marker} was saved, but screenshot capture could not start: {ex.Message}"));
	}

	public void JumpToRouteDistance(string distanceText)
	{
		if (!_debugDiagnostics.Enabled || !_sessionStarted) return;
		if (_debugJumpFinalizationFailed)
		{
			_hudController?.SetStatus("FIT finalization failed; route jump is disabled to preserve the recording.");
			return;
		}
		if (!double.TryParse(distanceText, System.Globalization.NumberStyles.Float,
			System.Globalization.CultureInfo.InvariantCulture, out var distance))
		{
			_hudController?.SetStatus("Enter a valid route distance in metres (for example 5830).");
			return;
		}
		if (!_debugDiagnostics.TryApplyDistanceJump(_rideState, distance, out _))
		{
			_hudController?.SetStatus("Enter a non-negative route distance in metres.");
			return;
		}
		if (_rideRecorder is not null)
		{
			_pendingDebugJumpDistanceMeters = distance;
			_hudController?.ShowDebugJumpConfirmation();
			return;
		}
		ApplyDebugDistanceJump(distance);
	}

	public void CancelDebugDistanceJump()
	{
		_pendingDebugJumpDistanceMeters = null;
	}

	public void ConfirmDebugDistanceJump()
	{
		if (!_debugDiagnostics.Enabled || !_sessionStarted || _pendingDebugJumpDistanceMeters is not { } distance)
			return;

		if (_rideRecorder is not null)
		{
			var stopAttempted = false;
			try
			{
				// Do not close the recorder if the final sample cannot be written.
				RecordCurrentSample();
				var recorder = _rideRecorder;
				stopAttempted = true;
				var path = recorder.Stop();
				if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
					throw new IOException("The stopped FIT recording file was not found.");
				_rideRecorder = null;
				_lastStoppedRecordingPath = path;
				_currentActivityFitPath = path;
				_recordingSampleElapsedSeconds = 0;
				_hudController?.SetRecordingState(false, false);
			}
			catch (Exception ex)
			{
				_debugJumpFinalizationFailed = stopAttempted;
				LastRecordingError = ex.Message;
				GameLogger.Log("debug_jump_fit_finalise_failed", new { message = ex.Message, filePath = _rideRecorder?.FilePath });
				if (stopAttempted) _hudController?.SetRecordingFailureState();
				_hudController?.SetStatus($"Could not save FIT recording; jump cancelled: {ex.Message}");
				_pendingDebugJumpDistanceMeters = null;
				return;
			}
		}

		_pendingDebugJumpDistanceMeters = null;
		ApplyDebugDistanceJump(distance);
	}

	private void ApplyDebugDistanceJump(double distance)
	{
		if (!_debugDiagnostics.TryApplyDistanceJump(_rideState, distance, out var jumped)) return;
		var before = _rideState.DistanceMeters;
		_rideState = jumped;
		_recordingSampleElapsedSeconds = 0;
		if (_currentRoute?.DistanceMeters > 0)
			_lastLoggedLapNumber = (int)Math.Floor(distance / _currentRoute.DistanceMeters) + 1;
		_trainerCommandDirty = true;
		_trainerCommandElapsedSeconds = TrainerCommandMinIntervalSeconds;
		UpdateRouteHud();
		_hudController?.SetDebugInspectionRecordingState();
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

		var activeDelta = _pauseGate.IsPaused ? 0 : delta;
		if (activeDelta > 0) _elapsedSeconds += activeDelta;

		if (_trainerSessionActive) ProcessTrainerMode(delta);
		else if (_fitReplaySessionActive) ProcessFitReplayMode(delta);
		ProcessMultiplayer(delta);

		if (_trainerSessionActive || _fitReplaySessionActive)
			_activityAccumulator.AddFrame(activeDelta, _rideState.VirtualSpeedKph, _lastTelemetry?.PowerWatts, _lastTelemetry?.CadenceRpm, _rideState.ElevationGainMeters);
		if (_debugDiagnostics.Enabled && _debugDiagnostics.SnapshotDue(delta)) WriteDebugSnapshot();
	}

	public async Task JoinMultiplayerAsync(string address, string displayName, string roomCode)
	{
		if (!_sessionStarted || _currentRoute is null)
			throw new InvalidOperationException("Start a ride and select a route before joining multiplayer.");
		await LeaveMultiplayerAsync();
		var client = new MultiplayerClient();
		_multiplayerClient = client;
		_remoteRiders.Clear();
		_multiplayerPublishElapsed = 0;
		_multiplayerSequence = 0;
		try { await client.ConnectAsync(address, displayName, roomCode, _currentRoute); }
		catch
		{
			throw;
		}
	}

	public async Task LeaveMultiplayerAsync()
	{
		var client = _multiplayerClient;
		_multiplayerClient = null;
		_remoteRiders.Clear();
		if (client is not null) await client.DisposeAsync();
	}

	private void ProcessMultiplayer(double delta)
	{
		var client = _multiplayerClient;
		if (client is null) return;
		foreach (var message in client.DrainEvents())
		{
			switch (message)
			{
				case RiderJoinedMessage joined:
					_remoteRiders[joined.Rider.RiderId] = new MultiplayerRemoteRider(joined.Rider.RiderId,
						joined.Rider.DisplayName, null, null, null, false, false);
					break;
				case RiderStateMessage state when state.RiderId != client.RiderId:
					_remoteRiders[state.RiderId] = new MultiplayerRemoteRider(state.RiderId, state.DisplayName,
						state.DistanceMeters, state.SpeedKph, state.PowerWatts, state.Paused, true);
					break;
				case RiderLeftMessage left:
					_remoteRiders.Remove(left.RiderId);
					break;
				case ErrorMessage error:
					GameLogger.Log("multiplayer_server_error", new { error.Code, error.Message });
					break;
			}
		}
		if (!client.IsConnected)
		{
			_remoteRiders.Clear();
			return;
		}
		if (!_sessionStarted) return;
		_multiplayerPublishElapsed += delta;
		if (_multiplayerPublishElapsed < 0.15) return;
		_multiplayerPublishElapsed = 0;
		client.TryQueueState(new RiderStateMessage
		{
			DisplayName = client.DisplayName,
			DistanceMeters = Math.Max(0, _rideState.DistanceMeters),
			SpeedKph = _pauseGate.IsPaused ? 0 : Math.Max(0, _rideState.VirtualSpeedKph),
			CadenceRpm = _pauseGate.IsPaused ? 0 : Math.Max(0, _rideState.CurrentCadenceRpm),
			PowerWatts = Math.Max(0, _rideState.CurrentPowerWatts),
			Paused = _pauseGate.IsPaused,
			Sequence = ++_multiplayerSequence
		});
	}

	private void ProcessTrainerMode(double delta)
	{
		if (GameBootstrap.TrainerClient is null || !GameBootstrap.TrainerClient.IsConnected)
		{
			_hudController?.SetStatus("Trainer not connected.");
			_hudController?.UpdateFromRideState(_rideState, _lastTelemetry, _elapsedSeconds);
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

public sealed record MultiplayerRemoteRider(string RiderId, string DisplayName, double? DistanceMeters,
	double? SpeedKph, int? PowerWatts, bool Paused, bool HasState);
