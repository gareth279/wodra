using System;
using System.IO;
using Dynastream.Fit;
using IndoorCycling.Core.Models;
using IndoorCycling.Core.Simulation;

using FitDateTime = Dynastream.Fit.DateTime;
using FitFile = Dynastream.Fit.File;
using FitActivityType = Dynastream.Fit.Activity;

public sealed class RideFitRecorder
{
	private Encode? _encoder;
    private FileStream? _fitStream;

	private DateTimeOffset _startedAtUtc;
	private DateTimeOffset _currentResumeStartedAtUtc;
	private TimeSpan _activeTimeBeforeCurrentResume = TimeSpan.Zero;

	private RecordingProgress _progress = new();
	private ushort _recordCount;

	public bool IsRecording { get; private set; }
	public bool IsPaused { get; private set; }
	public string? FilePath { get; private set; }

	public string StartOrResume(double rawDistanceMeters, double? routeElevationMeters = null)
	{
		if (_encoder is null)
		{
			StartNewFile();
			_progress.Rebase(rawDistanceMeters, routeElevationMeters);
			return FilePath!;
		}

		if (IsPaused)
		{
			_progress.Rebase(rawDistanceMeters, routeElevationMeters);
			var now = DateTimeOffset.UtcNow;
			_currentResumeStartedAtUtc = now;
			IsPaused = false;
			IsRecording = true;

			WriteTimerEvent(now, EventType.Start);

			GameLogger.Log("ride_recording_resumed", new { filePath = FilePath });
		}

		return FilePath!;
	}

	public void Pause()
	{
		if (_encoder is null || !IsRecording)
			return;

		var now = DateTimeOffset.UtcNow;
		_activeTimeBeforeCurrentResume += now - _currentResumeStartedAtUtc;

		WriteTimerEvent(now, EventType.Stop);

		IsRecording = false;
		IsPaused = true;

		GameLogger.Log("ride_recording_paused", new { filePath = FilePath });
	}

	public string? Stop()
    {
        if (_encoder is null)
            return FilePath;

        var now = DateTimeOffset.UtcNow;

        var totalTimerTime = _activeTimeBeforeCurrentResume;
        if (IsRecording)
        {
            totalTimerTime += now - _currentResumeStartedAtUtc;
        }

        var totalElapsedTime = now - _startedAtUtc;

        var finalPath = FilePath;

        try
        {
            WriteTimerEvent(now, EventType.StopAll);
            WriteLap(now, totalElapsedTime, totalTimerTime);
            WriteSession(now, totalElapsedTime, totalTimerTime);
            WriteActivity(now, totalTimerTime);

            _encoder.Close();

            GameLogger.Log("ride_recording_stopped", new
            {
                filePath = finalPath,
                totalElapsedSeconds = totalElapsedTime.TotalSeconds,
                totalTimerSeconds = totalTimerTime.TotalSeconds,
                totalDistanceMeters = _progress.DistanceMeters,
                recordCount = _recordCount
            });
        }
        catch (Exception ex)
        {
            GameLogger.Log("ride_recording_stop_failed", new
            {
                filePath = finalPath,
                message = ex.Message,
                exceptionType = ex.GetType().FullName,
                stackTrace = ex.StackTrace
            });

            throw;
        }
        finally
        {
            _encoder = null;

            _fitStream?.Dispose();
            _fitStream = null;

            IsRecording = false;
            IsPaused = false;
        }

        return finalPath;
    }

	public void RecordSample(
		RideState rideState,
		TrainerTelemetry telemetry,
		double gradientPercent,
		RouteSample? routeSample = null)
	{
		if (_encoder is null || !IsRecording)
			return;

		var now = DateTimeOffset.UtcNow;

		var elevationDeltaMeters = _progress.Advance(
			rideState.DistanceMeters, gradientPercent, routeSample?.ElevationMeters);

		var record = new RecordMesg();
		record.SetTimestamp(ToFitTime(now));
		record.SetDistance((float)_progress.DistanceMeters);
		record.SetSpeed((float)(rideState.VirtualSpeedKph / 3.6));
		record.SetPower(ToUShort(telemetry.PowerWatts));
		record.SetCadence(ToByte(telemetry.CadenceRpm));
		record.SetGrade((float)gradientPercent);
		record.SetAltitude((float)_progress.AltitudeMeters);

		if (routeSample?.Latitude is not null && routeSample.Longitude is not null)
		{
			record.SetPositionLat(DegreesToSemicircles(routeSample.Latitude.Value));
			record.SetPositionLong(DegreesToSemicircles(routeSample.Longitude.Value));
		}

		if (telemetry.HeartRateBpm.HasValue)
		{
			record.SetHeartRate(ToByte(telemetry.HeartRateBpm.Value));
		}

		_encoder.Write(record);
		_recordCount++;

		GameLogger.Log("ride_recording_sample", new
		{
			filePath = FilePath,
			distanceMeters = _progress.DistanceMeters,
			speedKph = rideState.VirtualSpeedKph,
			powerWatts = telemetry.PowerWatts,
			cadenceRpm = telemetry.CadenceRpm,
			heartRateBpm = telemetry.HeartRateBpm,
			gradientPercent,
			altitudeMeters = _progress.AltitudeMeters,
			elevationDeltaMeters,
			totalAscentMeters = _progress.AscentMeters,
			totalDescentMeters = _progress.DescentMeters,
			routeLoopDistanceMeters = routeSample?.LoopDistanceMeters,
			routeLatitude = routeSample?.Latitude,
			routeLongitude = routeSample?.Longitude,
			routeElevationMeters = routeSample?.ElevationMeters,
			routeSegmentLabel = routeSample?.SegmentLabel
		});
	}

