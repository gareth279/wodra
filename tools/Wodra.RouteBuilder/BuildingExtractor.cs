using System.Globalization;
using OsmSharp;
using OsmSharp.Streams;

namespace Wodra.RouteBuilder;

public sealed record BuildingFootprintPoint(double Longitude, double Latitude);
public sealed record RouteBuilding(string OsmType, long OsmId, string? Name, string BuildingType,
    double HeightMeters, string HeightSource, double? BaseElevationMeters,
    IReadOnlyList<BuildingFootprintPoint> Footprint, double DistanceFromRouteMeters,
    double NearestRouteDistanceMeters, IReadOnlyDictionary<string, string> SourceTags)
{
    public string VisualCategory { get; init; } = "generic";
    public int MaterialVariant { get; init; }
}
public sealed record BuildingExtractionResult(int NearbyCandidates, int SelectedCount, int OrdinaryWays,
    int MultipolygonRelationsIncluded, int MultipolygonRelationsSkipped, int NamedBuildings,
    int ExplicitHeightCount, int LevelsHeightCount, int FallbackHeightCount, int MalformedHeights,
    int MalformedOrExcludedRecords, int OriginalVertices, int SimplifiedVertices, int LargestFootprintVertices,
    double MinimumHeightMeters, double MaximumHeightMeters, double MedianHeightMeters,
    IReadOnlyList<RouteBuilding> Buildings, double ExtractionSeconds, int ElevationSkippedCount = 0);
public sealed record RawBuilding(string OsmType, long OsmId, Dictionary<string, string> Tags, long[] NodeIds,
    IReadOnlyList<GeoPoint> Polygon);

/// <summary>Extracts real nearby OSM building polygons without altering route geometry.</summary>
public static class BuildingExtractor
{
    public const double DefaultSearchRadiusMeters = 150;
    public const double DefaultSimplificationToleranceMeters = .75;
    public const double DefaultMetersPerLevel = 3;
    public const double DefaultOrdinaryHeightMeters = 8;
    public const double DefaultSmallBuildingHeightMeters = 3.5;
    public const double MaximumHeightMeters = 60;
    private const double CoordinateMarginMeters = 900;

