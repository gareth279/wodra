using OsmSharp;
using OsmSharp.Streams;

namespace GWift.RouteBuilder;

public sealed record StreetFurnitureItem(string OsmType, long OsmId, string Category, double Latitude,
    double Longitude, double? ElevationMeters, double? DirectionDegrees, double RouteTangentDegrees,
    double DistanceFromRouteMeters, double NearestRouteDistanceMeters, string? Name,
    IReadOnlyDictionary<string, string> Tags);
public sealed record StreetFurnitureCandidate(string OsmType, long OsmId, string Category, GeoPoint Point,
    double? DirectionDegrees, string? Name, IReadOnlyDictionary<string, string> Tags);
public sealed record StreetFurnitureExtractionResult(int TotalCandidates, int MalformedOrSkippedItems,
    int ItemsInsideRoadRibbon, IReadOnlyList<StreetFurnitureItem> Items, int ElevationSamples,
    double GenerationSeconds = 0, int RuntimeMultiMeshCount = 0, int SceneNodeCount = 0);

/// <summary>Extracts only explicit, mapped street-furniture objects from OSM point/linear features.</summary>
public static class StreetFurnitureExtractor
{
    public const double DefaultSearchRadiusMeters = 80;
    private const double CoordinateMarginMeters = 1800;
    private static readonly HashSet<string> UsefulTagKeys = new(StringComparer.OrdinalIgnoreCase)
    { "highway", "barrier", "amenity", "public_transport", "traffic_sign", "direction", "name", "name:en", "ref", "bus", "shelter", "bench", "crossing", "crossing:island", "lit", "tactile_paving", "operator" };

    public static StreetFurnitureExtractionResult Extract(string pbfPath, IReadOnlyList<RoutePointOutput> route,
        double radiusMeters = DefaultSearchRadiusMeters)
    {
        if (route.Count < 2) throw new ArgumentException("A route polyline needs at least two points.", nameof(route));
        if (!double.IsFinite(radiusMeters) || radiusMeters <= 0) throw new ArgumentOutOfRangeException(nameof(radiusMeters));
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var minLat = route.Min(item => item.Latitude); var maxLat = route.Max(item => item.Latitude);
        var minLon = route.Min(item => item.Longitude); var maxLon = route.Max(item => item.Longitude);
        var latMargin = CoordinateMarginMeters / 111_320d;
        var lonMargin = latMargin / Math.Max(.01, Math.Cos((minLat + maxLat) * Math.PI / 360));
        var coordinates = new Dictionary<long, GeoPoint>();
        var ways = new List<(long Id, long[] Nodes, Dictionary<string, string> Tags, string Category)>();
        var candidates = new List<StreetFurnitureCandidate>(); var skipped = 0;
        using var stream = File.OpenRead(pbfPath);
        foreach (var element in new PBFOsmStreamSource(stream))
        {
            if (element is Node node && node.Id is long nodeId && node.Latitude is double lat && node.Longitude is double lon)
            {
                if (lat < minLat - latMargin || lat > maxLat + latMargin || lon < minLon - lonMargin || lon > maxLon + lonMargin) continue;
                var point = new GeoPoint(lat, lon); coordinates[nodeId] = point;
                if (node.Tags is null) continue;
                var tags = OsmPbfGeometry.ToTags(node.Tags);
                if (Classify(tags) is not { } category) continue;
                candidates.Add(CreateCandidate("node", nodeId, category, point, tags));
            }
            else if (element is Way way && way.Id is long wayId && way.Nodes is { Length: >= 2 } nodes && way.Tags is not null)
            {
                var tags = OsmPbfGeometry.ToTags(way.Tags);
                if (Classify(tags) is not { } category) continue;
                ways.Add((wayId, nodes, tags, category));
            }
        }
        foreach (var (id, nodes, tags, category) in ways)
        {
            if (nodes.Any(node => !coordinates.ContainsKey(node))) { skipped++; continue; }
            var points = nodes.Select(node => coordinates[node]).ToArray();
            if (points.Length == 0) { skipped++; continue; }
            // Platforms and mapped line features are represented by their stable mean coordinate.
            candidates.Add(CreateCandidate("way", id, category,
                new GeoPoint(points.Average(point => point.Latitude), points.Average(point => point.Longitude)), tags));
        }
        var selected = SelectByRoute(candidates, route, radiusMeters);
        timer.Stop();
        return selected with { MalformedOrSkippedItems = skipped, GenerationSeconds = timer.Elapsed.TotalSeconds };
    }

    public static StreetFurnitureCandidate? TryCreateCandidate(string osmType, long osmId, GeoPoint point,
        IReadOnlyDictionary<string, string> tags)
    {
        if (osmId <= 0 || !double.IsFinite(point.Latitude) || !double.IsFinite(point.Longitude)
            || point.Latitude is < -90 or > 90 || point.Longitude is < -180 or > 180) return null;
        var category = Classify(tags);
        if (category is null) return null;
        return CreateCandidate(osmType, osmId, category, point,
            tags.ToDictionary(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase));
    }

