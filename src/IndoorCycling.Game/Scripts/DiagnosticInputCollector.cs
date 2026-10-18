using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

public sealed record DiagnosticLogSource(string Path, string ArchiveName, bool IsJsonLines);
public sealed record DiagnosticInputCollection(IReadOnlyList<(string Name, byte[] Data)> Files, IReadOnlyList<string> Omissions);

/// <summary>Collects bounded diagnostic inputs while reserving archive space for current logs before history.</summary>
public static class DiagnosticInputCollector
{
	private static readonly UTF8Encoding StrictUtf8 = new(false, true);

	public static DiagnosticInputCollection Collect(
		IEnumerable<(string Name, byte[] Data)> reservedFiles,
		IEnumerable<DiagnosticLogSource> currentLogs,
		IEnumerable<DiagnosticLogSource> olderLogs,
		string? userProfilePath,
		int maxTotalBytes,
		int maxLogBytes,
		int maxFileBytes,
		int manifestReserveBytes = 32 * 1024)
	{
		if (maxTotalBytes <= 0 || maxLogBytes < 0 || maxLogBytes > maxTotalBytes || maxFileBytes <= 0 || manifestReserveBytes < 0)
			throw new ArgumentOutOfRangeException(nameof(maxTotalBytes));

		var files = reservedFiles.Select(file => (file.Name, file.Data)).ToList();
		var reservedBytes = files.Sum(file => (long)file.Data.Length);
		if (reservedBytes > maxTotalBytes - manifestReserveBytes)
			throw new InvalidDataException("Reserved diagnostic files exceed the archive budget.");

		var omissions = new List<string>();
		var usedBytes = reservedBytes;
		var logBudget = (int)Math.Min(maxLogBytes, maxTotalBytes - manifestReserveBytes) - (int)reservedBytes;
		if (logBudget < 0) logBudget = 0;

		var current = DistinctSources(currentLogs, null);
		var older = DistinctSources(olderLogs, current);
		var availableCurrent = new List<(DiagnosticLogSource Source, long Length)>();
		foreach (var source in current)
		{
			try
			{
				if (!File.Exists(source.Path)) { omissions.Add($"{source.ArchiveName}: missing"); continue; }
				availableCurrent.Add((source, new FileInfo(source.Path).Length));
			}
			catch { omissions.Add($"{source.ArchiveName}: unreadable"); }
		}

		var remaining = logBudget;
		for (var i = 0; i < availableCurrent.Count; i++)
		{
			var (source, _) = availableCurrent[i];
			var slots = availableCurrent.Count - i;
			var allocation = Math.Min(maxFileBytes, (remaining + slots - 1) / slots);
			var read = TryRead(source, allocation, userProfilePath, out var data, out var details);
			if (!read) { omissions.Add($"{source.ArchiveName}: unreadable"); continue; }
			if (data.Length > 0)
			{
				files.Add((source.ArchiveName, data));
				usedBytes += data.Length;
				remaining -= data.Length;
			}
			if (details is not null) omissions.Add($"{source.ArchiveName}: {details}");
			if (data.Length == 0 && details is null) omissions.Add($"{source.ArchiveName}: no complete records available");
		}

		foreach (var source in older)
		{
			if (remaining <= 0) { omissions.Add($"{source.ArchiveName}: omitted (size limit reached)"); continue; }
			if (!File.Exists(source.Path)) { omissions.Add($"{source.ArchiveName}: missing"); continue; }
			var allocation = Math.Min(remaining, maxFileBytes);
			try
			{
				if (!TryRead(source, allocation, userProfilePath, out var data, out var details))
				{
					omissions.Add($"{source.ArchiveName}: unreadable");
					continue;
				}
				if (data.Length > 0)
				{
					files.Add((source.ArchiveName, data));
					usedBytes += data.Length;
					remaining -= data.Length;
				}
				if (details is not null) omissions.Add($"{source.ArchiveName}: {details}");
				if (data.Length == 0 && details is null) omissions.Add($"{source.ArchiveName}: no complete records available");
			}
			catch { omissions.Add($"{source.ArchiveName}: unreadable"); }
		}

		if (usedBytes > maxTotalBytes - manifestReserveBytes)
			throw new InvalidDataException("Collected diagnostic files exceeded the reserved archive budget.");
		return new(files, omissions);
	}

	private static List<DiagnosticLogSource> DistinctSources(IEnumerable<DiagnosticLogSource> sources, IEnumerable<DiagnosticLogSource>? exclude)
	{
		var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		if (exclude is not null)
			foreach (var source in exclude) paths.Add(NormalizePath(source.Path));
		var result = new List<DiagnosticLogSource>();
		foreach (var source in sources)
			if (paths.Add(NormalizePath(source.Path))) result.Add(source);
		return result;
	}

	private static string NormalizePath(string path)
	{
		try { return System.IO.Path.GetFullPath(path); }
		catch { return path; }
	}

