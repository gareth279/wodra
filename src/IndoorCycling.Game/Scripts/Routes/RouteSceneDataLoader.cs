using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;

/// <summary>A video-observed visual override for an existing mapped building footprint.</summary>
public sealed record RouteSceneBuildingStyle(long OsmId, string? FacadeColour, string? RoofColour,
    string? RoofShape, string? BuildingType, string? SourceVideo = null, double? VideoSeconds = null,
    double? Confidence = null, string? Provenance = null, double? VisualHeightMeters = null);

/// <summary>One individually authored, route-relative video observation used by the world renderer.</summary>
public sealed record RouteSceneFeature(string Id, string ObjectType, double RouteChainageMeters,
    double LateralPositionMeters, double ForwardOffsetMeters, double OrientationDegrees,
    double LengthMeters, double WidthMeters, double HeightMeters, string? Variant, string? Colour,
    string SourceVideo, double VideoSeconds, double Confidence, string Provenance);

public sealed record RouteSceneData(string Id, IReadOnlyList<RouteSceneFeature> Features,
    IReadOnlyList<RouteSceneBuildingStyle> BuildingStyles);

public sealed record RouteSceneObservation(string? Id, string? SourceVideo, double? VideoSeconds,
    double? Confidence, string? Provenance, List<RouteSceneFeature>? Features);

public static class RouteSceneDataLoader
{
    private sealed record SceneFile(string? Id, string? RouteId, List<RouteSceneFeature>? Features,
        List<RouteSceneObservation>? Observations,
        List<RouteSceneBuildingStyle>? BuildingStyles);
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };
    private static readonly HashSet<string> SupportedTypes = new(StringComparer.Ordinal)
    {
        "timber-fence", "picket-fence", "broadleaf-tree", "conifer-tree", "shrub", "hedge",
        "grass-verge", "planting-bed", "low-boundary-wall", "retaining-wall", "utility-pole",
        "give-way-sign", "yield-triangle", "give-way-line", "yellow-kerb-stripe", "white-lane-dash"
    };

    public static RouteSceneData Load(Assembly assembly, string resourceName, string routeId, double routeLengthMeters)
    {
        using var stream = RoutePackageFiles.Open(assembly, resourceName, routeId)
            ?? throw new InvalidDataException($"Route scene resource '{resourceName}' was not found.");
        var file = JsonSerializer.Deserialize<SceneFile>(stream, Options)
            ?? throw new InvalidDataException($"Route scene resource '{resourceName}' is empty.");
        if (!string.Equals(file.Id ?? file.RouteId, routeId, StringComparison.Ordinal))
            throw new InvalidDataException($"Route scene resource '{resourceName}' does not match route '{routeId}'.");
        var observations = file.Observations ?? [];
        var features = (file.Features ?? []).Concat(observations.SelectMany(observation =>
            (observation.Features ?? []).Select(feature => feature with
            {
                SourceVideo = feature.SourceVideo ?? observation.SourceVideo ?? "",
                VideoSeconds = double.IsFinite(feature.VideoSeconds) ? feature.VideoSeconds : observation.VideoSeconds ?? double.NaN,
                Confidence = double.IsFinite(feature.Confidence) ? feature.Confidence : observation.Confidence ?? double.NaN,
                Provenance = feature.Provenance ?? observation.Provenance ?? ""
            }))).ToArray();
        foreach (var feature in features)
        {
            if (string.IsNullOrWhiteSpace(feature.Id) || !SupportedTypes.Contains(feature.ObjectType)
                || !double.IsFinite(feature.RouteChainageMeters) || feature.RouteChainageMeters < 0
                || feature.RouteChainageMeters > routeLengthMeters || !double.IsFinite(feature.LateralPositionMeters)
                || !double.IsFinite(feature.ForwardOffsetMeters) || !double.IsFinite(feature.OrientationDegrees)
                || !double.IsFinite(feature.LengthMeters) || feature.LengthMeters < 0
                || !double.IsFinite(feature.WidthMeters) || feature.WidthMeters < 0
                || !double.IsFinite(feature.HeightMeters) || feature.HeightMeters < 0
                || !double.IsFinite(feature.VideoSeconds) || !double.IsFinite(feature.Confidence)
                || feature.Confidence is < 0 or > 1 || string.IsNullOrWhiteSpace(feature.SourceVideo)
                || string.IsNullOrWhiteSpace(feature.Provenance))
                throw new InvalidDataException($"Route scene feature '{feature.Id}' contains invalid evidence or placement data.");
        }
        var styles = file.BuildingStyles ?? [];
        if (styles.Any(style => style.OsmId <= 0))
            throw new InvalidDataException($"Route scene resource '{resourceName}' contains an invalid mapped-building style override.");
        return new RouteSceneData(routeId, features, styles);
    }
}