	private static int DegreesToSemicircles(double degrees)
	{
		return (int)Math.Round(degrees * (int.MaxValue / 180.0));
	}

	private void StartNewFile()
	{
		var outputDir = ResolveOutputFolder(GameBootstrap.Settings.Recording.OutputFolder);
		Directory.CreateDirectory(outputDir);

		FilePath = Path.Combine(outputDir, $"gwift-{System.DateTime.Now:yyyyMMdd-HHmmss}.fit");

		_fitStream = new FileStream(FilePath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        _encoder = new Encode(ProtocolVersion.V20);
        _encoder.Open(_fitStream);

		_startedAtUtc = DateTimeOffset.UtcNow;
		_currentResumeStartedAtUtc = _startedAtUtc;
		_activeTimeBeforeCurrentResume = TimeSpan.Zero;

		_progress = new RecordingProgress();
		_recordCount = 0;

		IsRecording = true;
		IsPaused = false;

		WriteFileId();
		WriteTimerEvent(_startedAtUtc, EventType.Start);

		GameLogger.Log("ride_recording_started", new { filePath = FilePath });
	}

	private void WriteFileId()
	{
		var fileId = new FileIdMesg();
		fileId.SetType(FitFile.Activity);
		fileId.SetManufacturer(Manufacturer.Development);
		fileId.SetProduct(1);
		fileId.SetSerialNumber((uint)Environment.MachineName.GetHashCode());
		fileId.SetTimeCreated(ToFitTime(_startedAtUtc));

		_encoder!.Write(fileId);
	}

	private void WriteTimerEvent(DateTimeOffset timestamp, EventType eventType)
	{
		var evt = new EventMesg();
		evt.SetTimestamp(ToFitTime(timestamp));
		evt.SetEvent(Event.Timer);
		evt.SetEventType(eventType);

		_encoder!.Write(evt);
	}

	private void WriteLap(DateTimeOffset timestamp, TimeSpan totalElapsedTime, TimeSpan totalTimerTime)
	{
		var lap = new LapMesg();
		lap.SetTimestamp(ToFitTime(timestamp));
		lap.SetStartTime(ToFitTime(_startedAtUtc));
		lap.SetEvent(Event.Lap);
		lap.SetEventType(EventType.Stop);
		lap.SetTotalElapsedTime((float)totalElapsedTime.TotalSeconds);
		lap.SetTotalTimerTime((float)totalTimerTime.TotalSeconds);
		lap.SetTotalDistance((float)_progress.DistanceMeters);
		lap.SetTotalAscent((ushort)Math.Clamp(Math.Round(_progress.AscentMeters), ushort.MinValue, ushort.MaxValue));
		lap.SetTotalDescent((ushort)Math.Clamp(Math.Round(_progress.DescentMeters), ushort.MinValue, ushort.MaxValue));

		_encoder!.Write(lap);
	}

	private void WriteSession(DateTimeOffset timestamp, TimeSpan totalElapsedTime, TimeSpan totalTimerTime)
	{
		var session = new SessionMesg();
		session.SetTimestamp(ToFitTime(timestamp));
		session.SetStartTime(ToFitTime(_startedAtUtc));
		session.SetEvent(Event.Lap);
		session.SetEventType(EventType.Stop);
		session.SetSport(Sport.Cycling);
		session.SetSubSport(SubSport.Generic);
		session.SetFirstLapIndex(0);
		session.SetNumLaps(1);
		session.SetTotalElapsedTime((float)totalElapsedTime.TotalSeconds);
		session.SetTotalTimerTime((float)totalTimerTime.TotalSeconds);
		session.SetTotalDistance((float)_progress.DistanceMeters);
		session.SetTotalAscent((ushort)Math.Clamp(Math.Round(_progress.AscentMeters), ushort.MinValue, ushort.MaxValue));
		session.SetTotalDescent((ushort)Math.Clamp(Math.Round(_progress.DescentMeters), ushort.MinValue, ushort.MaxValue));
		session.SetTrigger(SessionTrigger.ActivityEnd);

		_encoder!.Write(session);
	}

	private void WriteActivity(DateTimeOffset timestamp, TimeSpan totalTimerTime)
	{
		var activity = new ActivityMesg();
		activity.SetTimestamp(ToFitTime(timestamp));
		activity.SetTotalTimerTime((float)totalTimerTime.TotalSeconds);
		activity.SetNumSessions(1);
		activity.SetType(FitActivityType.Manual);
		activity.SetEvent(Event.Activity);
		activity.SetEventType(EventType.Stop);

		_encoder!.Write(activity);
	}

    private static string ResolveOutputFolder(string configuredPath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            return Path.Combine(documents, "gwift");
        }

        var expanded = configuredPath
            .Replace("%USERPROFILE%", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))
            .Replace("%DOCUMENTS%", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));

        return Environment.ExpandEnvironmentVariables(expanded);
    }

	private static FitDateTime ToFitTime(DateTimeOffset timestamp)
	{
		return new FitDateTime(timestamp.UtcDateTime);
	}

	private static ushort ToUShort(int value)
	{
		return (ushort)Math.Clamp(value, ushort.MinValue, ushort.MaxValue);
	}

	private static byte ToByte(int value)
	{
		return (byte)Math.Clamp(value, byte.MinValue, 254);
	}
}