using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Godot;
using IndoorCycling.Core.Simulation;

/// <summary>Local challenge records are separate from cycling activities and telemetry.</summary>
public static class CollectibleResultStore
{
	private sealed class ResultDocument { public int SchemaVersion { get; set; } = 1; public List<CollectibleChallengeResult> Results { get; set; } = []; }
	public static string GetPath(Guid profileId) => Path.Combine(ProjectSettings.GlobalizePath("user://profiles"), profileId.ToString("N"), "game-results.json");
	public static IReadOnlyList<CollectibleChallengeResult> Load(Guid profileId)
	{
		try { var path = GetPath(profileId); return File.Exists(path) ? JsonSerializer.Deserialize<ResultDocument>(File.ReadAllText(path))?.Results ?? [] : []; }
		catch { return []; }
	}
	public static bool Save(Guid profileId, CollectibleChallengeResult result)
	{
		var results = Load(profileId).ToList();
		var comparable = results.Where(existing => existing.EligibleForPersonalBest && existing.RouteId == result.RouteId &&
			existing.RouteContentIdentity == result.RouteContentIdentity && existing.ChallengeVersion == result.ChallengeVersion &&
			existing.LapNumber == result.LapNumber).Select(existing => existing.Score).DefaultIfEmpty(-1).Max();
		var isBest = result.EligibleForPersonalBest && result.Score > comparable;
		results.Add(result);
		var path = GetPath(profileId); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		var json = JsonSerializer.Serialize(new ResultDocument { Results = results }, new JsonSerializerOptions { WriteIndented = true });
		var temporary = path + ".tmp"; File.WriteAllText(temporary, json); File.Move(temporary, path, true);
		return isBest;
	}
}
