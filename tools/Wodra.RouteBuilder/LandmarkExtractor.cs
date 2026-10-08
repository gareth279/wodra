using System.Collections;
using System.Reflection;
using OsmSharp;
using OsmSharp.Streams;

namespace Wodra.RouteBuilder;

public sealed record RouteLandmark(string Name, string Category, double Latitude, double Longitude,
    double DistanceFromRouteMeters, double NearestRouteDistanceMeters, string OsmType, long OsmId,
    IReadOnlyDictionary<string, string> Tags);
public sealed record LandmarkExtractionResult(int NearbyCandidatesConsidered, int DeduplicatedCandidateCount,
    IReadOnlyList<RouteLandmark> Landmarks);
public sealed record RawLandmark(string Name, string Category, double Latitude, double Longitude,
    IReadOnlyList<GeoPoint> Geometry, string OsmType, long OsmId, IReadOnlyDictionary<string, string> Tags,
    double Score, bool GeometryIsContinuous = true);

/// <summary>Extracts a small ranked set of named OSM orientation landmarks near an authored route.</summary>
public static class LandmarkExtractor
{
    public const double DefaultSearchRadiusMeters = 150;
    public const int DefaultMaximumLandmarks = 20;
    private const double CoordinateMarginMeters = 900;
    private static readonly HashSet<string> UsefulAmenities = new(StringComparer.OrdinalIgnoreCase)
    { "university", "college", "school", "library", "museum", "theatre", "townhall", "courthouse", "place_of_worship", "hospital", "community_centre", "arts_centre" };
    private static readonly HashSet<string> SignificantBuildings = new(StringComparer.OrdinalIgnoreCase)
    { "university", "college", "school", "government", "civic", "church", "cathedral", "museum", "train_station" };
    private static readonly HashSet<string> MinimalTagKeys = new(StringComparer.OrdinalIgnoreCase)
    { "name", "name:en", "tourism", "historic", "amenity", "leisure", "place", "building", "railway", "landuse", "wikidata", "heritage", "operator" };

    public static (double DistanceMeters, double RouteDistanceMeters, GeoPoint ClosestPoint) PointToRoute(
        GeoPoint point, IReadOnlyList<RoutePointOutput> route)
    {
        if (route.Count < 2) throw new ArgumentException("A route polyline needs at least two points.", nameof(route));
        var best = (DistanceMeters: double.PositiveInfinity, RouteDistanceMeters: 0d,
            ClosestPoint: new GeoPoint(route[0].Latitude, route[0].Longitude));
        for (var i = 1; i < route.Count; i++)
        {
            var a = new GeoPoint(route[i - 1].Latitude, route[i - 1].Longitude);
            var b = new GeoPoint(route[i].Latitude, route[i].Longitude);
            var projected = RouteSolver.ProjectPointOntoSegment(point, a, b, out var fraction);
            var distance = GeoMath.HaversineMeters(point, projected);
            if (distance < best.DistanceMeters)
                best = (distance, route[i - 1].DistanceMeters + (route[i].DistanceMeters - route[i - 1].DistanceMeters) * fraction, projected);
        }
        return best;
    }

    public static double PointToPolylineDistanceMeters(GeoPoint point, IReadOnlyList<RoutePointOutput> route) =>
        PointToRoute(point, route).DistanceMeters;

    public static double NearestRouteDistanceMeters(GeoPoint point, IReadOnlyList<RoutePointOutput> route) =>
        PointToRoute(point, route).RouteDistanceMeters;