    public static BuildingExtractionResult Extract(string pbfPath, IReadOnlyList<RoutePointOutput> route,
        double radiusMeters = DefaultSearchRadiusMeters, double toleranceMeters = DefaultSimplificationToleranceMeters,
        double metersPerLevel = DefaultMetersPerLevel, double ordinaryHeightMeters = DefaultOrdinaryHeightMeters,
        double smallBuildingHeightMeters = DefaultSmallBuildingHeightMeters)
    {
        if (route.Count < 2) throw new ArgumentException("A route polyline needs at least two points.", nameof(route));
        if (radiusMeters <= 0 || toleranceMeters < 0 || metersPerLevel <= 0) throw new ArgumentOutOfRangeException(nameof(radiusMeters));
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var minLat = route.Min(point => point.Latitude); var maxLat = route.Max(point => point.Latitude);
        var minLon = route.Min(point => point.Longitude); var maxLon = route.Max(point => point.Longitude);
        var marginLat = CoordinateMarginMeters / 111_320d;
        var marginLon = marginLat / Math.Max(.01, Math.Cos((minLat + maxLat) * Math.PI / 360));
        var coordinates = new Dictionary<long, GeoPoint>();
        var ways = new Dictionary<long, long[]>();
        var raw = new List<RawBuilding>();
        var skippedRelations = 0;
        var malformed = 0;
        using var stream = File.OpenRead(pbfPath);
        foreach (var element in new PBFOsmStreamSource(stream))
        {
            if (element is Node node && node.Id is long nodeId && node.Latitude is double lat && node.Longitude is double lon)
            {
                if (lat >= minLat - marginLat && lat <= maxLat + marginLat && lon >= minLon - marginLon && lon <= maxLon + marginLon)
                    coordinates[nodeId] = new GeoPoint(lat, lon);
            }
            else if (element is Way way && way.Id is long wayId && way.Nodes is { Length: >= 2 })
            {
                var tags = way.Tags is null ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) : OsmPbfGeometry.ToTags(way.Tags);
                if (way.Nodes.Any(coordinates.ContainsKey))
                    ways[wayId] = way.Nodes;
                if (IsBuilding(tags))
                {
                    if (way.Nodes.Length < 4 || way.Nodes[0] != way.Nodes[^1]
                        || way.Nodes.Any(id => !coordinates.ContainsKey(id)))
                    {
                        var available = way.Nodes.Where(coordinates.ContainsKey).Select(id => coordinates[id]).ToArray();
                        if (available.Length > 0 && available.Min(point => LandmarkExtractor.PointToPolylineDistanceMeters(point, route)) <= radiusMeters)
                            malformed++;
                        continue;
                    }
                    var candidate = CreateClosedWay(wayId, way.Nodes, tags, coordinates);
                    if (candidate is null) { malformed++; continue; }
                    ways[wayId] = candidate.NodeIds;
                    if (!IsMultipolygonMember(tags)) raw.Add(candidate);
                }
            }
            else if (element is Relation relation && relation.Id is long relationId && relation.Tags is not null)
            {
                var tags = OsmPbfGeometry.ToTags(relation.Tags);
                if (!IsBuilding(tags) || !string.Equals(tags.GetValueOrDefault("type"), "multipolygon", StringComparison.OrdinalIgnoreCase)) continue;
                var members = OsmPbfGeometry.RelationWayMembers(relation).ToArray();
                if (members.Length == 0) continue;
                var outerIds = members.Where(member => member.Role is "" or "outer").Select(member => member.Id).ToArray();
                var stitched = OsmPbfGeometry.StitchOuterRing(outerIds, ways);
                if (stitched is null || stitched.Count < 4 || stitched[0] != stitched[^1]
                    || stitched.Any(id => !coordinates.ContainsKey(id)))
                {
                    var available = outerIds.Where(ways.ContainsKey).SelectMany(id => ways[id])
                        .Where(coordinates.ContainsKey).Distinct().Select(id => coordinates[id]).ToArray();
                    if (available.Length > 0 && available.Min(point => LandmarkExtractor.PointToPolylineDistanceMeters(point, route)) <= radiusMeters)
                        skippedRelations++;
                    continue;
                }
                var polygon = stitched.Select(id => coordinates[id]).ToArray();
                if (!ValidPolygon(polygon)) { malformed++; continue; }
                if (members.Any(member => member.Role == "inner"))
                {
                    if (PolygonToRoute(polygon, route).Distance <= radiusMeters) skippedRelations++;
                    continue;
                }
                raw.Add(new RawBuilding("relation", relationId, tags, stitched.ToArray(), polygon));
            }
        }

