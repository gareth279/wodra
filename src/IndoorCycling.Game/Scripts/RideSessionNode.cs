using Godot;
using IndoorCycling.Core.Models;
using IndoorCycling.Core.Simulation;
using IndoorCycling.Trainer.Models;
using IndoorCycling.Trainer.Implementations;
using IndoorCycling.Trainer.Windows;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using System.Collections.Generic;
using System.Text.Json;
using IndoorCycling.Game.Multiplayer;
using Wodra.Multiplayer.Protocol;

public partial class RideSessionNode : Node
{
	private HudController? _hudController;
	private DeviceConnectionPanelController? _deviceConnectionPanel;

	private RouteDefinition? _currentRoute;
	private bool _sessionStarted;
	private bool _trainerSessionActive;
	private bool _fitReplaySessionActive;

	private FakeTrainerClient? _fitReplayClient;

	private RideState _rideState = RideState.Initial;
	private readonly RidePauseGate _pauseGate = new();
	private double? _catchUpResumeSpeedKph;
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
	public bool HasRideRecording => _rideRecorder is not null;
	public int SavedRideSegmentCount => _fitSegments.Count;
	public double ActiveRiderBodyWeightKg => CurrentRiderProfile.WeightKg;
	public string ActiveRiderDisplayName => CurrentRiderProfile.DisplayName;
	private double _elapsedSeconds;
	private Guid? _currentActivityId;
	private Guid? _currentRiderProfileId;
	private long? _currentRideStravaAthleteId;
	private RiderProfile? _activeRideRiderProfile;
	private BikeProfile? _activeRideBikeProfile;
	private RiderProfile CurrentRiderProfile => _activeRideRiderProfile ?? GameBootstrap.RiderProfile;
	private BikeProfile CurrentBikeProfile => _activeRideBikeProfile ?? GameBootstrap.BikeProfile;
	private readonly TrainerGradeTransition _trainerGradeTransition = new();
	private double _trainerTargetGradePercent;
	private double _effectiveTrainerGradePercent;
	private DateTimeOffset _activityStartedAtUtc;
	private string? _currentActivityFitPath;
	private readonly RideActivityAccumulator _activityAccumulator = new();
	private double _actualRiddenDistanceMeters;
	private readonly List<FitSegmentManifestEntry> _fitSegments = new();
	private double _recordingSegmentStartDistanceMeters;
	private double _recordingSegmentStartActualDistanceMeters;
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
	private readonly TrainerCommandWarningPolicy _trainerCommandWarningPolicy = new();
	private long _trainerCommandWarningSessionId;
	private string? _trainerCommandWarningText;
	private IMultiplayerProvider? _multiplayerProvider;
	private CancellationTokenSource? _onlineLifetime;
	private CancellationTokenSource? _automaticJoinCts;
	private Task? _automaticJoinTask;
	private double _automaticRetryRemaining;
	private int _automaticRetryAttempt;
	private string _onlineStatus = "Offline";
	private bool _loggedMissingEosConfiguration;
	private bool? _onlineServiceAvailable;
	private readonly Dictionary<string, MultiplayerRemoteRider> _remoteRiders = new(StringComparer.Ordinal);
	private readonly Dictionary<string, RemoteRiderInterpolator> _remoteInterpolators = new(StringComparer.Ordinal);
	private readonly HashSet<string> _catchUpAssessmentLogged = new(StringComparer.Ordinal);
	private double _multiplayerPublishElapsed;
	private long _multiplayerSequence;
	private RiderRepositionedMessage? _pendingRepositionMessage;
	private Task? _repositionSendTask;
	private double _repositionRetryElapsed;
	public string MultiplayerStatus => _multiplayerProvider?.Status ?? (OnlineRidingEnabled ? _onlineStatus : "Disconnected");
	public bool IsMultiplayerConnected => _multiplayerProvider?.IsConnected == true;
	public bool HasMultiplayerClient => _multiplayerProvider is not null;
	public bool OnlineRidingEnabled => GameBootstrap.Settings?.OnlineRidingEnabled == true;
	public bool? OnlineServiceAvailable => _onlineServiceAvailable;
	public string OnlineStatus => _multiplayerProvider?.IsConnected == true ? "Connected" : _onlineStatus;
	public IReadOnlyList<MultiplayerRemoteRider> MultiplayerRiders => _remoteRiders.Values.ToArray();
	public IReadOnlyList<MultiplayerRemoteRider> GetInterpolatedRemoteRiders() => _remoteInterpolators.Values
		.Select(track => track.TrySample(MonotonicSeconds(), out var sample)
			? new MultiplayerRemoteRider(sample.RiderId, sample.DisplayName, sample.DistanceMeters, sample.SpeedKph,
			sample.PowerWatts, sample.Paused, true, sample.CadenceRpm, track.LastReceivedAtSeconds, sample.LateralOffsetMeters)
			: null).Where(rider => rider is not null).Cast<MultiplayerRemoteRider>().ToArray();

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
		_deviceConnectionPanel = GetNodeOrNull<DeviceConnectionPanelController>("../DeviceConnectionPanel");
		_rideWorldView = GetNodeOrNull<RideWorldView>("../RideWorldView");
		_hudController?.SetStatus("Choose a mode to begin.");
	}

	public async Task<bool> StartTrainerSessionAsync(RouteDefinition? route = null)
	{
		if (GameBootstrap.TrainerClient is null)
		{
			_hudController?.SetStatus("No trainer client configured.");
			return false;
		}

		if (!GameBootstrap.TrainerClient.IsReady)
		{
			if (GameBootstrap.TrainerClient is WindowsBleTrainerClient)
			{
				_hudController?.SetStatus("Trainer is reconnecting. Wait for the connection screen to show Ready before starting the ride.");
				return false;
			}
			_hudController?.SetStatus("Connecting to trainer...");
			await GameBootstrap.TrainerClient.ConnectAsync();
		}
		else
		{
			_hudController?.SetStatus("Trainer already connected.");
		}
		if (GameBootstrap.TrainerClient?.IsReady != true)
		{
			_hudController?.SetStatus("Trainer is not ready. Reconnect before starting the ride.");
			return false;
		}
		if (GameBootstrap.DebugModeEnabled)
			GameLogger.Log("trainer_connected", new { mode = DebugTrainerMode, connected = GameBootstrap.TrainerClient.IsConnected });
		_currentRoute = route ?? RouteCatalog.Routes[0];
		_hudController?.SetDummyTrainerControls(GameBootstrap.TrainerClient as FakeTrainerClient);
		if (GhostRider is not null && GhostRider.RouteId != _currentRoute?.Id)
		{ GhostRider = null; GhostActivityId = null; }
		_currentActivityId = Guid.NewGuid();
		_currentRiderProfileId = null;
		_currentRideStravaAthleteId = null;
		try
		{
			var rider = await GameBootstrap.GetPersistentRiderAsync();
			_currentRiderProfileId = rider.Id;
			SetActiveRideProfile(rider);
			_currentRideStravaAthleteId = StravaIntegrationClient.Shared.GetLink(rider.Id)?.AthleteId;
			GameLogger.Log("ride_profile_snapshot_created", new
			{
				profileId = rider.Id,
				profileName = rider.Name,
				bodyWeightKg = rider.BodyWeightKg,
				bikeWeightKg = rider.BikeWeightKg,
				trainerDifficultyPercent = rider.TrainerDifficultyPercent
			});
			if (GameBootstrap.TrainerClient is FakeTrainerClient { HasLiveDummyControls: true } dummy)
				dummy.SetDummyOutput(GameBootstrap.Settings.DummyTrainer.CalculateWatts(rider.BodyWeightKg),
					GameBootstrap.Settings.DummyTrainer.CadenceRpm);
			_hudController?.SetDummyTrainerControls(GameBootstrap.TrainerClient as FakeTrainerClient);
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
		_actualRiddenDistanceMeters = 0;
		_fitSegments.Clear();
		LastCompletedActivityId = null;
		LastCompletedActivityDraft = null;
		_sessionStarted = true;
		_trainerSessionActive = true;
		_trainerCommandWarningSessionId = _trainerCommandWarningPolicy.BeginSession();
		_trainerCommandWarningText = null;
		_fitReplaySessionActive = false;
		_rideState = RideState.Initial;
		_catchUpResumeSpeedKph = null;
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
		return true;
	}

	/// <summary>Reapplies only the current ride target after trainer reconnection, never a queued historical command.</summary>
	public async Task ReapplyCurrentTrainerSettingsAsync()
	{
		if (!_sessionStarted || !_trainerSessionActive || GameBootstrap.TrainerClient?.IsConnected != true)
			return;
		var commandSessionId = _trainerCommandWarningSessionId;
		await _trainerCommandTask;
		if (!_trainerCommandWarningPolicy.IsCurrentSession(commandSessionId) ||
			!_sessionStarted || !_trainerSessionActive || GameBootstrap.TrainerClient?.IsConnected != true)
			return;
		var gradient = GetTrainerGradeTarget(GetCurrentGradient());
		await GameBootstrap.TrainerClient.SetSimulationAsync(new SimulationCommand(
			GradePercent: gradient,
			RiderWeightKg: CurrentRiderProfile.WeightKg,
			BikeWeightKg: CurrentBikeProfile.WeightKg));
		_lastSentTrainerGradientPercent = GameBootstrap.TrainerClient.LastTransportAcknowledgedSimulationGradePercent ?? gradient;
		_trainerCommandDirty = false;
		HandleTrainerCommandAcknowledged(commandSessionId, gradient);
		GameLogger.Log("trainer_current_settings_transport_acknowledged", new { gradientPercent = gradient,
			physicalResistanceVerified = false });
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
		try { SetActiveRideProfile(await GameBootstrap.GetPersistentRiderAsync()); }
		catch (Exception ex) { GameLogger.Log("ride_profile_snapshot_unavailable", new { message = ex.Message }); }
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
		_catchUpResumeSpeedKph = null;
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

	private void SetActiveRideProfile(RiderProfileRecord profile)
	{
		_currentRiderProfileId = profile.Id;
		_activeRideRiderProfile = new RiderProfile(profile.Name, profile.BodyWeightKg, profile.Id,
			profile.BikeWeightKg, profile.IsTestProfile, profile.CreatedAtUtc, profile.UpdatedAtUtc);
		_activeRideBikeProfile = new BikeProfile(GameBootstrap.Settings.Rider.BikeName, profile.BikeWeightKg);
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
		_trainerCommandWarningPolicy.EndSession(_trainerCommandWarningSessionId);
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
					route.Id, route.Name, _activityStartedAtUtc, _actualRiddenDistanceMeters, route.DistanceMeters,
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
					var historySaved = false;
					try { await history.SaveCompletedActivityAsync(draft, media); historySaved = true; }
					catch (Exception ex) { GameLogger.Log("ride_history_save_failed", new { activityId, message = ex.Message }); GD.PushWarning($"Ride finished, but ride history could not be saved: {ex.Message}"); }
					var recordingLink = StravaIntegrationClient.Shared.GetLink(riderId);
					var hasFinalizedFit = LastRecordingError is null && fitPath is not null &&
						StravaIntegrationClient.GetFitSegments(activityId, fitPath).Any(segment => segment.IsValid);
					var realRide = StravaPolicies.ShouldAutomaticallyUpload(recordingLink?.AutomaticUploadEnabled == true,
						recordingLink is not null && recordingLink.AthleteId == _currentRideStravaAthleteId,
						_activeRideRiderProfile?.IsTestProfile == true,
						GameBootstrap.TrainerClient is FakeTrainerClient,
						hasFinalizedFit);
					if (historySaved && realRide && _currentRideStravaAthleteId is { } athleteId && fitPath is not null)
						_ = Task.Run(async () =>
						{
							try { await StravaIntegrationClient.Shared.QueueAutomaticUploadsAsync(riderId, athleteId, activityId, fitPath); }
							catch (Exception queueError) { GameLogger.Log("strava_automatic_upload_queue_failed", new { activityId, message = queueError.Message }); }
						});
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
					if (GameBootstrap.TrainerClient is WindowsBleTrainerClient ble && _deviceConnectionPanel is not null)
						await _deviceConnectionPanel.DisconnectForMainMenuAsync(ble);
					else if (GameBootstrap.TrainerClient is AntFeCTrainerClient ant && _deviceConnectionPanel is not null)
						await _deviceConnectionPanel.DisconnectForMainMenuAsync(ant);
					else
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
				_currentRideStravaAthleteId = null;
				_activeRideRiderProfile = null;
				_activeRideBikeProfile = null;
				_trainerGradeTransition.Cancel();
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
		GameLogger.Log("ride_ended", new
		{
			activityId = _currentActivityId,
			routeId = _currentRoute?.Id,
			endedAtUtc = DateTimeOffset.UtcNow,
			debugMarkerCount = _debugDiagnostics.MarkerCount
		});
		GameLogger.Flush();
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
		_rideRecorder.RecordSample(_rideState, trainer.GetLatestTelemetry().ForRecording(DateTimeOffset.UtcNow),
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
			{
				_currentActivityFitPath = _lastStoppedRecordingPath;
				_fitSegments.Add(new FitSegmentManifestEntry(Path.GetFileName(_lastStoppedRecordingPath),
					_recordingSegmentStartDistanceMeters, _rideState.DistanceMeters,
					Math.Max(0, _actualRiddenDistanceMeters - _recordingSegmentStartActualDistanceMeters), DateTimeOffset.UtcNow));
				WriteFitSegmentManifest();
			}
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

	private bool FinaliseRecordingForCatchUp()
	{
		if (_rideRecorder is null) return true;
		try
		{
			LastRecordingError = null;
			var path = FinaliseRecording();
			if (LastRecordingError is not null || string.IsNullOrWhiteSpace(path) || !File.Exists(path))
				throw new IOException(LastRecordingError ?? "The FIT file was not created.");
			GameLogger.Log("multiplayer_catch_up_fit_finalised", new { path });
			return true;
		}
		catch (Exception ex)
		{
			LastRecordingError = ex.Message;
			GameLogger.Log("multiplayer_catch_up_fit_finalise_failed", new { message = ex.Message });
			_hudController?.SetStatus($"Catch Up cancelled; the current FIT recording could not be saved: {ex.Message}");
			GD.PushWarning($"Catch Up cancelled to preserve the FIT recording: {ex.Message}");
			return false;
		}
	}

	private void StartAutomaticRideRecording()
	{
		try
		{
			_rideRecorder = new RideFitRecorder();
			_recordingSegmentStartDistanceMeters = _rideState.DistanceMeters;
			_recordingSegmentStartActualDistanceMeters = _actualRiddenDistanceMeters;
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

	private void WriteFitSegmentManifest()
	{
		if (_currentActivityId is not { } activityId || _currentRoute is null || _fitSegments.Count == 0) return;
		try
		{
			var directory = Path.GetDirectoryName(_currentActivityFitPath);
			if (string.IsNullOrWhiteSpace(directory)) return;
			var manifestPath = Path.Combine(directory, $"{activityId:N}.segments.json");
			var manifest = new
			{
				activityId,
				routeId = _currentRoute.Id,
				routeFingerprint = _currentRoute.GeometryFingerprint,
				startedAtUtc = _activityStartedAtUtc,
				segments = _fitSegments
			};
			File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
		}
		catch (Exception ex)
		{
			GameLogger.Log("fit_segment_manifest_write_failed", new { message = ex.Message });
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

	public async Task SetTrainerDifficultyAsync(int percent)
	{
		var previousEffectiveGrade = GetEffectiveTrainerGrade();
		await GameBootstrap.SetTrainerDifficultyAsync(percent);
		var target = GetTrainerGradeTarget(GetCurrentGradient());
		_trainerTargetGradePercent = target;
		if (_sessionStarted && GameBootstrap.TrainerClient?.IsConnected == true)
			_trainerGradeTransition.Start(previousEffectiveGrade, target);
		else
		{
			_trainerGradeTransition.Cancel(target);
			_effectiveTrainerGradePercent = target;
		}
		_trainerCommandDirty = true;
		if (_sessionStarted) _trainerCommandElapsedSeconds = Math.Max(_trainerCommandElapsedSeconds, TrainerCommandMinIntervalSeconds);
		GameLogger.Log("trainer_difficulty_changed", new
		{
			profileId = CurrentRiderProfile.ProfileId,
			trainerDifficultyPercent = GameBootstrap.TrainerDifficultyPercent,
			routeGradientPercent = GetRouteGradient(),
			trainerTargetGradePercent = target,
			transitionStarted = _trainerGradeTransition.IsActive
		});
	}

	private double GetTrainerGradeTarget(double routeGradientPercent) =>
		GameBootstrap.TrainerClient is FakeTrainerClient
			? routeGradientPercent
			: TrainerDifficultyPolicy.ScaleGrade(routeGradientPercent, GameBootstrap.TrainerDifficultyPercent);

	private double GetEffectiveTrainerGrade() => _trainerGradeTransition.IsActive
		? _trainerGradeTransition.CurrentPercent
		: GameBootstrap.TrainerClient?.LastTransportAcknowledgedSimulationGradePercent ??
			_lastSentTrainerGradientPercent ?? GetTrainerGradeTarget(GetCurrentGradient());

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
		settings.Validate(); dummy.SetDummyOutput(settings.CalculateWatts(CurrentRiderProfile.BodyWeightKg), settings.CadenceRpm);
		GameBootstrap.Settings.Save();
		_hudController?.SetDummyTrainerControls(dummy);
	}

	public void AdjustDummyCadence(int increment)
	{
		if (GameBootstrap.TrainerClient is not FakeTrainerClient { HasLiveDummyControls: true } dummy) return;
		var settings = GameBootstrap.Settings.DummyTrainer; settings.CadenceRpm = Math.Clamp(settings.CadenceRpm + increment, 0, 160);
		dummy.SetDummyOutput(settings.CalculateWatts(CurrentRiderProfile.BodyWeightKg), settings.CadenceRpm);
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
		var routeGradient = routeSample?.GradientPercent ?? 0;
		var trainerTargetGrade = _trainerSessionActive ? GetTrainerGradeTarget(GetCurrentGradient()) : 0;
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
			profileId = CurrentRiderProfile.ProfileId,
			profileName = CurrentRiderProfile.DisplayName,
			bodyWeightKg = CurrentRiderProfile.BodyWeightKg,
			bikeWeightKg = CurrentBikeProfile.WeightKg,
			trainerKind = DebugTrainerMode,
			dummyMode = GameBootstrap.TrainerClient is FakeTrainerClient { HasLiveDummyControls: true } ? GameBootstrap.Settings.DummyTrainer.Mode : null,
			dummyWattsPerKg = GameBootstrap.TrainerClient is FakeTrainerClient { HasLiveDummyControls: true } && GameBootstrap.Settings.DummyTrainer.Mode == "W/kg" ? GameBootstrap.Settings.DummyTrainer.WattsPerKg : null,
			dummyTargetWatts = GameBootstrap.TrainerClient is FakeTrainerClient { HasLiveDummyControls: true } ? GameBootstrap.Settings.DummyTrainer.CalculateWatts(CurrentRiderProfile.BodyWeightKg) : null,
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
			totalSystemMassKg = CurrentRiderProfile.WeightKg + CurrentBikeProfile.WeightKg,
			routeGradientPercent = routeGradient,
			trainerDifficultyPercent = GameBootstrap.TrainerDifficultyPercent,
			trainerGradeTargetPercent = trainerTargetGrade,
			trainerGradeCommandedPercent = trainer?.IsConnected == true
				? trainer.LastTransportAcknowledgedSimulationGradePercent ?? _lastSentTrainerGradientPercent : null,
			trainerGradeTransitionActive = _trainerGradeTransition.IsActive,
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
			remoteHumanRiders = world?.RemoteHumanRiders?.Select(rider => new RemoteHumanRiderDebugSnapshot(
				EosIdentityDiagnostics.HashProductUserId(rider.RiderId), rider.Name, rider.HasState, rider.RelativeDistanceMeters, rider.StateAgeSeconds,
				rider.VisualExists, rider.VisualVisible, rider.WithinVisualRange, rider.BehindCamera,
				rider.InsideCameraFrustum)).ToArray() ?? Array.Empty<RemoteHumanRiderDebugSnapshot>(),
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
		_rideWorldView?.MarkChallengePractice(debugJump: true);
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
		if (_catchUpResumeSpeedKph is { } catchUpSpeed)
		{
			_rideState = _pauseGate.Resume(_rideState, catchUpSpeed);
			_catchUpResumeSpeedKph = null;
		}
		else _pauseGate.Resume();
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

		_rideRecorder.RecordSample(_rideState, telemetry.ForRecording(DateTimeOffset.UtcNow), gradient, routeSample);
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

	public async Task SetOnlineRidingEnabledAsync(bool enabled)
	{
		if (GameBootstrap.Settings.OnlineRidingEnabled == enabled &&
			(enabled ? _onlineLifetime is { IsCancellationRequested: false } : _onlineLifetime is null or { IsCancellationRequested: true }))
			return;
		GameBootstrap.Settings.OnlineRidingEnabled = enabled;
		GameBootstrap.Settings.Save();
		if (!enabled)
		{
			_onlineLifetime?.Cancel();
			if (_automaticJoinTask is { IsCompleted: false } activeJoin)
				try { await activeJoin; } catch { }
			await LeaveMultiplayerAsync();
			_automaticRetryRemaining = 0;
			_automaticRetryAttempt = 0;
			_onlineStatus = "Offline";
			_onlineServiceAvailable = null;
			return;
		}

		if (_onlineLifetime is null || _onlineLifetime.IsCancellationRequested)
		{
			_onlineLifetime?.Dispose();
			_onlineLifetime = new CancellationTokenSource();
		}
		var token = _onlineLifetime.Token;
		await RefreshOnlineAvailabilityAsync(token);
	}

	public async Task<bool> RefreshOnlineAvailabilityAsync(CancellationToken cancellationToken = default)
	{
		if (!OnlineRidingEnabled) return false;
		var usingEos = OnlineStatusText.UsesEos(GameBootstrap.Settings.OnlineMultiplayerProvider);
		if (usingEos && EpicOnlineServicesMultiplayerProvider.ConfigurationInvalid)
		{
			_onlineServiceAvailable = false;
			_onlineStatus = "EOS configuration invalid";
			return false;
		}
		if (usingEos && !EpicOnlineServicesMultiplayerProvider.HasRequiredConfiguration)
		{
			_onlineServiceAvailable = false;
			_onlineStatus = "EOS not configured";
			if (!_loggedMissingEosConfiguration)
			{
				_loggedMissingEosConfiguration = true;
				GameLogger.Log("eos_configuration_missing", new
				{
					missingVariables = EpicOnlineServicesMultiplayerProvider.MissingConfigurationVariables
				});
			}
			return false;
		}
		if (usingEos && EosNativeLibraryLoader.LoadFailed)
		{
			_onlineServiceAvailable = false;
			_onlineStatus = "EOS runtime unavailable";
			return false;
		}
		_onlineStatus = "Checking service…";
		var probe = CreateOnlineProvider();
		var available = false;
		var providerStatus = "Offline";
		try
		{
			available = await probe.CheckAvailabilityAsync(GameBootstrap.Settings.MultiplayerServerAddress, cancellationToken);
			providerStatus = probe.Status;
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
		catch (Exception ex)
		{
			providerStatus = ex.Message;
			GameLogger.Log("multiplayer_availability_failed", new { provider = GameBootstrap.Settings.OnlineMultiplayerProvider, message = ex.Message });
		}
		finally { await probe.DisposeAsync(); }
		if (cancellationToken.IsCancellationRequested || !OnlineRidingEnabled) return false;
		_onlineServiceAvailable = available;
		_onlineStatus = available ? "Online ready" :
			usingEos && EosNativeLibraryLoader.LoadFailed ? "EOS runtime unavailable" : "Online unavailable";
		GameLogger.Log("multiplayer_availability_result", new
		{
			provider = GameBootstrap.Settings.OnlineMultiplayerProvider,
			available,
			providerStatus
		});
		if (_sessionStarted)
		{
			_automaticRetryAttempt = 0;
			_automaticRetryRemaining = 0;
		}
		return available;
	}

	public async Task<RoutePresenceResult> GetRoutePresenceAsync(RouteDefinition route,
		CancellationToken cancellationToken = default)
	{
		if (!OnlineRidingEnabled) return RoutePresenceResult.Unavailable("Online Riding is off.");
		var provider = CreateOnlineProvider();
		try
		{
			return await provider.GetRoutePresenceAsync(GameBootstrap.Settings.MultiplayerServerAddress,
				route.Id, route.GeometryFingerprint, cancellationToken);
		}
		finally { await provider.DisposeAsync(); }
	}

	private void StartAutomaticRouteJoin()
	{
		if (!OnlineRidingEnabled || !_sessionStarted || !_trainerSessionActive || _currentRoute is null ||
			_automaticJoinTask is { IsCompleted: false } || _multiplayerProvider is not null) return;
		if (OnlineStatusText.UsesEos(GameBootstrap.Settings.OnlineMultiplayerProvider) && EosNativeLibraryLoader.LoadFailed)
		{
			_onlineServiceAvailable = false;
			_onlineStatus = "EOS runtime unavailable";
			_automaticRetryRemaining = 0;
			return;
		}
		if (OnlineStatusText.UsesEos(GameBootstrap.Settings.OnlineMultiplayerProvider) &&
			EpicOnlineServicesMultiplayerProvider.ConfigurationInvalid)
		{
			_onlineServiceAvailable = false;
			_onlineStatus = "EOS configuration invalid";
			_automaticRetryRemaining = 0;
			return;
		}
		if (OnlineStatusText.UsesEos(GameBootstrap.Settings.OnlineMultiplayerProvider) &&
			!EpicOnlineServicesMultiplayerProvider.HasRequiredConfiguration)
		{
			_onlineServiceAvailable = false;
			_onlineStatus = "EOS not configured";
			_automaticRetryRemaining = 0;
			return;
		}
		var lifetimeToken = _onlineLifetime?.Token ?? CancellationToken.None;
		_automaticJoinCts = CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken);
		_automaticJoinTask = AutomaticRouteJoinAsync(_currentRoute, _automaticJoinCts.Token);
	}

	private async Task AutomaticRouteJoinAsync(RouteDefinition route, CancellationToken cancellationToken)
	{
		var provider = CreateOnlineProvider();
		_multiplayerProvider = provider;
		_remoteRiders.Clear();
		_remoteInterpolators.Clear();
		_catchUpAssessmentLogged.Clear();
		_multiplayerPublishElapsed = 0;
		_multiplayerSequence = 0;
		_pendingRepositionMessage = null;
		_repositionSendTask = null;
		_onlineStatus = "Connecting…";
		try
		{
			// Use the profile snapshot captured when this ride began, even if provider
			// availability checks or authentication started earlier in startup.
			var selectedProfile = CurrentRiderProfile;
			var displayName = RiderDisplayName.Resolve(selectedProfile.DisplayName);
			GameLogger.Log("multiplayer_local_display_name_selected", new
			{
				provider = GameBootstrap.Settings.OnlineMultiplayerProvider,
				profileId = selectedProfile.ProfileId,
				displayName
			});
			await provider.JoinRouteSessionAsync(GameBootstrap.Settings.MultiplayerServerAddress, displayName,
				"public", route.Id, route.GeometryFingerprint, cancellationToken);
			if (cancellationToken.IsCancellationRequested || !OnlineRidingEnabled || !_sessionStarted ||
				!string.Equals(_currentRoute?.Id, route.Id, StringComparison.Ordinal))
			{
				if (ReferenceEquals(_multiplayerProvider, provider)) _multiplayerProvider = null;
				await provider.DisposeAsync();
				return;
			}
			_onlineStatus = "Connected";
			_onlineServiceAvailable = true;
			_automaticRetryAttempt = 0;
			_automaticRetryRemaining = 0;
			GameLogger.Log("multiplayer_auto_joined", new { routeId = route.Id, sessionId = "public" });
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			if (ReferenceEquals(_multiplayerProvider, provider)) _multiplayerProvider = null;
			await provider.DisposeAsync();
		}
		catch (Exception ex)
		{
			if (ReferenceEquals(_multiplayerProvider, provider)) _multiplayerProvider = null;
			await provider.DisposeAsync();
			_onlineServiceAvailable = false;
			GameLogger.Log("multiplayer_auto_join_failed", new { routeId = route.Id, message = ex.Message });
			if (OnlineRidingEnabled && _trainerSessionActive && !cancellationToken.IsCancellationRequested)
			{
				var nativeRuntimeUnavailable = OnlineStatusText.UsesEos(GameBootstrap.Settings.OnlineMultiplayerProvider) &&
					EosNativeLibraryLoader.LoadFailed;
				if (nativeRuntimeUnavailable)
				{
					_automaticRetryRemaining = 0;
					_onlineStatus = "EOS runtime unavailable";
				}
				else
				{
					_automaticRetryAttempt++;
					_automaticRetryRemaining = AutomaticRetryDelaySeconds(_automaticRetryAttempt);
					_onlineStatus = "Server unavailable · retrying";
				}
			}
		}
		finally
		{
			if (_automaticJoinCts?.Token == cancellationToken)
			{
				_automaticJoinCts.Dispose();
				_automaticJoinCts = null;
			}
		}
	}

	private static IMultiplayerProvider CreateOnlineProvider()
	{
		var selected = GameBootstrap.Settings.OnlineMultiplayerProvider?.Trim();
		if (string.Equals(selected, "WebSocket", StringComparison.OrdinalIgnoreCase))
			return new WebSocketMultiplayerProvider();
		if (string.IsNullOrEmpty(selected) || string.Equals(selected, "EpicOnlineServices", StringComparison.OrdinalIgnoreCase) ||
			string.Equals(selected, "EOS", StringComparison.OrdinalIgnoreCase))
			return new EpicOnlineServicesMultiplayerProvider();
		throw new InvalidOperationException($"Unknown multiplayer provider '{selected}'. Choose EpicOnlineServices or WebSocket.");
	}

	private static double AutomaticRetryDelaySeconds(int attempt) =>
		Math.Min(30, Math.Pow(2, Math.Clamp(attempt - 1, 0, 5)));

	public async Task JoinMultiplayerAsync(string address, string displayName, string roomCode)
	{
		if (!_sessionStarted || _currentRoute is null)
			throw new InvalidOperationException("Start a ride and select a route before joining multiplayer.");
		await LeaveMultiplayerAsync();
		GameBootstrap.Settings.MultiplayerServerAddress = address.Trim();
		GameBootstrap.Settings.Save();
		var client = new WebSocketMultiplayerProvider();
		_multiplayerProvider = client;
		_remoteRiders.Clear();
		_multiplayerPublishElapsed = 0;
		_multiplayerSequence = 0;
		_pendingRepositionMessage = null;
		_repositionSendTask = null;
		try
		{
			await client.JoinRouteSessionAsync(address, displayName, roomCode, _currentRoute.Id,
				_currentRoute.GeometryFingerprint);
			_onlineStatus = "Connected";
			_onlineServiceAvailable = true;
		}
		catch
		{
			_multiplayerProvider = null;
			await client.DisposeAsync();
			throw;
		}
	}

	public async Task LeaveMultiplayerAsync()
	{
		_automaticJoinCts?.Cancel();
		if (_automaticJoinTask is { IsCompleted: false } automaticJoin)
			try { await automaticJoin; } catch { }
		_automaticJoinCts?.Dispose();
		_automaticJoinCts = null;
		_automaticJoinTask = null;
		var client = _multiplayerProvider;
		_automaticRetryRemaining = 0;
		_multiplayerProvider = null;
		_remoteRiders.Clear();
		_remoteInterpolators.Clear();
		_pendingRepositionMessage = null;
		_repositionSendTask = null;
		_hudController?.SetMultiplayerStatus(null);
		_hudController?.HideCatchUpOffer();
		if (client is not null) await client.DisposeAsync();
		if (OnlineRidingEnabled) _onlineStatus = "Available · joins when you start a ride";
	}

	public async Task<RoutePresenceResult> GetCurrentRoutePresenceAsync(CancellationToken cancellationToken = default)
	{
		if (_currentRoute is null) return RoutePresenceResult.Unavailable("Select a route to check its public session.");
		return await GetRoutePresenceAsync(_currentRoute, cancellationToken);
	}

	private void ProcessMultiplayer(double delta)
	{
		UpdateNearbyHumanRiderHud();
		var client = _multiplayerProvider;
		if (client is null)
		{
			_hudController?.SetMultiplayerStatus(OnlineRidingEnabled ? _onlineStatus : null);
			if (OnlineRidingEnabled && _trainerSessionActive)
			{
				_automaticRetryRemaining = Math.Max(0, _automaticRetryRemaining - delta);
				if (_automaticRetryRemaining <= 0) StartAutomaticRouteJoin();
			}
			return;
		}
		client.Tick(delta);
		var eosProviderSelected = OnlineStatusText.UsesEos(GameBootstrap.Settings.OnlineMultiplayerProvider);
		var automaticJoinInProgress = _automaticJoinTask is { IsCompleted: false };
		if (!client.IsConnected && !client.Status.Equals("Connecting…", StringComparison.Ordinal) &&
			!(eosProviderSelected && automaticJoinInProgress))
		{
			if (ReferenceEquals(_multiplayerProvider, client)) _multiplayerProvider = null;
			_remoteRiders.Clear();
			_remoteInterpolators.Clear();
			_hudController?.HideCatchUpOffer();
			if (OnlineRidingEnabled && _trainerSessionActive && _onlineLifetime?.IsCancellationRequested != true)
			{
				_onlineServiceAvailable = false;
				_onlineStatus = "Reconnecting…";
				_automaticRetryAttempt++;
				_automaticRetryRemaining = AutomaticRetryDelaySeconds(_automaticRetryAttempt);
			}
			_ = client.DisposeAsync();
			_hudController?.SetMultiplayerStatus(OnlineRidingEnabled ? _onlineStatus : null);
			return;
		}
		foreach (var message in client.DrainEvents())
		{
			switch (message)
			{
				case RiderJoinedMessage joined:
					if (_remoteRiders.TryGetValue(joined.Rider.RiderId, out var existingJoinedRider))
						_remoteRiders[joined.Rider.RiderId] = existingJoinedRider with { DisplayName = joined.Rider.DisplayName };
					else
						_remoteRiders[joined.Rider.RiderId] = new MultiplayerRemoteRider(joined.Rider.RiderId,
							joined.Rider.DisplayName, null, null, null, false, false);
					break;
				case RiderStateMessage state when state.RiderId != client.RiderId:
					var joiningRider = !_remoteRiders.TryGetValue(state.RiderId, out var knownRider) || !knownRider.HasState;
					ApplyRemoteSample(state.RiderId, state.DisplayName, state.DistanceMeters, state.SpeedKph,
						state.CadenceRpm, state.PowerWatts, state.Paused, state.Sequence, state.LateralOffsetMeters, reposition: false);
					if (joiningRider)
					{
						var distanceAhead = state.DistanceMeters - _rideState.DistanceMeters;
						var eligible = _sessionStarted && distanceAhead > 100;
						if (_debugDiagnostics.Enabled && _catchUpAssessmentLogged.Add(state.RiderId))
							GameLogger.Log("multiplayer_catch_up_offer_assessed", new
							{
								riderIdentityHash = EosIdentityDiagnostics.HashProductUserId(state.RiderId),
								eligible,
								reason = !_sessionStarted ? "local ride is not active" : eligible ? "remote rider is more than 100 m ahead" : "remote rider is not more than 100 m ahead",
								localDistanceMeters = _rideState.DistanceMeters,
								remoteDistanceMeters = state.DistanceMeters,
								distanceAheadMeters = distanceAhead
							});
						if (eligible)
							_hudController?.ShowCatchUpOffer(state.RiderId, state.DisplayName, distanceAhead);
					}
					break;
				case RiderRepositionedMessage reposition when reposition.RiderId != client.RiderId:
					ApplyRemoteSample(reposition.RiderId, reposition.DisplayName, reposition.DistanceMeters, reposition.SpeedKph,
						reposition.CadenceRpm, reposition.PowerWatts, reposition.Paused, reposition.Sequence, reposition.LateralOffsetMeters, reposition: true);
					break;
				case RiderLeftMessage left:
					_remoteRiders.Remove(left.RiderId);
					_remoteInterpolators.Remove(left.RiderId);
					break;
				case ErrorMessage error:
					GameLogger.Log("multiplayer_server_error", new { error.Code, error.Message });
					break;
			}
		}
		var staleRiders = _remoteRiders.Values
			.Where(r => r.HasState && MonotonicSeconds() - r.ReceivedAtSeconds > RemoteRiderInterpolator.StaleAfterSeconds)
			.Select(r => r.RiderId).ToArray();
		foreach (var riderId in staleRiders)
		{
			_remoteRiders.Remove(riderId);
			_remoteInterpolators.Remove(riderId);
			if (_debugDiagnostics.Enabled) GameLogger.Log("multiplayer_remote_rider_stale", new { riderIdentityHash = EosIdentityDiagnostics.HashProductUserId(riderId) });
		}
		UpdateNearbyHumanRiderHud();
		var multiplayerStatus = client.IsConnected
			? eosProviderSelected
				? client.Status
				: $"Online · {_remoteRiders.Count + 1} rider(s)"
			: eosProviderSelected && automaticJoinInProgress ? client.Status : null;
		_hudController?.SetMultiplayerStatus(multiplayerStatus);
		if (!client.IsConnected)
		{
			_remoteRiders.Clear();
			_remoteInterpolators.Clear();
			return;
		}
		if (!_sessionStarted) return;
		if (_pendingRepositionMessage is { } pending)
		{
			if (_repositionSendTask?.IsCompleted == true)
			{
				if (_repositionSendTask.IsCompletedSuccessfully)
					_pendingRepositionMessage = null;
				else
				{
					GameLogger.Log("multiplayer_reposition_retry", new { pending.DistanceMeters });
					_repositionSendTask = null;
				}
			}
			if (_pendingRepositionMessage is not null)
			{
				_repositionRetryElapsed += delta;
				if (_repositionSendTask is null && _repositionRetryElapsed >= 1.0)
				{
					_repositionRetryElapsed = 0;
					_repositionSendTask = client.PublishRepositionAsync(_pendingRepositionMessage);
				}
				return;
			}
		}
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
			LateralOffsetMeters = _rideWorldView?.CurrentLateralOffsetMeters ?? 1.75,
			Sequence = ++_multiplayerSequence
		});
	}

	private void UpdateNearbyHumanRiderHud() => _hudController?.SetNearbyHumanRiders(
		_remoteRiders.Values, _rideState.DistanceMeters, _currentRoute?.DistanceMeters ?? 0, MonotonicSeconds());

	private void ApplyRemoteSample(string riderId, string name, double distance, double speed, int cadence,
		int power, bool paused, long sequence, double lateralOffsetMeters, bool reposition)
	{
		var now = MonotonicSeconds();
		var sample = new RemoteRiderSample(riderId, name, distance, speed, cadence, power, paused, sequence, now,
			Math.Clamp(double.IsFinite(lateralOffsetMeters) ? lateralOffsetMeters : 1.75, -3.05, 3.05));
		if (!_remoteInterpolators.TryGetValue(riderId, out var track))
			_remoteInterpolators[riderId] = track = new RemoteRiderInterpolator();
		if (reposition) track.Reset(sample);
		else track.Add(sample);
		_remoteRiders[riderId] = new MultiplayerRemoteRider(riderId, name, distance, speed, power, paused, true, cadence, now,
			sample.LateralOffsetMeters);
	}

	private static double MonotonicSeconds() => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;

	public bool TryGetCatchUpTarget(string riderId, out MultiplayerRemoteRider rider, out string error)
	{
		rider = null!;
		if (_multiplayerProvider?.IsConnected != true) { error = "Multiplayer is disconnected."; return false; }
		if (_currentRoute is null) { error = "No route is active."; return false; }
		if (!_remoteInterpolators.TryGetValue(riderId, out var track) ||
			MonotonicSeconds() - track.LastReceivedAtSeconds > RemoteRiderInterpolator.StaleAfterSeconds ||
			!_remoteRiders.TryGetValue(riderId, out var latest) || !latest.HasState)
		{ error = "That rider's position is unavailable or stale."; return false; }
		rider = latest;
		error = "";
		return true;
	}

	public async Task<bool> CatchUpToRiderAsync(string riderId)
	{
		var riderIdentityHash = EosIdentityDiagnostics.HashProductUserId(riderId);
		GameLogger.Log("multiplayer_catch_up_operation_started", new { riderIdentityHash });
		var error = "";
		MultiplayerRemoteRider target = null!;
		if (!_sessionStarted || _currentRoute is null || _rideWorldView is null || !TryGetCatchUpTarget(riderId, out target, out error))
		{
			GameLogger.Log("multiplayer_catch_up_reposition_failed", new { riderIdentityHash, reason = error.Length == 0 ? "Catch Up is unavailable." : error });
			_hudController?.SetStatus(error.Length == 0 ? "Catch Up is unavailable." : error);
			return false;
		}
		var route = _currentRoute;
		if (route is null || target.DistanceMeters is not { } absoluteDistance)
		{
			const string reason = "The selected rider's route position is unavailable.";
			GameLogger.Log("multiplayer_catch_up_reposition_failed", new { riderIdentityHash, reason });
			_hudController?.SetStatus(reason);
			return false;
		}
		var shouldRestartRecording = _rideRecorder is not null;
		var wasPaused = _pauseGate.IsPaused || _rideRecorder?.IsPaused == true;
		var previousTrainerGrade = GetEffectiveTrainerGrade();
		if (shouldRestartRecording && !FinaliseRecordingForCatchUp())
		{
			GameLogger.Log("multiplayer_catch_up_reposition_failed", new { riderIdentityHash, reason = "FIT finalisation failed; rider was not repositioned." });
			return false;
		}
		// Re-read after closing the current segment: position can advance while the final FIT sample is written.
		if (!TryGetCatchUpTarget(riderId, out target, out error))
		{
			GameLogger.Log("multiplayer_catch_up_reposition_failed", new { riderIdentityHash, reason = error, stage = "post_fit_finalisation" });
			_hudController?.SetStatus($"Catch Up cancelled: {error}");
			return false;
		}
		absoluteDistance = target.DistanceMeters!.Value;
		var remote = target;
		var matchedSpeedKph = CatchUpSpeedResolver.Resolve(remote.SpeedKph ?? double.NaN,
			MonotonicSeconds() - remote.ReceivedAtSeconds, remote.Paused);
		var destination = MultiplayerRoutePosition.PositionBehind(absoluteDistance);
		var routeSample = route.GetSampleAt(destination);
		_catchUpResumeSpeedKph = wasPaused ? matchedSpeedKph : null;
		_rideState = _rideState with { DistanceMeters = destination,
			VirtualSpeedKph = wasPaused ? 0 : matchedSpeedKph,
			CurrentGradientPercent = routeSample.GradientPercent + _manualGradientOffsetPercent };
		_trainerTargetGradePercent = GetTrainerGradeTarget(routeSample.GradientPercent + _manualGradientOffsetPercent);
		_trainerGradeTransition.Start(previousTrainerGrade, _trainerTargetGradePercent);
		_effectiveTrainerGradePercent = previousTrainerGrade;
		_recordingSampleElapsedSeconds = 0;
		_lastLoggedLapNumber = (int)Math.Floor(destination / route.DistanceMeters) + 1;
		_trainerCommandDirty = true;
		_trainerCommandElapsedSeconds = TrainerCommandMinIntervalSeconds;
		UpdateRouteHud();
		_rideWorldView.ResetAfterReposition();
		_rideWorldView.MarkChallengePractice(catchUp: true);
		var fitRestartFailed = false;
		if (shouldRestartRecording)
		{
			StartAutomaticRideRecording();
			if (_rideRecorder is { IsRecording: true })
				GameLogger.Log("multiplayer_catch_up_new_fit_started", new { path = _rideRecorder.FilePath, distanceMeters = destination });
			else
			{
				fitRestartFailed = true;
				GameLogger.Log("multiplayer_catch_up_new_fit_failed", new { message = LastRecordingError ?? "FIT recording did not start.", distanceMeters = destination });
				_hudController?.SetStatus($"Caught up, but the new FIT recording could not start: {LastRecordingError ?? "unknown error"}");
			}
			if (wasPaused && _rideRecorder is { IsRecording: true })
			{
				RecordCurrentSample();
				_rideRecorder.Pause();
				_hudController?.SetRecordingState(true, true);
			}
		}
		if (_multiplayerProvider is { IsConnected: true } client)
		{
			var reposition = new RiderRepositionedMessage { Sequence = ++_multiplayerSequence,
				RouteId = route.Id, RouteFingerprint = route.GeometryFingerprint,
				DistanceMeters = destination, SpeedKph = _pauseGate.IsPaused ? 0 : matchedSpeedKph,
				CadenceRpm = _pauseGate.IsPaused ? 0 : _rideState.CurrentCadenceRpm,
				PowerWatts = _rideState.CurrentPowerWatts, Paused = _pauseGate.IsPaused,
				LateralOffsetMeters = _rideWorldView?.CurrentLateralOffsetMeters ?? 1.75 };
			_pendingRepositionMessage = reposition;
			_repositionRetryElapsed = 0;
			_repositionSendTask = client.PublishRepositionAsync(reposition);
			try
			{
				await _repositionSendTask.WaitAsync(TimeSpan.FromSeconds(2));
				if (ReferenceEquals(_pendingRepositionMessage, reposition)) _pendingRepositionMessage = null;
			}
			catch (Exception ex) { GameLogger.Log("multiplayer_reposition_send_failed", new { message = ex.Message }); }
		}
		if (!fitRestartFailed)
			_hudController?.SetStatus($"Caught up to {remote.DisplayName} · {_rideState.DistanceMeters / 1000:0.00} km.");
		GameLogger.Log("multiplayer_catch_up_reposition_completed", new { riderIdentityHash, targetDisplayName = remote.DisplayName, distanceMeters = destination, wasPaused });
		return true;
	}

	private void ProcessTrainerMode(double delta)
	{
		if (GameBootstrap.TrainerClient is null || !GameBootstrap.TrainerClient.IsReady)
		{
			_trainerGradeTransition.Cancel();
			_trainerCommandDirty = true;
			var staleTelemetry = GameBootstrap.TrainerClient?.GetLatestTelemetry() ?? new TrainerTelemetry(
				DateTimeOffset.MinValue, 0, 0, 0, HasPowerMeasurement: false,
				HasCadenceMeasurement: false, HasReceivedMeasurement: false, HasSpeedMeasurement: false);
			staleTelemetry = AddFreshHeartRate(staleTelemetry);
			_lastTelemetry = staleTelemetry;
			_hudController?.SetStatus(GameBootstrap.TrainerClient is null
				? "No trainer is configured. Return to trainer connections to reconnect."
				: "Trainer disconnected. The ride and recording continue while fresh telemetry and control recover.");
			var reconnectGradient = GetCurrentGradient();
			var before = _rideState.DistanceMeters;
			_rideState = _pauseGate.Tick(GameBootstrap.SimulationEngine, _rideState,
				new SimulationTickInput(staleTelemetry.ForRiderInput(DateTimeOffset.UtcNow), reconnectGradient,
					_pauseGate.IsPaused ? 0 : delta, CurrentRiderProfile, CurrentBikeProfile));
			if (!_pauseGate.IsPaused)
			{
				_actualRiddenDistanceMeters += Math.Max(0, _rideState.DistanceMeters - before);
				LogLapWraps(before, _rideState.DistanceMeters);
				RecordRideSampleIfNeeded(delta, staleTelemetry, reconnectGradient);
			}
			UpdateRouteHud();
			_hudController?.UpdateFromRideState(_rideState, _lastTelemetry, _elapsedSeconds);
			return;
		}

		var telemetry = AddFreshHeartRate(GameBootstrap.TrainerClient.GetLatestTelemetry());
		_lastTelemetry = telemetry;
		var riderInput = telemetry.ForRiderInput(DateTimeOffset.UtcNow);
		if (_pauseGate.IsPaused)
		{
			_rideState = _pauseGate.Tick(GameBootstrap.SimulationEngine, _rideState,
				new SimulationTickInput(riderInput, GetCurrentGradient(), 0,
					CurrentRiderProfile, CurrentBikeProfile));
			UpdateRouteHud();
			_hudController?.UpdateFromRideState(_rideState, telemetry, _elapsedSeconds);
			return;
		}
		//var gradient = GetGradientForCurrentDistance(_rideState.DistanceMeters);
		var gradient = GetCurrentGradient();

		var input = new SimulationTickInput(
			Telemetry: riderInput,
			GradientPercent: gradient,
			DeltaSeconds: delta,
			RiderProfile: CurrentRiderProfile,
			BikeProfile: CurrentBikeProfile
		);

		var previousDistance = _rideState.DistanceMeters;
		_rideState = _pauseGate.Tick(GameBootstrap.SimulationEngine, _rideState, input);
		_actualRiddenDistanceMeters += Math.Max(0, _rideState.DistanceMeters - previousDistance);
		LogLapWraps(previousDistance, _rideState.DistanceMeters);

		_trainerCommandElapsedSeconds += delta;
		_trainerTargetGradePercent = GetTrainerGradeTarget(gradient);
		_effectiveTrainerGradePercent = _trainerGradeTransition.Advance(delta, _trainerTargetGradePercent);

		if (_trainerCommandTask.IsCompleted && ShouldSendTrainerSimulationCommand(_effectiveTrainerGradePercent))
		{
			_trainerCommandTask = SendTrainerSimulationCommandAsync(_effectiveTrainerGradePercent);
		}
		RecordRideSampleIfNeeded(delta, telemetry, gradient);
		UpdateRouteHud();
		_hudController?.UpdateFromRideState(_rideState, telemetry, _elapsedSeconds);
	}

	private static TrainerTelemetry AddFreshHeartRate(TrainerTelemetry telemetry)
	{
		var now = DateTimeOffset.UtcNow;
		var bpm = GameBootstrap.HeartRateClient?.GetFreshBpm(now);
		return telemetry with
		{
			HeartRateBpm = bpm ?? telemetry.HeartRateBpm,
			HeartRateTimestamp = bpm.HasValue ? GameBootstrap.HeartRateClient?.LastMeasurementAt : telemetry.HeartRateTimestamp
		};
	}

	private async Task SendTrainerSimulationCommandAsync(double gradient)
	{
		var commandSessionId = _trainerCommandWarningSessionId;
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
					RiderWeightKg: CurrentRiderProfile.WeightKg,
					BikeWeightKg: CurrentBikeProfile.WeightKg
				)
			);

			if (!_trainerCommandWarningPolicy.IsCurrentSession(commandSessionId))
			{
				GameLogger.Log("trainer_simulation_command_stale_completion", new { commandSessionId, outcome = "acknowledged" });
				return;
			}
			_lastSentTrainerGradientPercent = GameBootstrap.TrainerClient!.LastTransportAcknowledgedSimulationGradePercent ?? gradient;
			HandleTrainerCommandAcknowledged(commandSessionId, gradient);
			if (_debugDiagnostics.Enabled) GameLogger.Log("trainer_simulation_transport_acknowledged", new { gradientPercent = gradient,
				physicalResistanceVerified = false });
		}
		catch (Exception ex)
		{
			var currentSession = _trainerCommandWarningPolicy.IsCurrentSession(commandSessionId);
			var warningTransition = currentSession
				? _trainerCommandWarningPolicy.ObserveFailure(commandSessionId)
				: TrainerCommandWarningTransition.None;
			GameLogger.Log("trainer_simulation_command_failed", new
			{
				gradientPercent = gradient,
				message = ex.Message,
				exceptionType = ex.GetType().FullName,
				commandSessionId,
				staleSession = !currentSession,
				consecutiveFailures = _trainerCommandWarningPolicy.GetConsecutiveFailureCount(commandSessionId),
				persistentWarning = warningTransition == TrainerCommandWarningTransition.Show ||
					_trainerCommandWarningPolicy.IsWarningVisible(commandSessionId)
			});
			if (!currentSession) return;

			if (warningTransition == TrainerCommandWarningTransition.Show)
			{
				_trainerCommandWarningText = $"Trainer control has missed acknowledgements for {TrainerCommandWarningPolicy.ConsecutiveFailureWarningThreshold} consecutive gradient commands.";
				_hudController?.SetStatus(_trainerCommandWarningText);
			}

			// Keep dirty=true so it retries later, but not every frame.
			_trainerCommandDirty = true;
		}
		finally
		{
			if (_trainerCommandWarningPolicy.IsCurrentSession(commandSessionId))
				_trainerCommandElapsedSeconds = 0;
		}
	}

	private void HandleTrainerCommandAcknowledged(long commandSessionId, double gradient)
	{
		var transition = _trainerCommandWarningPolicy.ObserveTransportAcknowledgement(commandSessionId);
		if (transition != TrainerCommandWarningTransition.Clear) return;
		var warning = _trainerCommandWarningText;
		var statusCleared = warning is not null && _hudController?.ClearStatusIfMatches(warning) == true;
		_trainerCommandWarningText = null;
		GameLogger.Log("trainer_simulation_warning_cleared", new
		{
			commandSessionId,
			gradientPercent = gradient,
			statusCleared,
			physicalResistanceVerified = false
		});
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
					CurrentRiderProfile, CurrentBikeProfile));
			_hudController?.UpdateFromRideState(_rideState, telemetry, _elapsedSeconds);
			return;
		}

		var input = new SimulationTickInput(
			Telemetry: telemetry,
			GradientPercent: gradient,
			DeltaSeconds: delta,
			RiderProfile: CurrentRiderProfile,
			BikeProfile: CurrentBikeProfile
		);

		var previousDistance = _rideState.DistanceMeters;
		_rideState = _pauseGate.Tick(GameBootstrap.SimulationEngine, _rideState, input);
		_actualRiddenDistanceMeters += Math.Max(0, _rideState.DistanceMeters - previousDistance);
		LogLapWraps(previousDistance, _rideState.DistanceMeters);

		_ = _fitReplayClient.SetSimulationAsync(
			new SimulationCommand(
				GradePercent: gradient,
				RiderWeightKg: CurrentRiderProfile.WeightKg,
				BikeWeightKg: CurrentBikeProfile.WeightKg
			)
		);

		RecordRideSampleIfNeeded(delta, telemetry, gradient);
		_hudController?.UpdateFromRideState(_rideState, telemetry, _elapsedSeconds);
	}

}

public sealed record MultiplayerRemoteRider(string RiderId, string DisplayName, double? DistanceMeters,
	double? SpeedKph, int? PowerWatts, bool Paused, bool HasState, int CadenceRpm = 0, double ReceivedAtSeconds = 0,
	double? LateralOffsetMeters = 1.75);

public sealed record FitSegmentManifestEntry(string FileName, double RouteStartDistanceMeters,
	double RouteEndDistanceMeters, double RiddenDistanceMeters, DateTimeOffset FinalisedAtUtc);
