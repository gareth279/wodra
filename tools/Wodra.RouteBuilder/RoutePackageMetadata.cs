using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Wodra.RouteBuilder;

/// <summary>Writes package identity after optional elevation resampling has finalized the route points.</summary>
internal static class RoutePackageMetadata
{
    public static async Task RefreshAsync(string outputDirectory, RouteAuthoringConfig config)
    {
        var routePath = Path.Combine(outputDirectory, $"{config.Id}.route-points.json");
        var routeNode = JsonNode.Parse(await File.ReadAllTextAsync(routePath))
            ?? throw new InvalidDataException("Generated route package is empty.");
        var points = routeNode["points"]?.Deserialize<RoutePointOutput[]>(new JsonSerializerOptions
            { PropertyNameCaseInsensitive = true }) ?? [];
        if (points.Length < 2) throw new InvalidDataException("Generated route package has fewer than two points.");
        var type = config.RouteType switch { "" when config.Loop => "loop", "" => "pointToPoint", var value => value };
        var fingerprint = Fingerprint(points);
        var reportPath = Path.Combine(outputDirectory, $"{config.Id}.report.json");
        var reportNode = JsonNode.Parse(await File.ReadAllTextAsync(reportPath))
            ?? throw new InvalidDataException("Generated route report is empty.");
        var totalAscent = reportNode["elevation"]?["totalAscentMeters"]?.GetValue<double>()
            ?? CalculateAscent(points);
        var difficulty = string.IsNullOrWhiteSpace(config.Difficulty)
            ? EstimateDifficulty(points[^1].DistanceMeters, totalAscent) : config.Difficulty;
        var elevationAttribution = reportNode["elevation"]?["attribution"]?.GetValue<string>();
        var fallback = reportNode["elevation"]?["fallbackCollection"] as JsonObject;
        var terrainAttribution = reportNode["terrain"]?["sourceAttribution"]?.GetValue<string>();
        var attribution = "© OpenStreetMap contributors, ODbL 1.0."
            + (string.IsNullOrWhiteSpace(elevationAttribution) ? "" : $"; elevation: {elevationAttribution}")
            + (fallback is null ? "" : $"; elevation fallback: {FormatCollectionAttribution(fallback)}")
            + (string.IsNullOrWhiteSpace(terrainAttribution) || terrainAttribution == elevationAttribution
                ? "" : $"; terrain: {terrainAttribution}");
        routeNode["routeType"] = type;
        routeNode["difficulty"] = difficulty;
        routeNode["routeDataVersion"] = 1;
        routeNode["geometryFingerprint"] = fingerprint;
        routeNode["totalAscentMeters"] = totalAscent;
        routeNode["sourceAttribution"] = attribution;
        await File.WriteAllTextAsync(routePath, routeNode.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        reportNode["routeType"] = type;
        reportNode["difficulty"] = difficulty;
        reportNode["routeDataVersion"] = 1;
        reportNode["geometryFingerprint"] = fingerprint;
        reportNode["totalAscentMeters"] = totalAscent;
        reportNode["sourceAttribution"] = attribution;
        await File.WriteAllTextAsync(reportPath, reportNode.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string FormatCollectionAttribution(JsonObject collection)
    {
        var title = collection["title"]?.GetValue<string>();
        var license = collection["license"]?.GetValue<string>();
        var providers = collection["providers"] is JsonArray array
            ? string.Join(", ", array.OfType<JsonObject>()
                .Select(provider => provider["name"]?.GetValue<string>())
                .Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.Ordinal))
            : "";
        var parts = new[] { title, providers, license }.Where(part => !string.IsNullOrWhiteSpace(part));
        return string.Join("; ", parts);
    }

    private static double CalculateAscent(IReadOnlyList<RoutePointOutput> points)
    {
        var ascent = 0d;
        for (var i = 1; i < points.Count; i++)
            ascent += Math.Max(0, (points[i].ElevationMeters ?? 0) - (points[i - 1].ElevationMeters ?? 0));
        return ascent;
    }

    private static string EstimateDifficulty(double distanceMeters, double ascentMeters)
    {
        var distanceKm = distanceMeters / 1000;
        if (distanceKm <= 0) return "Moderate";
        var ascentPerKm = ascentMeters / distanceKm;
        return ascentPerKm <= 12 ? "Easy" : ascentPerKm <= 30 ? "Moderate" : "Challenging";
    }

    private static string Fingerprint(IReadOnlyList<RoutePointOutput> points)
    {
        var canonical = string.Join("\n", points.Select(point => string.Join(",",
            point.DistanceMeters.ToString("F3", CultureInfo.InvariantCulture),
            point.Latitude.ToString("F7", CultureInfo.InvariantCulture),
            point.Longitude.ToString("F7", CultureInfo.InvariantCulture),
            point.ElevationMeters?.ToString("F3", CultureInfo.InvariantCulture) ?? "")));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }
}