        var selection = SelectByRoute(raw, route, radiusMeters, toleranceMeters, metersPerLevel,
            ordinaryHeightMeters, smallBuildingHeightMeters);
        var selected = selection.Buildings;
        var nearby = selection.NearbyCandidates;
        var malformedHeights = selection.MalformedHeights;
        var excluded = malformed + selection.MalformedOrExcludedRecords;
        timer.Stop();
        var heights = selected.Select(item => item.HeightMeters).Order().ToArray();
        var originalVertices = raw.Where(item => PolygonToRoute(item.Polygon, route).Distance <= radiusMeters).Sum(item => item.Polygon.Count);
        return new BuildingExtractionResult(nearby + skippedRelations, selected.Count, selected.Count(item => item.OsmType == "way"),
            selected.Count(item => item.OsmType == "relation"), skippedRelations,
            selected.Count(item => !string.IsNullOrWhiteSpace(item.Name)),
            selection.ExplicitHeightCount, selection.LevelsHeightCount, selection.FallbackHeightCount,
            malformedHeights, excluded, originalVertices, selected.Sum(item => item.Footprint.Count),
            selected.Count == 0 ? 0 : selected.Max(item => item.Footprint.Count),
            heights.Length == 0 ? 0 : heights[0], heights.Length == 0 ? 0 : heights[^1],
            heights.Length == 0 ? 0 : heights[(heights.Length - 1) / 2], selected, timer.Elapsed.TotalSeconds);
    }

    public static RawBuilding? CreateClosedWay(long id, IReadOnlyList<long> nodeIds,
        Dictionary<string, string> tags, IReadOnlyDictionary<long, GeoPoint> coordinates)
    {
        if (!IsBuilding(tags) || nodeIds.Count < 4 || nodeIds[0] != nodeIds[^1]
            || nodeIds.Any(node => !coordinates.ContainsKey(node))) return null;
        var polygon = nodeIds.Select(node => coordinates[node]).ToArray();
        return ValidPolygon(polygon) ? new RawBuilding("way", id, tags, nodeIds.ToArray(), polygon) : null;
    }

    public static (double DistanceMeters, double RouteDistanceMeters) PolygonToRouteDistance(
        IReadOnlyList<GeoPoint> polygon, IReadOnlyList<RoutePointOutput> route)
    {
        var result = PolygonToRoute(polygon, route);
        return (result.Distance, result.RouteDistance);
    }

    public static (int NearbyCandidates, int ExplicitHeightCount, int LevelsHeightCount, int FallbackHeightCount,
        int MalformedHeights, int MalformedOrExcludedRecords, IReadOnlyList<RouteBuilding> Buildings) SelectByRoute(
        IEnumerable<RawBuilding> source, IReadOnlyList<RoutePointOutput> route, double radiusMeters = DefaultSearchRadiusMeters,
        double toleranceMeters = DefaultSimplificationToleranceMeters, double metersPerLevel = DefaultMetersPerLevel,
        double ordinaryHeightMeters = DefaultOrdinaryHeightMeters, double smallBuildingHeightMeters = DefaultSmallBuildingHeightMeters)
    {
        var selected = new List<(RouteBuilding Building, HeightKind Kind)>();
        var nearby = 0; var malformedHeights = 0; var excluded = 0;
        foreach (var item in source)
        {
            if (!ValidPolygon(item.Polygon)) { excluded++; continue; }
            var proximity = PolygonToRoute(item.Polygon, route);
            if (proximity.Distance > radiusMeters) continue;
            nearby++;
            var simplified = Simplify(item.Polygon, toleranceMeters);
            if (!ValidPolygon(simplified)) { excluded++; continue; }
            var (height, kind, invalid) = ResolveHeight(item.Tags, metersPerLevel, ordinaryHeightMeters, smallBuildingHeightMeters);
            if (invalid) malformedHeights++;
            var retainedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "building", "height", "building:levels", "building:material", "building:colour", "name", "name:en",
                "historic", "roof:shape", "roof:levels", "roof:height", "roof:material", "roof:colour",
                "amenity", "office", "shop", "tourism", "isced:level", "education", "operator:type"
            };
            var retainedTags = item.Tags.Where(pair => retainedKeys.Contains(pair.Key))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
            var category = BuildingVisualStyle.Classify(item.Tags);
            selected.Add((new RouteBuilding(item.OsmType, item.OsmId,
                item.Tags.GetValueOrDefault("name") ?? item.Tags.GetValueOrDefault("name:en"),
                item.Tags.GetValueOrDefault("building") ?? "yes", height, kind.ToString(), null,
                simplified.Select(point => new BuildingFootprintPoint(point.Longitude, point.Latitude)).ToArray(),
                proximity.Distance, proximity.RouteDistance, retainedTags)
                { VisualCategory = category, MaterialVariant = BuildingVisualStyle.MaterialVariant(item.OsmId, category) }, kind));
        }
        var ordered = selected.OrderBy(item => item.Building.NearestRouteDistanceMeters).ThenBy(item => item.Building.OsmType)
            .ThenBy(item => item.Building.OsmId).ToArray();
        return (nearby, ordered.Count(item => item.Kind == HeightKind.Explicit), ordered.Count(item => item.Kind == HeightKind.Levels),
            ordered.Count(item => item.Kind == HeightKind.Fallback), malformedHeights, excluded,
            ordered.Select(item => item.Building).ToArray());
    }

    public static bool IsBuilding(IReadOnlyDictionary<string, string> tags) => tags.TryGetValue("building", out var value)
        && !string.IsNullOrWhiteSpace(value) && value is not ("no" or "entrance" or "roof" or "construction");

    public static (double Height, string Source, bool Implausible) ParseHeight(IReadOnlyDictionary<string, string> tags,
        double metersPerLevel = DefaultMetersPerLevel, double ordinaryHeightMeters = DefaultOrdinaryHeightMeters,
        double smallBuildingHeightMeters = DefaultSmallBuildingHeightMeters)
    {
        var resolved = ResolveHeight(new Dictionary<string, string>(tags, StringComparer.OrdinalIgnoreCase), metersPerLevel,
            ordinaryHeightMeters, smallBuildingHeightMeters);
        return (resolved.Height, resolved.Kind.ToString(), resolved.Invalid);
    }

    private enum HeightKind { Explicit, Levels, Fallback }
    private static (double Height, HeightKind Kind, bool Invalid) ResolveHeight(Dictionary<string, string> tags,
        double perLevel, double ordinary, double small)
    {
        var invalid = false;
        if (tags.TryGetValue("height", out var rawHeight))
        {
            var parsed = ParseNumber(rawHeight);
            if (parsed is > 0 and <= MaximumHeightMeters) return (parsed.Value, HeightKind.Explicit, false);
            invalid = true;
        }
        if (tags.TryGetValue("building:levels", out var rawLevels))
        {
            var levels = ParseNumber(rawLevels);
            if (levels is > 0 and <= 20)
            {
                var height = Math.Min(MaximumHeightMeters, levels.Value * perLevel);
                return (height, HeightKind.Levels, invalid);
            }
            invalid = true;
        }
        var type = tags.GetValueOrDefault("building") ?? "";
        var compact = type.Contains("garage", StringComparison.OrdinalIgnoreCase) || type.Contains("shed", StringComparison.OrdinalIgnoreCase)
            || type.Contains("service", StringComparison.OrdinalIgnoreCase) || type is "garages" or "carport";
        return (Math.Clamp(compact ? small : ordinary, 1, MaximumHeightMeters), HeightKind.Fallback, invalid);
    }

    private static double? ParseNumber(string value)
    {
        var text = value.Trim();
        if (text.EndsWith("m", StringComparison.OrdinalIgnoreCase)) text = text[..^1].Trim();
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) && double.IsFinite(parsed) ? parsed : null;
    }

    private static bool IsMultipolygonMember(Dictionary<string, string> tags) => tags.GetValueOrDefault("type") == "multipolygon";
    private static bool ValidPolygon(IReadOnlyList<GeoPoint> points)
    {
        if (points.Count < 4 || GeoMath.HaversineMeters(points[0], points[^1]) > .05) return false;
        if (points.Take(points.Count - 1).Distinct().Count() < 3) return false;
        var originLatitude = points.Average(point => point.Latitude);
        var originLongitude = points.Average(point => point.Longitude);
        const double radians = Math.PI / 180;
        var scaleX = 6371000 * Math.Cos(originLatitude * radians) * radians;
        var scaleY = 6371000 * radians;
        var twiceArea = 0d;
        for (var i = 0; i < points.Count - 1; i++)
        {
            var a = ((points[i].Longitude - originLongitude) * scaleX,
                -(points[i].Latitude - originLatitude) * scaleY);
            var b = ((points[i + 1].Longitude - originLongitude) * scaleX,
                -(points[i + 1].Latitude - originLatitude) * scaleY);
            twiceArea += a.Item1 * b.Item2 - b.Item1 * a.Item2;
        }
        // Tiny OSM slivers quantize to zero area in the runtime's single-precision mesh frame.
        return double.IsFinite(twiceArea) && Math.Abs(twiceArea) >= 0.2;
    }

    private static (double Distance, double RouteDistance) PolygonToRoute(IReadOnlyList<GeoPoint> polygon, IReadOnlyList<RoutePointOutput> route)
    {
        var bestDistance = double.PositiveInfinity; var bestAlong = 0d;
        for (var i = 0; i < route.Count; i++)
        {
            var point = new GeoPoint(route[i].Latitude, route[i].Longitude);
            if (PointInPolygon(point, polygon)) return (0, route[i].DistanceMeters);
        }
        for (var edge = 1; edge < polygon.Count; edge++)
        {
            var a = polygon[edge - 1]; var b = polygon[edge];
            var length = GeoMath.HaversineMeters(a, b); var samples = Math.Max(1, (int)Math.Ceiling(length / 4));
            for (var step = 0; step <= samples; step++)
            {
                var t = step / (double)samples;
                var point = new GeoPoint(a.Latitude + (b.Latitude - a.Latitude) * t, a.Longitude + (b.Longitude - a.Longitude) * t);
                var projected = LandmarkExtractor.PointToRoute(point, route);
                if (projected.DistanceMeters < bestDistance) { bestDistance = projected.DistanceMeters; bestAlong = projected.RouteDistanceMeters; }
            }
        }
        return (bestDistance, bestAlong);
    }

    private static bool PointInPolygon(GeoPoint point, IReadOnlyList<GeoPoint> polygon)
    {
        var inside = false;
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
        {
            var a = polygon[i]; var b = polygon[j];
            if ((a.Latitude > point.Latitude) != (b.Latitude > point.Latitude)
                && point.Longitude < (b.Longitude - a.Longitude) * (point.Latitude - a.Latitude) / (b.Latitude - a.Latitude) + a.Longitude)
                inside = !inside;
        }
        return inside;
    }

    public static IReadOnlyList<GeoPoint> Simplify(IReadOnlyList<GeoPoint> closed, double toleranceMeters)
    {
        if (closed.Count < 5 || toleranceMeters <= 0) return closed.ToArray();
        var origin = closed[0]; const double rad = Math.PI / 180;
        var scaleX = 6371000 * Math.Cos(origin.Latitude * rad) * rad; var scaleY = 6371000 * rad;
        var open = closed.Take(closed.Count - 1).ToList();
        bool changed = true;
        while (changed && open.Count > 4)
        {
            changed = false;
            for (var i = 0; i < open.Count && open.Count > 4; i++)
            {
                var prev = open[(i - 1 + open.Count) % open.Count]; var cur = open[i]; var next = open[(i + 1) % open.Count];
                var px = (cur.Longitude - origin.Longitude) * scaleX; var py = (cur.Latitude - origin.Latitude) * scaleY;
                var ax = (prev.Longitude - origin.Longitude) * scaleX; var ay = (prev.Latitude - origin.Latitude) * scaleY;
                var bx = (next.Longitude - origin.Longitude) * scaleX; var by = (next.Latitude - origin.Latitude) * scaleY;
                var dx = bx - ax; var dy = by - ay; var den = dx * dx + dy * dy;
                var t = den > 0 ? Math.Clamp(((px - ax) * dx + (py - ay) * dy) / den, 0, 1) : 0;
                var d = Math.Sqrt(Math.Pow(px - ax - dx * t, 2) + Math.Pow(py - ay - dy * t, 2));
                if (d <= toleranceMeters) { open.RemoveAt(i); changed = true; i--; }
            }
        }
        open.Add(open[0]);
        return open;
    }

}
