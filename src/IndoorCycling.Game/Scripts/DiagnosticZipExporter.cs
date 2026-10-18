using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Godot;

public sealed record DiagnosticExportResult(bool Success, string Message, string? Path = null);

public static class DiagnosticZipExporter
{
	private const int MaxTotalBytes = 8 * 1024 * 1024;
	private const int MaxLogBytes = MaxTotalBytes - 16 * 1024;
	private const int MaxArchiveBytes = MaxTotalBytes + 256 * 1024;
	private const int MaxFileBytes = 2 * 1024 * 1024;

	public static async System.Threading.Tasks.Task<DiagnosticExportResult> ExportAsync()
	{
		try
		{
			var outputDirectory = Path.Combine(ProjectSettings.GlobalizePath("user://"), "diagnostics");
			Directory.CreateDirectory(outputDirectory);
			var filePath = Path.Combine(outputDirectory, $"wodra-diagnostics-{DateTime.UtcNow:yyyyMMdd-HHmmss}.zip");
			var snapshot = BuildInputs();
			var result = await System.Threading.Tasks.Task.Run(() => WriteZip(filePath, snapshot));
			if (result.Success) OpenContainingFolder(filePath);
			return result;
		}
		catch (Exception ex) { return new(false, $"Could not export diagnostics: {ex.Message}"); }
	}

	private static List<(string Name, byte[] Data)> BuildInputs()
	{
		var files = new List<(string Name, byte[] Data)>();
		var omissions = new List<string>();
		var root = ProjectSettings.GlobalizePath("user://");
		AddText(files, "build.txt", $"version={Assembly.GetExecutingAssembly().GetName().Version}\ncommit={Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown"}\nos={OS.GetName()} {OS.GetVersion()}\nruntime={System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}\narchitecture={System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}\ncreatedUtc={DateTime.UtcNow:O}\n");
		var settings = AppSettings.SettingsPath;
		if (File.Exists(settings))
		{
			try
			{
				using var doc = JsonDocument.Parse(File.ReadAllText(settings));
				var safe = new Dictionary<string, object?>();
				var rootElement = doc.RootElement;
				bool Bool(string name) => rootElement.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False && v.GetBoolean();
				if (rootElement.TryGetProperty("Steering", out var steering))
					safe["Steering"] = new { Sensitivity = Number(steering, "Sensitivity"), DeadZoneDegrees = Number(steering, "DeadZoneDegrees") };
				if (rootElement.TryGetProperty("Display", out var display))
					safe["Display"] = new { RouteGradientInsetVisible = BoolProperty(display, "RouteGradientInsetVisible") };
				safe["DebugModeEnabled"] = Bool("DebugModeEnabled");
				safe["OnlineRidingEnabled"] = Bool("OnlineRidingEnabled");
				AddText(files, "settings-sanitized.json", JsonSerializer.Serialize(safe, new JsonSerializerOptions { WriteIndented = true }));
			}
			catch { omissions.Add("settings-sanitized.json: settings could not be read"); }
		}
		else omissions.Add("settings-sanitized.json: settings file missing");

		var candidates = new List<(string Path, string Name)>();
		if (GameLogger.LogPath is { } currentJsonl) candidates.Add((currentJsonl, "logs/current-session.jsonl"));
		candidates.Add((Path.Combine(root, "logs", "godot.log"), "logs/godot.log"));
		var logDir = Path.Combine(root, "logs");
		if (Directory.Exists(logDir))
			foreach (var path in Directory.GetFiles(logDir, "wodra-*.jsonl").OrderByDescending(File.GetLastWriteTimeUtc).Take(5))
				if (!candidates.Any(c => string.Equals(c.Path, path, StringComparison.OrdinalIgnoreCase))) candidates.Add((path, $"logs/recent-{Path.GetFileName(path)}"));
		var used = files.Sum(file => file.Data.Length);
		foreach (var candidate in candidates)
		{
			if (!File.Exists(candidate.Path)) { omissions.Add($"{candidate.Name}: missing"); continue; }
			try
			{
				var bytes = ReadBounded(candidate.Path, Math.Min(MaxFileBytes, MaxLogBytes - used));
				if (bytes.Length == 0) { omissions.Add($"{candidate.Name}: size limit reached"); continue; }
				var content = DiagnosticLogSanitizer.Redact(Encoding.UTF8.GetString(bytes), System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile));
				var data = Encoding.UTF8.GetBytes(content);
				files.Add((candidate.Name, data)); used += data.Length;
				if (new FileInfo(candidate.Path).Length > bytes.Length) omissions.Add($"{candidate.Name}: truncated to size limit");
			}
			catch { omissions.Add($"{candidate.Name}: unreadable"); }
		}
		AddText(files, "manifest.txt", "Wodra diagnostics export\nNo files were modified. Settings use a whitelist; logs are redacted and bounded.\n" +
			(omissions.Count == 0 ? "Omissions: none\n" : "Omissions:\n- " + string.Join("\n- ", omissions) + "\n"));
		return files;
	}

	private static byte[] ReadBounded(string path, int limit)
	{
		if (limit <= 0) return [];
		using var stream = new FileStream(path, FileMode.Open, System.IO.FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
		var buffer = new byte[Math.Min(limit, checked((int)Math.Min(stream.Length, int.MaxValue)))];
		var read = 0;
		while (read < buffer.Length) { var n = stream.Read(buffer, read, buffer.Length - read); if (n == 0) break; read += n; }
		return read == buffer.Length ? buffer : buffer[..read];
	}

	private static double? Number(JsonElement element, string property) => element.TryGetProperty(property, out var value) && value.TryGetDouble(out var number) && double.IsFinite(number) ? number : null;
	private static bool BoolProperty(JsonElement element, string property) => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;

	private static void AddText(List<(string Name, byte[] Data)> files, string name, string value) => files.Add((name, Encoding.UTF8.GetBytes(value)));
	private static DiagnosticExportResult WriteZip(string path, List<(string Name, byte[] Data)> files)
	{
		try
		{
			DiagnosticArchiveWriter.Write(path, files, MaxTotalBytes);
			if (new FileInfo(path).Length > MaxArchiveBytes) throw new InvalidDataException("Diagnostic ZIP exceeds its maximum size.");
			return new(true, "Diagnostic ZIP created.", path);
		}
		catch (Exception ex) { try { File.Delete(path); } catch { } return new(false, $"Could not write diagnostic ZIP: {ex.Message}"); }
	}

	private static void OpenContainingFolder(string path)
	{
		try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true }); }
		catch { Process.Start(new ProcessStartInfo(Path.GetDirectoryName(path)!) { UseShellExecute = true }); }
	}
}