    public static StreetFurnitureExtractionResult SelectByRoute(IEnumerable<StreetFurnitureCandidate> source,
        IReadOnlyList<RoutePointOutput> route, double radiusMeters = DefaultSearchRadiusMeters)
    {
        if (!double.IsFinite(radiusMeters) || radiusMeters <= 0) throw new ArgumentOutOfRangeException(nameof(radiusMeters));
        var candidates = source.ToArray(); var selected = new List<StreetFurnitureItem>(); var inside = 0;
        foreach (var candidate in candidates.OrderBy(item => item.OsmType, StringComparer.Ordinal).ThenBy(item => item.OsmId))
        {
            var nearest = Nearest(candidate.Point, route);
            if (nearest.Distance > radiusMeters) continue;
            if (IsInsideRoadRibbon(nearest.Distance)) inside++;
            selected.Add(new StreetFurnitureItem(candidate.OsmType, candidate.OsmId, candidate.Category,
                candidate.Point.Latitude, candidate.Point.Longitude, null, candidate.DirectionDegrees, nearest.TangentDegrees,
                nearest.Distance, nearest.Chainage, candidate.Name, candidate.Tags));
        }
        return new StreetFurnitureExtractionResult(candidates.Length, 0, inside,
            selected.OrderBy(item => item.NearestRouteDistanceMeters).ThenBy(item => item.Category, StringComparer.Ordinal)
                .ThenBy(item => item.OsmType, StringComparer.Ordinal).ThenBy(item => item.OsmId).ToArray(), 0);
    }

    public static string? Classify(IReadOnlyDictionary<string, string> tags)
    {
        var highway = tags.GetValueOrDefault("highway");
        if (highway == "street_lamp" || tags.GetValueOrDefault("man_made") == "street_lamp") return "street-lamp";
        if (highway == "traffic_signals") return "traffic-signal";
        if (highway == "stop") return "stop-sign";
        if (highway == "give_way") return "give-way-sign";
        if (highway == "crossing") return "crossing";
        if (highway == "bus_stop" || tags.GetValueOrDefault("public_transport") == "platform"
            && (tags.GetValueOrDefault("bus") == "yes" || highway == "bus_stop")) return "bus-stop";
        if (tags.GetValueOrDefault("barrier") == "bollard") return "bollard";
        if (tags.GetValueOrDefault("amenity") == "bench") return "bench";
        if (tags.ContainsKey("traffic_sign")) return "traffic-sign";
        return null;
    }

    public static bool IsInsideRoadRibbon(double distanceFromRouteMeters, double roadWidthMeters = 7) =>
        double.IsFinite(distanceFromRouteMeters) && distanceFromRouteMeters < roadWidthMeters / 2;

    public static int MeshPartCount(string category) => category == "bench" ? 4
        : category is "street-lamp" or "traffic-signal" or "stop-sign" or "give-way-sign"
            or "traffic-sign" or "crossing" or "bus-stop" ? 2 : 1;

    public static IReadOnlyDictionary<string, string> RetainUsefulTags(IReadOnlyDictionary<string, string> tags) =>
        tags.Where(item => UsefulTagKeys.Contains(item.Key)).OrderBy(item => item.Key, StringComparer.Ordinal)
            .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);

    private static StreetFurnitureCandidate CreateCandidate(string type, long id, string category, GeoPoint point, Dictionary<string, string> tags)
    {
        double? direction = null;
        if (tags.TryGetValue("direction", out var value) && double.TryParse(value,
                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var degrees)
            && double.IsFinite(degrees)) direction = (degrees % 360 + 360) % 360;
        return new StreetFurnitureCandidate(type, id, category, point, direction,
            tags.GetValueOrDefault("name") ?? tags.GetValueOrDefault("name:en"), RetainUsefulTags(tags));
    }

    private static (double Distance, double Chainage, double TangentDegrees) Nearest(GeoPoint point, IReadOnlyList<RoutePointOutput> route)
    {
        var best = (Distance: double.PositiveInfinity, Chainage: 0d, TangentDegrees: 0d);
        for (var i = 1; i < route.Count; i++)
        {
            var a = new GeoPoint(route[i-1].Latitude, route[i-1].Longitude);
            var b = new GeoPoint(route[i].Latitude, route[i].Longitude);
            var projected = RouteSolver.ProjectPointOntoSegment(point, a, b, out var fraction);
            var distance = GeoMath.HaversineMeters(point, projected);
            if (distance >= best.Distance) continue;
            var lat1 = a.Latitude * Math.PI / 180; var lat2 = b.Latitude * Math.PI / 180;
            var deltaLon = (b.Longitude - a.Longitude) * Math.PI / 180;
            var y = Math.Sin(deltaLon) * Math.Cos(lat2);
            var x = Math.Cos(lat1) * Math.Sin(lat2) - Math.Sin(lat1) * Math.Cos(lat2) * Math.Cos(deltaLon);
            var bearing = (Math.Atan2(y, x) * 180 / Math.PI + 360) % 360;
            best = (distance, route[i-1].DistanceMeters + (route[i].DistanceMeters-route[i-1].DistanceMeters)*fraction, bearing);
        }
        return best;
    }
}
