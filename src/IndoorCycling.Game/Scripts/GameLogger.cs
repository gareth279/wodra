
using System.IO;

using System.Text.Json;
using Godot;

public static class GameLogger
{
	private static readonly object LockObject = new();
	private static bool _initialised;

	public static string? LogPath { get; private set; }

	public static void Init()
	{
		if (_initialised)
			return;

		var logDir = ProjectSettings.GlobalizePath("user://logs");
		Directory.CreateDirectory(logDir);

		LogPath = Path.Combine(logDir, $"gwift-{System.DateTime.Now:yyyyMMdd-HHmmss}.jsonl");
		_initialised = true;

		Log("logger_started", new { logPath = LogPath });
		GD.Print($"GWift log: {LogPath}");
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