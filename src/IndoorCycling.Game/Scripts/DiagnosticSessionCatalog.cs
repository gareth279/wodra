using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

public sealed record DiagnosticSessionDescriptor(
	string Id,
	DateTimeOffset StartedAtUtc,
	DateTimeOffset? EndedAtUtc,
	string AppVersion,
	string WodraLogFile,
	bool DebugModeEnabled,
	bool IsLegacy = false)
{
	public string DisplayLabel(bool current) =>
		$"{(current ? "Current · " : "Recent · ")}{StartedAtUtc.ToLocalTime():g} · {AppVersion}";
}

/// <summary>Stable per-launch identities and recent-session discovery, independent of Godot UI.</summary>
public static class DiagnosticSessionCatalog
{
	private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

	public static DiagnosticSessionDescriptor Start(string userDataDirectory, string logFileName,
		string appVersion, bool debugModeEnabled, DateTimeOffset startedAtUtc)
	{
		var session = new DiagnosticSessionDescriptor(Guid.NewGuid().ToString("N"), startedAtUtc,
			null, appVersion, Path.GetFileName(logFileName), debugModeEnabled);
		Save(userDataDirectory, session);
		return session;
	}

	public static DiagnosticSessionDescriptor? Load(string userDataDirectory, string id)
	{
		if (string.IsNullOrWhiteSpace(id) || id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;
		try
		{
			var path = MetadataPath(userDataDirectory, id);
			if (!File.Exists(path)) return null;
			var session = JsonSerializer.Deserialize<DiagnosticSessionDescriptor>(File.ReadAllText(path));
			return session is not null && session.Id == id && IsSafeLogName(session.WodraLogFile) ? session : null;
		}
		catch { return null; }
	}

	public static DiagnosticSessionDescriptor? End(string userDataDirectory, string id, DateTimeOffset endedAtUtc)
	{
		var session = Load(userDataDirectory, id);
		if (session is null) return null;
		var ended = session with { EndedAtUtc = endedAtUtc < session.StartedAtUtc ? session.StartedAtUtc : endedAtUtc };
		Save(userDataDirectory, ended);
		return ended;
	}

	public static IReadOnlyList<DiagnosticSessionDescriptor> ListRecent(string userDataDirectory, int maximum = 12)
	{
		var sessions = new Dictionary<string, DiagnosticSessionDescriptor>(StringComparer.OrdinalIgnoreCase);
		var metadataDirectory = Path.Combine(userDataDirectory, "diagnostics", "sessions");
		try
		{
			if (Directory.Exists(metadataDirectory))
				foreach (var path in Directory.GetFiles(metadataDirectory, "*.json"))
				{
					try
					{
						var session = JsonSerializer.Deserialize<DiagnosticSessionDescriptor>(File.ReadAllText(path));
						if (session is not null && IsSafeId(session.Id) && IsSafeLogName(session.WodraLogFile))
							sessions[session.Id] = session;
					}
					catch { }
				}
		}
		catch { }

		var logDirectory = Path.Combine(userDataDirectory, "logs");
		try
		{
			if (Directory.Exists(logDirectory))
				foreach (var path in Directory.GetFiles(logDirectory, "wodra-*.jsonl"))
				{
					var name = Path.GetFileName(path);
					if (sessions.Values.Any(session => string.Equals(session.WodraLogFile, name, StringComparison.OrdinalIgnoreCase))) continue;
					DateTimeOffset started;
					try { started = new DateTimeOffset(File.GetCreationTimeUtc(path), TimeSpan.Zero); }
					catch { started = DateTimeOffset.MinValue; }
					if (started == DateTimeOffset.MinValue && TryParseLegacyTimestamp(name, out var parsed)) started = parsed;
					var id = "legacy-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name)))[..16].ToLowerInvariant();
					sessions[id] = new(id, started, File.GetLastWriteTimeUtc(path), "previous version", name, false, true);
				}
		}
		catch { }

		return sessions.Values.OrderByDescending(session => session.StartedAtUtc)
			.Take(Math.Max(1, maximum)).ToArray();
	}

	public static IReadOnlyList<string> FindGodotLogs(string userDataDirectory,
		DiagnosticSessionDescriptor session, IReadOnlyList<DiagnosticSessionDescriptor> allSessions,
		string currentSessionId)
	{
		var logDirectory = Path.Combine(userDataDirectory, "logs");
		var selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var isCurrent = string.Equals(session.Id, currentSessionId, StringComparison.OrdinalIgnoreCase);
		if (isCurrent)
		{
			var current = Path.Combine(logDirectory, "godot.log");
			selected.Add(current);
		}

		try
		{
			if (!Directory.Exists(logDirectory)) return selected.ToArray();
			foreach (var path in Directory.GetFiles(logDirectory, "godot*.log"))
			{
				if (string.Equals(Path.GetFileName(path), "godot.log", StringComparison.OrdinalIgnoreCase)) continue;
				if (!TryParseGodotTimestamp(Path.GetFileName(path), out var timestamp)) continue;
				// Godot stamps a rotated log with the process start time. Assign it to
				// the nearest Wodra launch so fast restarts cannot leak a neighboring session.
				var rotatedAt = new DateTimeOffset(timestamp);
				var closest = allSessions.Append(session)
					.GroupBy(candidate => candidate.Id, StringComparer.OrdinalIgnoreCase)
					.Select(group => group.First())
					.OrderBy(candidate => Math.Abs((candidate.StartedAtUtc - rotatedAt).TotalSeconds))
					.FirstOrDefault();
				if (closest?.Id == session.Id && Math.Abs((session.StartedAtUtc - rotatedAt).TotalMinutes) <= 5)
					selected.Add(path);
			}
		}
		catch { }
		return selected.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
	}

	private static void Save(string userDataDirectory, DiagnosticSessionDescriptor session)
	{
		var directory = Path.GetDirectoryName(MetadataPath(userDataDirectory, session.Id))!;
		Directory.CreateDirectory(directory);
		var path = MetadataPath(userDataDirectory, session.Id);
		var temp = path + ".tmp";
		File.WriteAllText(temp, JsonSerializer.Serialize(session, JsonOptions), new UTF8Encoding(false));
		File.Move(temp, path, true);
	}

	private static string MetadataPath(string userDataDirectory, string id) =>
		Path.Combine(userDataDirectory, "diagnostics", "sessions", id + ".json");

	private static bool IsSafeId(string id) => !string.IsNullOrWhiteSpace(id) &&
		id.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && !id.Contains("..", StringComparison.Ordinal);

	private static bool IsSafeLogName(string name) => !string.IsNullOrWhiteSpace(name) &&
		string.Equals(Path.GetFileName(name), name, StringComparison.Ordinal) && name.StartsWith("wodra-", StringComparison.OrdinalIgnoreCase) &&
		name.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase);

	private static bool TryParseLegacyTimestamp(string name, out DateTimeOffset parsed)
	{
		parsed = default;
		var start = "wodra-".Length;
		return name.Length >= start + 15 && DateTime.TryParseExact(name.Substring(start, 15), "yyyyMMdd-HHmmss",
			CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var local) &&
			(parsed = new DateTimeOffset(local)).UtcTicks > 0;
	}

	private static bool TryParseGodotTimestamp(string name, out DateTime local)
	{
		local = default;
		const string prefix = "godot";
		const string suffix = ".log";
		if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return false;
		var value = name.Substring(prefix.Length, name.Length - prefix.Length - suffix.Length);
		return DateTime.TryParseExact(value, "yyyy-MM-dd'T'HH.mm.ss", CultureInfo.InvariantCulture,
			DateTimeStyles.AssumeLocal, out local);
	}
}
