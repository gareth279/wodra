
using System.IO;

using System.Text.Json;
using System;
using Godot;

public static class GameLogger
{
	private static readonly object LockObject = new();
	private static bool _initialised;

	public static string? LogPath { get; private set; }
	public static DiagnosticSessionDescriptor? CurrentSession { get; private set; }
	public static string? SessionId => CurrentSession?.Id;

	public static void Init()
	{
		if (_initialised)
			return;

		var logDir = ProjectSettings.GlobalizePath("user://logs");
		Directory.CreateDirectory(logDir);

		var sessionId = Guid.NewGuid().ToString("N");
		LogPath = Path.Combine(logDir, $"wodra-{System.DateTime.Now:yyyyMMdd-HHmmss}-{sessionId}.jsonl");
		var buildIdentity = BuildIdentity.Current;
		var version = buildIdentity.ApplicationVersion;
		CurrentSession = DiagnosticSessionCatalog.Start(OS.GetUserDataDir(), Path.GetFileName(LogPath), version,
			GameBootstrap.Settings?.DebugModeEnabled == true, DateTimeOffset.UtcNow);
		_initialised = true;

		Log("logger_started", new { sessionId = CurrentSession.Id, appVersion = version, buildInformation = buildIdentity.ToPlainText(), logPath = LogPath });
		GD.Print($"Wodra log: {LogPath}");
	}

	public static void Flush()
	{
		if (!_initialised || LogPath is null) return;
		try
		{
			lock (LockObject)
			{
				using var stream = new FileStream(LogPath, FileMode.OpenOrCreate, System.IO.FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
				stream.Flush(flushToDisk: true);
			}
		}
		catch { }
	}

	public static void EndSession()
	{
		if (CurrentSession is null) return;
		Log("diagnostic_session_ended", new { sessionId = CurrentSession.Id });
		Flush();
		var userDataDirectory = Path.GetDirectoryName(Path.GetDirectoryName(LogPath!)!)!;
		CurrentSession = DiagnosticSessionCatalog.End(userDataDirectory, CurrentSession.Id, DateTimeOffset.UtcNow) ?? CurrentSession;
	}

	public static void Log(string type, object? payload = null)
	{
		try
		{
			if (!_initialised)
				Init();

			var entry = new
			{
				ts = System.DateTimeOffset.UtcNow,
				sessionId = CurrentSession?.Id,
				type,
				payload
			};

			var json = JsonSerializer.Serialize(entry);

			lock (LockObject)
			{
				File.AppendAllText(LogPath!, json + System.Environment.NewLine);
			}
		}
		catch
		{
			// Never let logging crash the game.
		}
	}
}