	private static bool TryRead(DiagnosticLogSource source, int limit, string? userProfilePath, out byte[] data, out string? details)
	{
		data = [];
		details = null;
		if (limit <= 0) { details = "size limit reached"; return true; }
		try
		{
			using var stream = new FileStream(source.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
			var length = stream.Length;
			var truncatedAtStart = length > limit;
			var start = truncatedAtStart ? length - limit : 0;
			stream.Position = start;
			var raw = new byte[(int)Math.Min(limit, length - start)];
			var read = 0;
			while (read < raw.Length)
			{
				var count = stream.Read(raw, read, raw.Length - read);
				if (count == 0) break;
				read += count;
			}
			if (read != raw.Length) Array.Resize(ref raw, read);
			var content = source.IsJsonLines
				? SanitizeJsonLines(raw, truncatedAtStart, userProfilePath, out var jsonDetails)
				: SanitizeTextLog(raw, truncatedAtStart, userProfilePath, out jsonDetails);
			if (truncatedAtStart && jsonDetails is null) jsonDetails = "truncated tail to size limit";
			if (read != (int)Math.Min(limit, length - start)) jsonDetails = Combine(jsonDetails, "file changed while being read");
			details = jsonDetails;
			data = Encoding.UTF8.GetBytes(content);
			if (!source.IsJsonLines && data.Length > limit)
			{
				data = TrimSanitizedTextTail(data, limit);
				details = Combine(details, "sanitized tail trimmed to size limit");
			}
			return true;
		}
		catch
		{
			return false;
		}
	}

	private static string SanitizeJsonLines(byte[] bytes, bool truncatedAtStart, string? userProfilePath, out string? details)
	{
		details = null;
		var start = 0;
		if (truncatedAtStart)
		{
			var firstNewline = Array.IndexOf(bytes, (byte)'\n');
			if (firstNewline < 0) { details = "tail contained no complete JSONL records"; return string.Empty; }
			start = firstNewline + 1;
			details = "truncated tail; partial first record omitted";
		}

		var lastNewline = Array.LastIndexOf(bytes, (byte)'\n');
		if (lastNewline < start)
		{
			details = Combine(details, "incomplete trailing JSONL record omitted");
			return string.Empty;
		}
		var completeLength = lastNewline + 1 - start;
		var text = DecodeUtf8(bytes.AsSpan(start, completeLength));
		if (start == 0) text = text.TrimStart('\uFEFF');
		var sanitizedRecords = new List<string>();
		var invalidRecords = 0;
		foreach (var line in text.Split('\n'))
		{
			if (line.Length == 0) continue;
			var record = line.EndsWith('\r') ? line[..^1] : line;
			try
			{
				using var parsed = JsonDocument.Parse(record);
				sanitizedRecords.Add(DiagnosticLogSanitizer.RedactJsonLine(record, userProfilePath));
			}
			catch (JsonException) { invalidRecords++; }
		}
		if (lastNewline < bytes.Length - 1)
			details = Combine(details, "incomplete trailing JSONL record omitted");
		if (invalidRecords > 0) details = Combine(details, $"{invalidRecords} malformed JSONL record(s) omitted");
		var kept = new List<string>();
		var remainingBytes = bytes.Length;
		for (var i = sanitizedRecords.Count - 1; i >= 0; i--)
		{
			var record = sanitizedRecords[i];
			var recordBytes = Encoding.UTF8.GetByteCount(record) + 1;
			if (recordBytes > remainingBytes)
			{
				details = Combine(details, "sanitized JSONL records trimmed to fit size limit");
				continue;
			}
			kept.Add(record);
			remainingBytes -= recordBytes;
		}
		kept.Reverse();
		return kept.Count == 0 ? string.Empty : string.Join("\n", kept) + "\n";
	}

	private static string SanitizeTextLog(byte[] bytes, bool truncatedAtStart, string? userProfilePath, out string? details)
	{
		details = truncatedAtStart ? "truncated tail to size limit" : null;
		var start = 0;
		if (truncatedAtStart)
		{
			while (start < bytes.Length && (bytes[start] & 0xC0) == 0x80) start++;
			var newline = Array.IndexOf(bytes, (byte)'\n', start);
			if (newline >= 0) start = newline + 1;
		}
		var end = bytes.Length;
		string text;
		while (true)
		{
			try { text = StrictUtf8.GetString(bytes, start, end - start); break; }
			catch (DecoderFallbackException) when (end > start) { end--; details = Combine(details, "incomplete UTF-8 tail omitted"); }
			catch (DecoderFallbackException) { text = string.Empty; break; }
		}
		return DiagnosticLogSanitizer.Redact(text, userProfilePath);
	}

	private static string DecodeUtf8(ReadOnlySpan<byte> bytes)
	{
		var length = bytes.Length;
		while (length > 0)
		{
			try { return StrictUtf8.GetString(bytes[..length]); }
			catch (DecoderFallbackException) { length--; }
		}
		return string.Empty;
	}

	private static byte[] TrimSanitizedTextTail(byte[] bytes, int limit)
	{
		var start = Math.Max(0, bytes.Length - limit);
		while (start < bytes.Length && (bytes[start] & 0xC0) == 0x80) start++;
		var newline = Array.IndexOf(bytes, (byte)'\n', start);
		if (newline >= 0 && newline + 1 < bytes.Length) start = newline + 1;
		return bytes[start..];
	}

	private static string Combine(string? first, string next) => string.IsNullOrWhiteSpace(first) ? next : $"{first}; {next}";
}