    public static LandmarkExtractionResult RankAndFilter(IEnumerable<RawLandmark> source,
        IReadOnlyList<RoutePointOutput> route, double radiusMeters = DefaultSearchRadiusMeters,
        int maximumLandmarks = DefaultMaximumLandmarks)
    {
        var candidates = new List<(RawLandmark Raw, double Distance, double RouteDistance)>();
        foreach (var item in source)
        {
            var closest = FeatureToRoute(item.Geometry.Count == 0
                ? [new GeoPoint(item.Latitude, item.Longitude)] : item.Geometry, route, item.GeometryIsContinuous);
            if (closest.DistanceMeters <= radiusMeters)
                candidates.Add((item, closest.DistanceMeters, closest.RouteDistanceMeters));
        }

        var ranked = candidates.OrderByDescending(item => item.Raw.Score - item.Distance / 40)
            .ThenBy(item => item.Distance).ThenBy(item => item.Raw.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Raw.OsmType, StringComparer.Ordinal).ThenBy(item => item.Raw.OsmId).ToList();
        var unique = new List<(RawLandmark Raw, double Distance, double RouteDistance)>();
        foreach (var candidate in ranked)
        {
            var normalizedName = Normalize(candidate.Raw.Name);
            var wikidata = candidate.Raw.Tags.GetValueOrDefault("wikidata");
            if (unique.Any(existing =>
            {
                var sameSite = wikidata is not null && wikidata.Equals(existing.Raw.Tags.GetValueOrDefault("wikidata"), StringComparison.OrdinalIgnoreCase);
                var sameNamedPlace = normalizedName == Normalize(existing.Raw.Name)
                    && GeoMath.HaversineMeters(new(candidate.Raw.Latitude, candidate.Raw.Longitude),
                        new(existing.Raw.Latitude, existing.Raw.Longitude)) <= 50;
                return sameSite || sameNamedPlace;
            })) continue;
            unique.Add(candidate);
        }
        var selected = unique.Take(Math.Max(0, maximumLandmarks)).Select(item => new RouteLandmark(
            item.Raw.Name, item.Raw.Category, item.Raw.Latitude, item.Raw.Longitude, item.Distance,
            item.RouteDistance, item.Raw.OsmType, item.Raw.OsmId,
            item.Raw.Tags.Where(pair => MinimalTagKeys.Contains(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value,
                StringComparer.OrdinalIgnoreCase))).ToArray();
        return new LandmarkExtractionResult(candidates.Count, unique.Count, selected);
    }

    private static (double DistanceMeters, double RouteDistanceMeters, GeoPoint ClosestPoint) FeatureToRoute(
        IReadOnlyList<GeoPoint> geometry, IReadOnlyList<RoutePointOutput> route, bool continuous)
    {
        var best = (DistanceMeters: double.PositiveInfinity, RouteDistanceMeters: 0d, ClosestPoint: geometry[0]);
        for (var i = 0; i < geometry.Count; i++)
        {
            var a = geometry[i];
            var pointResult = PointToRoute(a, route);
            if (pointResult.DistanceMeters < best.DistanceMeters) best = pointResult;
            if (i == 0 || !continuous) continue;
            var before = geometry[i - 1];
            var length = GeoMath.HaversineMeters(before, a);
            var samples = Math.Max(1, (int)Math.Ceiling(length / 10));
            for (var step = 1; step < samples; step++)
            {
                var t = step / (double)samples;
                var sample = new GeoPoint(before.Latitude + (a.Latitude - before.Latitude) * t,
                    before.Longitude + (a.Longitude - before.Longitude) * t);
                var result = PointToRoute(sample, route);
                if (result.DistanceMeters < best.DistanceMeters) best = result;
            }
        }
        return best;
    }

    public static string? Classify(IReadOnlyDictionary<string, string> tags)
    {
        var name = tags.GetValueOrDefault("name") ?? tags.GetValueOrDefault("name:en");
        if (string.IsNullOrWhiteSpace(name) || IsTrivial(name)) return null;
        if (tags.TryGetValue("tourism", out var tourism) && tourism is "attraction" or "museum" or "artwork" or "viewpoint") return "attraction";
        if (tags.TryGetValue("historic", out var historic) && historic != "no")
            return historic is "monument" or "memorial" or "wayside_shrine" ? "monument" : "historic";
        if (tags.TryGetValue("railway", out var railway) && railway is "station" or "halt" or "tram_stop") return "railway/station";
        if (tags.TryGetValue("leisure", out var leisure) && leisure is "park" or "garden") return leisure;
        if (tags.TryGetValue("amenity", out var amenity) && UsefulAmenities.Contains(amenity))
            return amenity is "university" or "college" or "school" ? "education/civic" : amenity;
        if (tags.TryGetValue("building", out var stationBuilding) && stationBuilding == "train_station") return "railway/station";
        if (tags.TryGetValue("building", out var building) && SignificantBuildings.Contains(building)) return "building/civic";
        if (tags.TryGetValue("place", out var place) && place is "suburb" or "neighbourhood" or "square" or "locality") return "place";
        return null;
    }

    private static bool IsTrivial(string name) => name.Trim().Length < 3
        || name.Equals("unnamed", StringComparison.OrdinalIgnoreCase)
        || name.Equals("unknown", StringComparison.OrdinalIgnoreCase);
    private static string Normalize(string name) => string.Join(' ', name.Trim().Split((char[]?)null,
        StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();

    private static double BaseScore(string category, IReadOnlyDictionary<string, string> tags, int geometryCount)
    {
        var score = category switch
        {
            "monument" => 12, "attraction" => 11, "historic" => 10, "education/civic" => 10,
            "railway/station" => 9, "park" or "garden" => 9, "museum" or "library" or "theatre" => 9,
            "building/civic" => 8, "place" => 6, _ => 7
        };
        if (tags.ContainsKey("wikidata") || tags.ContainsKey("heritage")) score += 2;
        if (geometryCount >= 10) score += 1;
        return score;
    }

    public static LandmarkExtractionResult Extract(string pbfPath, IReadOnlyList<RoutePointOutput> route,
        double radiusMeters = DefaultSearchRadiusMeters, int maximumLandmarks = DefaultMaximumLandmarks)
    {
        if (route.Count < 2) throw new ArgumentException("A route polyline needs at least two points.", nameof(route));
        var minLat = route.Min(point => point.Latitude); var maxLat = route.Max(point => point.Latitude);
        var minLon = route.Min(point => point.Longitude); var maxLon = route.Max(point => point.Longitude);
        var marginLat = CoordinateMarginMeters / 111_320d;
        var marginLon = marginLat / Math.Max(.01, Math.Cos((minLat + maxLat) * Math.PI / 360));
        var coordinates = new Dictionary<long, GeoPoint>();
        var ways = new Dictionary<long, (long[] Nodes, Dictionary<string, string> Tags)>();
        var raw = new List<RawLandmark>();
        using var pbfStream = File.OpenRead(pbfPath);
        foreach (var element in new PBFOsmStreamSource(pbfStream))
        {
            if (element is Node node && node.Id is long nodeId && node.Latitude is double lat && node.Longitude is double lon)
            {
                if (lat >= minLat - marginLat && lat <= maxLat + marginLat && lon >= minLon - marginLon && lon <= maxLon + marginLon)
                    coordinates[nodeId] = new GeoPoint(lat, lon);
                if (node.Tags is not null && node.Id is long id)
                {
                    var tags = ToTags(node.Tags); var category = Classify(tags);
                    if (category is not null && lat >= minLat - marginLat && lat <= maxLat + marginLat
                        && lon >= minLon - marginLon && lon <= maxLon + marginLon)
                        raw.Add(CreateRaw(tags, category, [new GeoPoint(lat, lon)], "node", id));
                }
            }
            else if (element is Way way && way.Id is long wayId && way.Nodes is not null)
            {
                var tags = way.Tags is null ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) : ToTags(way.Tags);
                var hasNearbyNode = way.Nodes.Any(coordinates.ContainsKey);
                if (hasNearbyNode) ways[wayId] = (way.Nodes, tags);
                var category = Classify(tags);
                if (category is not null && hasNearbyNode)
                {
                    var geometry = way.Nodes.Where(coordinates.ContainsKey).Select(id => coordinates[id]).ToArray();
                    if (geometry.Length > 0) raw.Add(CreateRaw(tags, category, geometry, "way", wayId));
                }
            }
            else if (element is Relation relation && relation.Id is long relationId && relation.Tags is not null)
            {
                var tags = ToTags(relation.Tags); var category = Classify(tags);
                if (category is null) continue;
                var memberWayIds = RelationWayIds(relation);
                var geometry = memberWayIds.Where(ways.ContainsKey).SelectMany(id => ways[id].Nodes)
                    .Where(coordinates.ContainsKey).Distinct().Select(id => coordinates[id]).ToArray();
                if (geometry.Length > 0) raw.Add(CreateRaw(tags, category, geometry, "relation", relationId));
            }
        }
        return RankAndFilter(raw, route, radiusMeters, maximumLandmarks);
    }

    private static RawLandmark CreateRaw(Dictionary<string, string> tags, string category,
        IReadOnlyList<GeoPoint> geometry, string type, long id)
    {
        var latitude = geometry.Average(point => point.Latitude); var longitude = geometry.Average(point => point.Longitude);
        return new RawLandmark(tags.GetValueOrDefault("name") ?? tags.GetValueOrDefault("name:en") ?? "",
            category, latitude, longitude, geometry, type, id, tags, BaseScore(category, tags, geometry.Count),
            GeometryIsContinuous: type != "relation");
    }

    private static Dictionary<string, string> ToTags(object tags)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (tags is not IEnumerable entries) return result;
        foreach (var entry in entries)
        {
            if (entry is null) continue;
            var key = entry.GetType().GetProperty("Key")?.GetValue(entry)?.ToString();
            var value = entry.GetType().GetProperty("Value")?.GetValue(entry)?.ToString();
            if (!string.IsNullOrWhiteSpace(key) && value is not null) result[key] = value;
        }
        return result;
    }

    private static IEnumerable<long> RelationWayIds(Relation relation)
    {
        var members = relation.GetType().GetProperty("Members")?.GetValue(relation) as IEnumerable;
        if (members is null) yield break;
        foreach (var member in members)
        {
            if (member is null) continue;
            var type = member.GetType().GetProperty("Type")?.GetValue(member)?.ToString();
            if (!string.Equals(type, "Way", StringComparison.OrdinalIgnoreCase)) continue;
            var idValue = member.GetType().GetProperty("Id")?.GetValue(member)
                ?? member.GetType().GetProperty("MemberId")?.GetValue(member);
            if (idValue is not null && long.TryParse(idValue.ToString(), out var id)) yield return id;
        }
    }
}
