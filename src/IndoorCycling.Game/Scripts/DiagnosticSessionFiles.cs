using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

public sealed record DiagnosticSessionFile(string SourcePath, string ArchiveName, DiagnosticSessionFileKind Kind);

public enum DiagnosticSessionFileKind { JsonLines, PlainText, MarkerJson, ScreenshotPng }

public static class DiagnosticSessionFiles
{
	public static IReadOnlyList<DiagnosticSessionFile> Discover(DiagnosticSessionDescriptor session,
		string userDataDirectory, string picturesDirectory, IReadOnlyList<DiagnosticSessionDescriptor> sessions,
		string currentSessionId, bool includeScreenshots)
	{
		var files = new List<DiagnosticSessionFile>();
		var logs = Path.Combine(userDataDirectory, "logs");
		var wodraLog = Path.Combine(logs, session.WodraLogFile);
		files.Add(new(wodraLog, $"session/{Path.GetFileName(wodraLog)}", DiagnosticSessionFileKind.JsonLines));

		var godotLogs = DiagnosticSessionCatalog.FindGodotLogs(userDataDirectory, session, sessions, currentSessionId);
		foreach (var godotLog in godotLogs)
			files.Add(new(godotLog, $"godot/{Path.GetFileName(godotLog)}", DiagnosticSessionFileKind.PlainText));
		if (godotLogs.Count == 0)
		{
			var expected = Path.Combine(logs, "godot" + session.StartedAtUtc.ToLocalTime().ToString("yyyy-MM-dd'T'HH.mm.ss", CultureInfo.InvariantCulture) + ".log");
			files.Add(new(expected, $"godot/{Path.GetFileName(expected)}", DiagnosticSessionFileKind.PlainText));
		}

		var directories = new[]
		{
			string.IsNullOrWhiteSpace(picturesDirectory) ? null : Path.Combine(picturesDirectory, "Wodra"),
			Path.Combine(userDataDirectory, "screenshots")
		}.Where(path => path is not null).Distinct(StringComparer.OrdinalIgnoreCase).Cast<string>();
		foreach (var directory in directories)
		{
			try
			{
				if (!Directory.Exists(directory)) continue;
				foreach (var sidecar in Directory.GetFiles(directory, "wodra-*-marker-*.json"))
				{
					if (!IsForSession(sidecar, session)) continue;
					var stem = Path.GetFileNameWithoutExtension(sidecar);
					files.Add(new(sidecar, $"markers/{stem}.json", DiagnosticSessionFileKind.MarkerJson));
					var png = Path.Combine(directory, stem + ".png");
					files.Add(new(png, $"markers/{stem}.png", DiagnosticSessionFileKind.ScreenshotPng));
				}
			}
			catch { }
		}
		return files.Where(file => includeScreenshots || file.Kind != DiagnosticSessionFileKind.ScreenshotPng)
			.GroupBy(file => Path.GetFullPath(file.SourcePath), StringComparer.OrdinalIgnoreCase)
			.Select(group => group.First()).OrderBy(file => file.ArchiveName, StringComparer.OrdinalIgnoreCase).ToArray();
	}

	public static bool HasMarkerFiles(DiagnosticSessionDescriptor session, string userDataDirectory, string picturesDirectory)
		=> Discover(session, userDataDirectory, picturesDirectory, [session], session.Id, includeScreenshots: true)
			.Any(file => file.Kind == DiagnosticSessionFileKind.ScreenshotPng);

	private static bool IsForSession(string sidecar, DiagnosticSessionDescriptor session)
	{
		try
		{
			using var stream = new FileStream(sidecar, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
			if (stream.Length > 1024 * 1024) return false;
			using var json = JsonDocument.Parse(stream);
			var root = json.RootElement;
			if (root.TryGetProperty("sessionId", out var id) && id.ValueKind == JsonValueKind.String)
				return string.Equals(id.GetString(), session.Id, StringComparison.OrdinalIgnoreCase);
			if (!root.TryGetProperty("timestamp", out var timestamp) || !timestamp.TryGetDateTimeOffset(out var capturedAt)) return false;
			var end = session.EndedAtUtc ?? DateTimeOffset.UtcNow;
			return capturedAt >= session.StartedAtUtc.AddMinutes(-2) && capturedAt <= end.AddMinutes(2);
		}
		catch { return false; }
	}
}
