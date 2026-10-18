using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Godot;

public sealed record DiagnosticExportResult(bool Success, string Message, string? Path = null,
	bool RequiresReducedExport = false, long EstimatedBytes = 0);

/// <summary>Builds one selected application session bundle without loading whole logs or images into memory.</summary>
public static class DiagnosticSessionExporter
{
	private const long MaximumArchiveBytes = 64L * 1024 * 1024;
	private const long ManifestReserveBytes = 512L * 1024;
	private const long MaximumMarkerJsonBytes = 1024L * 1024;
	private static readonly UTF8Encoding StrictUtf8 = new(false, true);
	private static readonly UTF8Encoding OutputUtf8 = new(false);

	public static IReadOnlyList<DiagnosticSessionDescriptor> ListSessions()
	{
		var root = ProjectSettings.GlobalizePath("user://");
		var sessions = DiagnosticSessionCatalog.ListRecent(root, 30).ToList();
		var current = GameLogger.CurrentSession;
		if (current is not null && sessions.All(session => session.Id != current.Id)) sessions.Insert(0, current);
		return sessions;
	}

	public static async System.Threading.Tasks.Task<DiagnosticExportResult> ExportAsync(string sessionId,
		bool includeScreenshots, bool reduced = false)
	{
		var root = ProjectSettings.GlobalizePath("user://");
		var session = DiagnosticSessionCatalog.Load(root, sessionId) ??
			ListSessions().FirstOrDefault(item => item.Id == sessionId);
		if (session is null) return new(false, "The selected diagnostic session is no longer available.");

		GameLogger.Log("diagnostic_export_started", new { sessionId = session.Id, includeScreenshots, reduced });
		GameLogger.Flush();
		var sessions = ListSessions();
		var pictures = System.Environment.GetFolderPath(System.Environment.SpecialFolder.MyPictures);
		var discovered = DiagnosticSessionFiles.Discover(session, root, pictures, sessions,
			GameLogger.SessionId ?? string.Empty, includeScreenshots: true);
		var settings = BuildSanitizedSettings();
		var build = BuildContext(session);
		var sessionJson = JsonSerializer.Serialize(new
		{
			sessionId = session.Id,
			startedAtUtc = session.StartedAtUtc,
			endedAtUtc = session.EndedAtUtc,
			appVersion = session.AppVersion,
			debugModeEnabledAtStart = session.DebugModeEnabled
		}, new JsonSerializerOptions { WriteIndented = true });
		var reserved = new List<(string Name, string Data)>
		{
			("build.txt", build), ("settings-sanitized.json", settings), ("session.json", sessionJson)
		};
		var exclusions = new List<string>();
		if (!includeScreenshots)
			foreach (var screenshot in discovered.Where(file => file.Kind == DiagnosticSessionFileKind.ScreenshotPng))
				exclusions.Add($"{screenshot.ArchiveName}: excluded by selection");
		var sources = discovered.Where(file => file.Kind != DiagnosticSessionFileKind.ScreenshotPng || includeScreenshots)
			.OrderBy(file => file.Kind switch
			{
				DiagnosticSessionFileKind.JsonLines => 0,
				DiagnosticSessionFileKind.PlainText => 1,
				DiagnosticSessionFileKind.MarkerJson => 2,
				_ => 3
			}).ToArray();
		var contextBytes = reserved.Sum(item => (long)OutputUtf8.GetByteCount(item.Data));
		var estimate = contextBytes + ManifestReserveBytes;
		foreach (var source in sources)
		{
			try { if (File.Exists(source.SourcePath)) estimate += new FileInfo(source.SourcePath).Length; }
			catch { }
		}
		if (!reduced && estimate > MaximumArchiveBytes - ManifestReserveBytes)
			return new(false,
				$"The complete session is about {estimate / (1024 * 1024)} MB, above the {MaximumArchiveBytes / (1024 * 1024)} MB archive limit. You can create an explicitly reduced bundle with bounded log tails and screenshots omitted.",
				RequiresReducedExport: true, EstimatedBytes: estimate);

		var outputDirectory = Path.Combine(root, "diagnostics");
		var tempDirectory = Path.Combine(Path.GetTempPath(), "WodraDiagnosticExport", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(outputDirectory);
		Directory.CreateDirectory(tempDirectory);
		var omissions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		var processed = new List<(string ArchiveName, string TempPath, long Length)>();
		try
		{
			foreach (var text in exclusions) AddOmission(omissions, text);
			foreach (var source in sources)
			{
				if (reduced && source.Kind == DiagnosticSessionFileKind.ScreenshotPng)
				{
					AddOmission(omissions, $"{source.ArchiveName}: excluded by reduced export");
					continue;
				}
				long length;
				try
				{
					if (!File.Exists(source.SourcePath)) { AddOmission(omissions, $"{source.ArchiveName}: missing"); continue; }
					using var probe = new FileStream(source.SourcePath, FileMode.Open, System.IO.FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
					length = probe.Length;
					if (length == 0) { AddOmission(omissions, $"{source.ArchiveName}: empty"); continue; }
				}
				catch { AddOmission(omissions, $"{source.ArchiveName}: unreadable"); continue; }

				var destination = Path.Combine(tempDirectory, $"{processed.Count:D5}.data");
				var budget = reduced ? BudgetFor(source, processed, contextBytes) : long.MaxValue;
				if (reduced && budget <= 0)
				{
					AddOmission(omissions, $"{source.ArchiveName}: omitted (reduced export size limit)");
					continue;
				}
				try
				{
					var wasTruncated = SanitizeToFile(source, destination, length, budget, out var sanitizeDetails);
					var outputLength = new FileInfo(destination).Length;
					if (outputLength == 0)
					{
						File.Delete(destination);
						AddOmission(omissions, $"{source.ArchiveName}: no complete records available");
						continue;
					}
					if (wasTruncated) AddOmission(omissions, $"{source.ArchiveName}: truncated to a bounded tail");
					if (sanitizeDetails is not null) AddOmission(omissions, $"{source.ArchiveName}: {sanitizeDetails}");
					processed.Add((source.ArchiveName, destination, outputLength));
				}
				catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or DecoderFallbackException)
				{
					try { File.Delete(destination); } catch { }
					AddOmission(omissions, $"{source.ArchiveName}: unreadable or invalid UTF-8/JSON");
				}
			}

			if (!includeScreenshots && sources.Any(source => source.Kind == DiagnosticSessionFileKind.ScreenshotPng))
				AddOmission(omissions, "screenshots: excluded by selection; marker JSON is retained");
			if (reduced) AddOmission(omissions, "bundle: reduced export; screenshots excluded and oversized logs contain bounded tails");
		var manifestPath = Path.Combine(tempDirectory, "manifest.txt");
		var manifest = BuildManifest(session, omissions);
		File.WriteAllText(manifestPath, manifest, OutputUtf8);
		var manifestLength = new FileInfo(manifestPath).Length;
		var files = reserved.Select(item => WriteReserved(item.Name, item.Data, tempDirectory)).ToList();
		files.AddRange(processed.Select(item => (item.ArchiveName, item.TempPath, item.Length)));
		files.Add(("manifest.txt", manifestPath, manifestLength));
		var totalBytes = files.Sum(file => file.Length);
		if (totalBytes > MaximumArchiveBytes)
		{
			if (!reduced)
				return new(false, "Sanitisation expanded the complete session beyond the archive limit. Choose a reduced export.",
					RequiresReducedExport: true, EstimatedBytes: totalBytes);
			return new(false, "The reduced session bundle still exceeds the archive limit. No archive was written.", EstimatedBytes: totalBytes);
		}
		var suffix = reduced ? "-reduced" : string.Empty;
		var filePath = Path.Combine(outputDirectory,
			$"wodra-diagnostics-{session.StartedAtUtc.ToLocalTime():yyyyMMdd-HHmmss}-{session.Id[..Math.Min(8, session.Id.Length)]}{suffix}.zip");
		await System.Threading.Tasks.Task.Run(() => DiagnosticArchiveWriter.WriteStreaming(filePath, files, MaximumArchiveBytes));
		return new(true, "Diagnostic session bundle created.", filePath, EstimatedBytes: totalBytes);
		}
		catch (Exception ex)
		{
			return new(false, $"Could not export diagnostics: {ex.Message}");
		}
		finally
		{
			try { Directory.Delete(tempDirectory, recursive: true); } catch { }
		}
	}

	private static long BudgetFor(DiagnosticSessionFile source, IReadOnlyList<(string ArchiveName, string TempPath, long Length)> processed, long contextBytes)
	{
		var used = contextBytes + processed.Sum(file => file.Length);
		var remaining = MaximumArchiveBytes - ManifestReserveBytes - used;
		if (remaining <= 0) return 0;
		var perFile = source.Kind switch
		{
			DiagnosticSessionFileKind.JsonLines => 24L * 1024 * 1024,
			DiagnosticSessionFileKind.PlainText => 8L * 1024 * 1024,
			DiagnosticSessionFileKind.MarkerJson => MaximumMarkerJsonBytes,
			_ => 0
		};
		return Math.Min(remaining, perFile);
	}

	private static bool SanitizeToFile(DiagnosticSessionFile source, string destination, long snapshotLength,
		long outputBudget, out string? sanitizeDetails)
	{
		sanitizeDetails = null;
		using var input = new FileStream(source.SourcePath, FileMode.Open, System.IO.FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
		var actualLength = Math.Min(snapshotLength, input.Length);
		if (actualLength <= 0) return false;
		using var output = new FileStream(destination, FileMode.CreateNew, System.IO.FileAccess.Write, FileShare.None);
		if (source.Kind == DiagnosticSessionFileKind.ScreenshotPng)
		{
			CopyBounded(input, output, actualLength);
			return false;
		}
		if (source.Kind == DiagnosticSessionFileKind.MarkerJson)
		{
			if (actualLength > MaximumMarkerJsonBytes) throw new InvalidDataException("Marker metadata exceeded its size limit.");
			using var limited = new LengthLimitedStream(input, actualLength);
			using var reader = new StreamReader(limited, StrictUtf8, detectEncodingFromByteOrderMarks: true, 4096, leaveOpen: true);
			var raw = reader.ReadToEnd();
			using var parsedMarker = JsonDocument.Parse(raw);
			var safe = DiagnosticLogSanitizer.RedactJsonLine(raw, System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile));
			var bytes = OutputUtf8.GetBytes(safe + "\n");
			if (bytes.LongLength > outputBudget) throw new InvalidDataException("Marker metadata exceeded the reduced export budget.");
			output.Write(bytes);
			return false;
		}

		var tailStart = outputBudget == long.MaxValue ? 0 : Math.Max(0, actualLength - outputBudget);
		var truncated = tailStart > 0;
		input.Position = tailStart;
		if (tailStart > 0) SkipPartialLine(input, actualLength);
		var remainingInput = actualLength - input.Position;
		var limitedInput = new LengthLimitedStream(input, remainingInput);
		using var textReader = new StreamReader(limitedInput, StrictUtf8, detectEncodingFromByteOrderMarks: tailStart == 0, 64 * 1024, leaveOpen: true);
		var lines = new System.Collections.Generic.Queue<(string Value, int Bytes)>();
		long bufferedBytes = 0;
		var invalidJsonLines = 0;
		string? pending = null;
		var sourceEndsInNewline = EndsWithNewline(input, actualLength);
		while (true)
		{
			var line = textReader.ReadLine();
			if (line is null) break;
			if (pending is not null) AddLine(pending, complete: true);
			pending = line;
		}
		if (pending is not null)
		{
			if (source.Kind == DiagnosticSessionFileKind.JsonLines && !sourceEndsInNewline)
				truncated = true;
			else AddLine(pending, complete: true);
		}
		if (invalidJsonLines > 0) sanitizeDetails = $"{invalidJsonLines} malformed JSONL record(s) omitted";
		while (lines.Count > 0)
		{
			var line = lines.Dequeue();
			var bytes = OutputUtf8.GetBytes(line.Value + "\n"); output.Write(bytes);
		}
		return truncated;

		void AddLine(string line, bool complete)
		{
			if (!complete || line.Length == 0) return;
			string safe;
			if (source.Kind == DiagnosticSessionFileKind.JsonLines)
			{
				try
				{
					using var parsed = JsonDocument.Parse(line);
					safe = DiagnosticLogSanitizer.RedactJsonLine(line, System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile));
				}
				catch (JsonException) { invalidJsonLines++; return; }
			}
			else safe = DiagnosticLogSanitizer.Redact(line, System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile));
			var bytes = OutputUtf8.GetByteCount(safe) + 1;
			if (bytes > outputBudget) { truncated = true; return; }
			if (outputBudget == long.MaxValue)
			{
				var outputBytes = OutputUtf8.GetBytes(safe + "\n");
				output.Write(outputBytes);
				return;
			}
			lines.Enqueue((safe, bytes)); bufferedBytes += bytes;
			while (bufferedBytes > outputBudget && lines.Count > 0)
			{
				bufferedBytes -= lines.Dequeue().Bytes;
				truncated = true;
			}
		}
	}

	private static void SkipPartialLine(Stream stream, long limit)
	{
		var buffer = new byte[64 * 1024];
		while (stream.Position < limit)
		{
			var read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, limit - stream.Position));
			if (read == 0) break;
			var newline = Array.IndexOf(buffer, (byte)'\n', 0, read);
			if (newline >= 0)
			{
				stream.Position -= read - newline - 1;
				break;
			}
		}
	}

	private static bool EndsWithNewline(Stream stream, long length)
	{
		if (length <= 0) return false;
		var position = stream.Position;
		stream.Position = length - 1;
		var value = stream.ReadByte();
		stream.Position = position;
		return value == (byte)'\n';
	}

	private static void CopyBounded(Stream input, Stream output, long length)
	{
		var remaining = length;
		var buffer = new byte[64 * 1024];
		while (remaining > 0)
		{
			var read = input.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
			if (read == 0) throw new EndOfStreamException("A diagnostic file changed while being read.");
			output.Write(buffer, 0, read); remaining -= read;
		}
	}

	private static (string Name, string TempPath, long Length) WriteReserved(string name, string value, string tempDirectory)
	{
		var path = Path.Combine(tempDirectory, name.Replace('/', '_'));
		File.WriteAllText(path, value, OutputUtf8);
		return (name, path, new FileInfo(path).Length);
	}

	private static void AddOmission(IDictionary<string, string> omissions, string item)
	{
		var split = item.IndexOf(": ", StringComparison.Ordinal);
		var name = split < 0 ? item : item[..split];
		var detail = split < 0 ? "omitted" : item[(split + 2)..];
		if (!omissions.TryAdd(name, detail) && !omissions[name].Contains(detail, StringComparison.OrdinalIgnoreCase))
			omissions[name] += "; " + detail;
	}

	private static string BuildManifest(DiagnosticSessionDescriptor session, IReadOnlyDictionary<string, string> omissions)
	{
		var builder = new StringBuilder();
		builder.AppendLine("Wodra local diagnostic session export");
		builder.AppendLine($"Session: {session.Id}");
		builder.AppendLine($"Started UTC: {session.StartedAtUtc:O}");
		builder.AppendLine($"Ended UTC: {(session.EndedAtUtc?.ToString("O", CultureInfo.InvariantCulture) ?? "active snapshot")}");
		builder.AppendLine($"Version: {session.AppVersion}");
		builder.AppendLine("Only the selected application session is included. FIT files, activity uploads, credentials and identity caches are excluded.");
		builder.AppendLine("Marker JSON is sanitised. PNG screenshots are not anonymised and may contain visible rider names; deselect them before sharing if needed.");
		builder.AppendLine("Files changed during an active session are snapshotted at their initial readable length. Original evidence files were not modified.");
		if (omissions.Count == 0) builder.AppendLine("Omissions: none");
		else
		{
			builder.AppendLine("Omitted or truncated files:");
			foreach (var item in omissions.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
				builder.AppendLine($"- {item.Key}: {item.Value}");
		}
		return builder.ToString();
	}

	private static string BuildContext(DiagnosticSessionDescriptor session)
	{
		var godot = Engine.GetVersionInfo();
		return $"version={session.AppVersion}\nsessionId={session.Id}\nos={OS.GetName()} {OS.GetVersion()}\n" +
			$"runtime={System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}\n" +
			$"architecture={System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}\n" +
			$"godot={godot.GetValueOrDefault("string", "unknown").AsString()}\n" +
			$"createdUtc={DateTimeOffset.UtcNow:O}\n";
	}

	private static string BuildSanitizedSettings()
	{
		try
		{
			using var document = JsonDocument.Parse(File.ReadAllText(AppSettings.SettingsPath));
			var root = document.RootElement;
			var safe = new Dictionary<string, object?>
			{
				["DebugModeEnabled"] = Bool(root, "DebugModeEnabled"),
				["OnlineRidingEnabled"] = Bool(root, "OnlineRidingEnabled")
			};
			if (root.TryGetProperty("Steering", out var steering))
				safe["Steering"] = new { Sensitivity = Number(steering, "Sensitivity"), DeadZoneDegrees = Number(steering, "DeadZoneDegrees") };
			if (root.TryGetProperty("Display", out var display))
				safe["Display"] = new { RouteGradientInsetVisible = Bool(display, "RouteGradientInsetVisible") };
			if (root.TryGetProperty("Trainer", out var trainer) && trainer.TryGetProperty("ProtocolPreference", out var protocol))
				safe["Trainer"] = new { ProtocolPreference = protocol.ValueKind == JsonValueKind.String ? protocol.GetString() : null };
			return JsonSerializer.Serialize(safe, new JsonSerializerOptions { WriteIndented = true });
		}
		catch { return "{}"; }
	}

	private static bool Bool(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
	private static double? Number(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.TryGetDouble(out var number) && double.IsFinite(number) ? number : null;

	private sealed class LengthLimitedStream(Stream inner, long length) : Stream
	{
		private long _remaining = length;
		public override bool CanRead => true;
		public override bool CanSeek => false;
		public override bool CanWrite => false;
		public override long Length => length;
		public override long Position { get => length - _remaining; set => throw new NotSupportedException(); }
		public override int Read(byte[] buffer, int offset, int count)
		{
			if (_remaining <= 0) return 0;
			var read = inner.Read(buffer, offset, (int)Math.Min(count, _remaining)); _remaining -= read; return read;
		}
		public override int Read(Span<byte> buffer)
		{
			if (_remaining <= 0) return 0;
			var read = inner.Read(buffer[..(int)Math.Min(buffer.Length, _remaining)]); _remaining -= read; return read;
		}
		public override void Flush() => throw new NotSupportedException();
		public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
		public override void SetLength(long value) => throw new NotSupportedException();
		public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
		protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
	}
}
