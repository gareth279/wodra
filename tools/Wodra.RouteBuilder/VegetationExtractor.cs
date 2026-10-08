using OsmSharp;
using OsmSharp.Streams;

namespace Wodra.RouteBuilder;

public sealed record VegetationPoint(double Longitude, double Latitude);
public sealed record VegetationArea(string OsmType, long OsmId, string? Name, string Category,
    IReadOnlyList<double[]> Polygon, double AreaSquareMeters, double DistanceFromRouteMeters,
    double NearestRouteDistanceMeters, double? BaseElevationMeters, double ElevationVariationMeters,
    bool RenderGround, int GeneratedTreeCount, int OriginalVertexCount, int ElevationSampleCount);
public sealed record VegetationTree(string Source, long OsmId, double Latitude, double Longitude,
    double? ElevationMeters, string TreeType, double HeightMeters, double CanopyRadiusMeters,
    double NearestRouteDistanceMeters);
public sealed record VegetationExtractionResult(int PolygonsConsidered, int PolygonsIncluded,
    IReadOnlyDictionary<string, int> CategoryCounts, int NamedAreas, int ExplicitTreeCount, int TreeRowCount,
    int TreeRowInstances, int GeneratedTreeCount, int TreesRejectedForRoadProximity,
    int TreesRejectedForBuildingIntersection, int TreesRejectedAsDuplicates, int TreesRejectedNearLandmarks,
    int OriginalPolygonVertices, int SimplifiedPolygonVertices, int LargestPolygonVertices,
    int MalformedOrSkippedGeometries, int GeneratedTreeCap, IReadOnlyList<VegetationArea> Areas,
    IReadOnlyList<VegetationTree> Trees, double GenerationSeconds, int DemSamples = 0, int DemCacheHits = 0);

public sealed record RawVegetationArea(string OsmType, long OsmId, Dictionary<string, string> Tags,
    long[] NodeIds, IReadOnlyList<GeoPoint> Polygon, string Category);
public sealed record RawTreeRow(long OsmId, IReadOnlyList<GeoPoint> Geometry);
public sealed record ExplicitTreeCandidate(long OsmId, GeoPoint Point);

/// <summary>Extracts nearby OSM green areas and explicit trees, then deterministically fills suitable polygons.</summary>
public static class VegetationExtractor
{
    public const double DefaultSearchRadiusMeters = 200;
    public const double DefaultSimplificationToleranceMeters = 1;
    public const double DefaultTreeRowSpacingMeters = 10;
    public const int DefaultPerAreaTreeCap = 150;
    public const int DefaultTreeCap = 1200;
    private const double CoordinateMarginMeters = 2500;
    private const double EarthRadiusMeters = 6371000;
    private const double GeneratedRoadClearanceMeters = 7;
    private const double ExplicitRoadClearanceMeters = 3.75;
    private const double TreeSpacingMeters = 7;

    public static VegetationExtractionResult Extract(string pbfPath, IReadOnlyList<RoutePointOutput> route,
        IReadOnlyList<RouteBuilding> buildings, IReadOnlyList<RouteLandmark>? landmarks = null,
        double radiusMeters = DefaultSearchRadiusMeters, double toleranceMeters = DefaultSimplificationToleranceMeters,
        double rowSpacingMeters = DefaultTreeRowSpacingMeters, int perAreaCap = DefaultPerAreaTreeCap,
        int totalCap = DefaultTreeCap, string routeId = "route")
    {
        if (route.Count < 2) throw new ArgumentException("A route polyline needs at least two points.", nameof(route));
        if (radiusMeters <= 0 || toleranceMeters is < .5 or > 1.5 || rowSpacingMeters is < 8 or > 12
            || perAreaCap <= 0 || totalCap <= 0) throw new ArgumentOutOfRangeException(nameof(radiusMeters));
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var minLat = route.Min(item => item.Latitude); var maxLat = route.Max(item => item.Latitude);
        var minLon = route.Min(item => item.Longitude); var maxLon = route.Max(item => item.Longitude);
        var marginLat = CoordinateMarginMeters / 111_320d;
        var marginLon = marginLat / Math.Max(.01, Math.Cos((minLat + maxLat) * Math.PI / 360));
        var coordinates = new Dictionary<long, GeoPoint>();
        var wayNodes = new Dictionary<long, long[]>();
        var rawAreas = new List<RawVegetationArea>();
        var rows = new List<RawTreeRow>();
        var explicitTrees = new List<(long Id, GeoPoint Point)>();
        var skipped = 0; var considered = 0;
        using var stream = File.OpenRead(pbfPath);
        foreach (var element in new PBFOsmStreamSource(stream))
        {
            if (element is Node node && node.Id is long nodeId && node.Latitude is double lat && node.Longitude is double lon)
            {
                if (lat >= minLat - marginLat && lat <= maxLat + marginLat && lon >= minLon - marginLon && lon <= maxLon + marginLon)
                    coordinates[nodeId] = new GeoPoint(lat, lon);
                if (node.Tags is not null && node.Tags.TryGetValue("natural", out var natural) && natural == "tree"
                    && lat >= minLat - marginLat && lat <= maxLat + marginLat && lon >= minLon - marginLon && lon <= maxLon + marginLon)
                {
                    considered++;
                    explicitTrees.Add((nodeId, new GeoPoint(lat, lon)));
                }
            }
            else if (element is Way way && way.Id is long wayId && way.Nodes is { Length: >= 2 })
            {
                if (!way.Nodes.Any(coordinates.ContainsKey)) continue;
                wayNodes[wayId] = way.Nodes;
                if (way.Tags is null) continue;
                var tags = OsmPbfGeometry.ToTags(way.Tags);
                if (string.Equals(tags.GetValueOrDefault("natural"), "tree_row", StringComparison.OrdinalIgnoreCase))
                {
                    considered++;
                    if (way.Nodes.All(coordinates.ContainsKey))
                    {
                        var line = way.Nodes.Select(id => coordinates[id]).ToArray();
                        if (line.Length >= 2) rows.Add(new RawTreeRow(wayId, line)); else skipped++;
                    }
                    else skipped++;
                    continue;
                }
                var category = ClassifyArea(tags);
                if (category is null) continue;
                considered++;
                if (way.Nodes.Length < 4 || way.Nodes[0] != way.Nodes[^1] || way.Nodes.Any(id => !coordinates.ContainsKey(id)))
                {
                    var available = way.Nodes.Where(coordinates.ContainsKey).Select(id => coordinates[id]).ToArray();
                    if (available.Length > 0 && available.Min(point => LandmarkExtractor.PointToPolylineDistanceMeters(point, route)) <= radiusMeters)
                        skipped++;
                    continue;
                }
                if (tags.GetValueOrDefault("type") == "multipolygon") continue;
                rawAreas.Add(new RawVegetationArea("way", wayId, tags, way.Nodes,
                    way.Nodes.Select(id => coordinates[id]).ToArray(), category));
            }
            else if (element is Relation relation && relation.Tags is not null)
            {
                var tags = OsmPbfGeometry.ToTags(relation.Tags);
                var category = ClassifyArea(tags);
                if (category is null || !string.Equals(tags.GetValueOrDefault("type"), "multipolygon", StringComparison.OrdinalIgnoreCase)) continue;
                var members = OsmPbfGeometry.RelationWayMembers(relation).ToArray();
                var outerIds = members.Where(item => item.Role is "" or "outer").Select(item => item.Id).ToArray();
                if (!outerIds.Any(wayNodes.ContainsKey)) continue;
                considered++;
                var ring = OsmPbfGeometry.StitchOuterRing(outerIds, wayNodes);
                if (ring is null || ring.Count < 4 || ring[0] != ring[^1] || ring.Any(id => !coordinates.ContainsKey(id)))
                {
                    var available = outerIds.Where(wayNodes.ContainsKey).SelectMany(id => wayNodes[id])
                        .Where(coordinates.ContainsKey).Distinct().Select(id => coordinates[id]).ToArray();
                    if (available.Length > 0 && available.Min(point => LandmarkExtractor.PointToPolylineDistanceMeters(point, route)) <= radiusMeters)
                        skipped++;
                    continue;
                }
                var polygon = ring.Select(id => coordinates[id]).ToArray();
                if (!IsValidPolygon(polygon)) { skipped++; continue; }
                if (members.Any(item => item.Role == "inner"))
                {
                    if (BuildingExtractor.PolygonToRouteDistance(polygon, route).DistanceMeters <= radiusMeters) skipped++;
                    continue;
                }
                rawAreas.Add(new RawVegetationArea("relation", relation.Id ?? 0, tags, ring.ToArray(), polygon, category));
            }
        }

        var areas = new List<(VegetationArea Area, IReadOnlyList<GeoPoint> Original)>();
        foreach (var item in rawAreas.OrderBy(area => area.OsmType, StringComparer.Ordinal).ThenBy(area => area.OsmId))
        {
            if (!IsValidPolygon(item.Polygon)) { skipped++; continue; }
            var proximity = BuildingExtractor.PolygonToRouteDistance(item.Polygon, route);
            if (proximity.DistanceMeters > radiusMeters || IsPrivateOrTiny(item)) continue;
            var simple = Simplify(item.Polygon, toleranceMeters);
            if (!IsValidPolygon(simple)) { skipped++; continue; }
            var areaSqm = PolygonAreaSquareMeters(item.Polygon);
            areas.Add((new VegetationArea(item.OsmType, item.OsmId,
                item.Tags.GetValueOrDefault("name") ?? item.Tags.GetValueOrDefault("name:en"), item.Category,
                simple.Select(point => new[] { point.Longitude, point.Latitude }).ToArray(), areaSqm,
                proximity.DistanceMeters, proximity.RouteDistanceMeters, null, 0, proximity.DistanceMeters >= 5, 0,
                item.Polygon.Count, 0), item.Polygon));
        }

        var routeExplicit = explicitTrees.Select(item =>
        {
            var nearest = LandmarkExtractor.PointToRoute(item.Point, route);
            return (item.Id, item.Point, nearest.DistanceMeters, nearest.RouteDistanceMeters);
        }).Where(item => item.DistanceMeters <= radiusMeters).OrderBy(item => item.RouteDistanceMeters).ThenBy(item => item.Id).ToArray();
        var acceptedTrees = new List<VegetationTree>();
        var acceptedTreePoints = new List<GeoPoint>();
        var rejectedRoad = 0; var rejectedBuildings = 0; var rejectedDuplicates = 0; var rejectedLandmarks = 0;
        foreach (var tree in routeExplicit)
        {
            if (tree.DistanceMeters < ExplicitRoadClearanceMeters) { rejectedRoad++; continue; }
            acceptedTrees.Add(CreateTree("osm-tree", tree.Id, tree.Point, tree.RouteDistanceMeters, tree.Id, "generic"));
            acceptedTreePoints.Add(tree.Point);
        }

        var generatedRowInstances = 0; var nearbyTreeRows = 0;
        foreach (var row in rows.OrderBy(item => item.OsmId))
        {
            if (PolylineToRouteDistance(row.Geometry, route) > radiusMeters) continue;
            nearbyTreeRows++;
            foreach (var point in PlaceAlongLine(row.Geometry, rowSpacingMeters))
            {
                var nearest = LandmarkExtractor.PointToRoute(point, route);
                if (nearest.DistanceMeters < ExplicitRoadClearanceMeters) { rejectedRoad++; continue; }
                if (acceptedTreePoints.Any(existing => DistanceMeters(point, existing) < 5))
                { rejectedDuplicates++; continue; }
                if (acceptedTrees.Count >= totalCap) { rejectedDuplicates++; continue; }
                acceptedTrees.Add(CreateTree("tree-row", row.OsmId, point, nearest.RouteDistanceMeters, row.OsmId + (long)(point.Longitude * 1e8), "generic"));
                acceptedTreePoints.Add(point);
                generatedRowInstances++;
            }
        }

        var generatedTotal = 0;
        var acceptedAreas = new List<(VegetationArea Area, IReadOnlyList<GeoPoint> Original)>();
        foreach (var (area, original) in areas)
        {
            var categoryCount = 0;
            if (SupportsGeneratedTrees(area.Category))
            {
                var density = area.Category is "wood" or "forest" ? 200d : 450d;
                var requested = Math.Min(perAreaCap, (int)Math.Floor(area.AreaSquareMeters / density));
                var remainingGlobal = Math.Max(0, totalCap - acceptedTrees.Count);
                requested = Math.Min(requested, remainingGlobal);
                var maxAttempts = Math.Max(requested * 60, 20);
                var candidates = GenerateDeterministicPoints(original, routeId, area.OsmId, maxAttempts, area.OsmType);
                var landmarkPositions = landmarks?.Select(mark => new GeoPoint(mark.Latitude, mark.Longitude)).ToArray() ?? [];
                foreach (var point in candidates)
                {
                    if (categoryCount >= requested) break;
                    var nearest = LandmarkExtractor.PointToRoute(point, route);
                    var rejection = GeneratedTreeRejection(point, route, buildings, acceptedTreePoints, landmarkPositions);
                    if (rejection == "road") { rejectedRoad++; continue; }
                    if (rejection == "building") { rejectedBuildings++; continue; }
                    if (rejection == "duplicate") { rejectedDuplicates++; continue; }
                    if (rejection == "landmark") { rejectedLandmarks++; continue; }
                    var variationSeed = Seed(routeId, area.OsmId, $"tree-{generatedTotal}");
                    var sizeRng = new DeterministicRandom(variationSeed);
                    var height = 4 + 8 * sizeRng.NextDouble();
                    var type = area.Category is "wood" or "forest" && sizeRng.NextDouble() < .55 ? "conifer" : "broadleaf";
                    acceptedTrees.Add(new VegetationTree("generated", area.OsmId, point.Latitude, point.Longitude,
                        null, type, height, height * .2, nearest.RouteDistanceMeters));
                    acceptedTreePoints.Add(point);
                    categoryCount++; generatedTotal++;
                }
            }
            acceptedAreas.Add((area with { GeneratedTreeCount = categoryCount }, original));
        }
        timer.Stop();
        var categories = acceptedAreas.GroupBy(item => item.Area.Category, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var finalTrees = acceptedTrees.OrderBy(item => item.NearestRouteDistanceMeters).ThenBy(item => item.Source, StringComparer.Ordinal)
            .ThenBy(item => item.OsmId).ThenBy(item => item.Latitude).ThenBy(item => item.Longitude).ToArray();
        return new VegetationExtractionResult(considered, acceptedAreas.Count, categories,
            acceptedAreas.Count(item => !string.IsNullOrWhiteSpace(item.Area.Name)), routeExplicit.Length - routeExplicit.Count(item => item.DistanceMeters < ExplicitRoadClearanceMeters),
            nearbyTreeRows, generatedRowInstances, generatedTotal, rejectedRoad, rejectedBuildings, rejectedDuplicates, rejectedLandmarks,
            acceptedAreas.Sum(item => item.Area.OriginalVertexCount), acceptedAreas.Sum(item => item.Area.Polygon.Count),
            acceptedAreas.Count == 0 ? 0 : acceptedAreas.Max(item => item.Area.Polygon.Count), skipped,
            totalCap, acceptedAreas.Select(item => item.Area).ToArray(), finalTrees, timer.Elapsed.TotalSeconds);
    }

    public static string? ClassifyArea(IReadOnlyDictionary<string, string> tags)
    {
        var leisure = tags.GetValueOrDefault("leisure"); var landuse = tags.GetValueOrDefault("landuse");
        var natural = tags.GetValueOrDefault("natural");
        if (leisure is "park" or "garden") return leisure;
        if (leisure == "nature_reserve") return "nature-reserve";
        if (landuse == "forest" || natural == "wood") return "wood";
        if (landuse == "grass" || natural == "grassland") return landuse == "grass" ? "grass" : "grassland";
        if (landuse == "recreation_ground") return "recreation";
        if (landuse == "meadow") return "meadow";
        return null;
    }

    public static RawVegetationArea? CreateClosedAreaWay(long id, IReadOnlyList<long> nodeIds,
        Dictionary<string, string> tags, IReadOnlyDictionary<long, GeoPoint> coordinates)
    {
        var category = ClassifyArea(tags);
        if (category is null || nodeIds.Count < 4 || nodeIds[0] != nodeIds[^1]
            || nodeIds.Any(node => !coordinates.ContainsKey(node))) return null;
        var polygon = nodeIds.Select(node => coordinates[node]).ToArray();
        return IsValidPolygon(polygon) ? new RawVegetationArea("way", id, tags, nodeIds.ToArray(), polygon, category) : null;
    }

    public static ExplicitTreeCandidate? CreateTreeNode(long id, GeoPoint point, IReadOnlyDictionary<string, string> tags) =>
        string.Equals(tags.GetValueOrDefault("natural"), "tree", StringComparison.OrdinalIgnoreCase)
            ? new ExplicitTreeCandidate(id, point) : null;

    public static IReadOnlyList<GeoPoint> GenerateDeterministicPoints(IReadOnlyList<GeoPoint> polygon,
        string routeId, long osmId, int attemptCount, string osmType = "way")
    {
        if (polygon.Count < 4 || attemptCount <= 0) return [];
        var local = ToLocalPolygon(polygon); var bounds = (MinX: local.Min(item => item.X), MaxX: local.Max(item => item.X),
            MinY: local.Min(item => item.Y), MaxY: local.Max(item => item.Y));
        var random = new DeterministicRandom(Seed(routeId, osmId, osmType));
        var points = new List<GeoPoint>();
        for (var i = 0; i < attemptCount; i++)
        {
            var x = bounds.MinX + random.NextDouble() * (bounds.MaxX - bounds.MinX);
            var y = bounds.MinY + random.NextDouble() * (bounds.MaxY - bounds.MinY);
            if (PointInPolygon(x, y, local)) points.Add(FromLocal(x, y, polygon[0]));
        }
        return points;
    }

    public static string? GeneratedTreeRejection(GeoPoint point, IReadOnlyList<RoutePointOutput> route,
        IReadOnlyList<RouteBuilding> buildings, IReadOnlyList<GeoPoint> existingTrees,
        IReadOnlyList<GeoPoint> landmarkPositions, double roadClearanceMeters = GeneratedRoadClearanceMeters)
    {
        if (LandmarkExtractor.PointToPolylineDistanceMeters(point, route) < roadClearanceMeters) return "road";
        if (buildings.Any(building => PointInPolygon(point, building.Footprint))) return "building";
        if (existingTrees.Any(tree => DistanceMeters(point, tree) < TreeSpacingMeters)) return "duplicate";
        if (landmarkPositions.Any(mark => DistanceMeters(point, mark) < 12)) return "landmark";
        return null;
    }

    public static IReadOnlyList<GeoPoint> PlaceAlongLine(IReadOnlyList<GeoPoint> line, double spacingMeters)
    {
        if (line.Count < 2 || spacingMeters <= 0) return [];
        var lengths = new double[line.Count];
        for (var i = 1; i < line.Count; i++) lengths[i] = lengths[i - 1] + GeoMath.HaversineMeters(line[i - 1], line[i]);
        var result = new List<GeoPoint>();
        for (var distance = spacingMeters / 2; distance < lengths[^1]; distance += spacingMeters)
        {
            var index = Array.FindIndex(lengths, value => value >= distance);
            if (index <= 0) continue;
            var span = lengths[index] - lengths[index - 1]; var t = span > 0 ? (distance - lengths[index - 1]) / span : 0;
            result.Add(new GeoPoint(line[index - 1].Latitude + (line[index].Latitude - line[index - 1].Latitude) * t,
                line[index - 1].Longitude + (line[index].Longitude - line[index - 1].Longitude) * t));
        }
        return result;
    }

    public static IReadOnlyList<GeoPoint> Simplify(IReadOnlyList<GeoPoint> closed, double toleranceMeters) =>
        BuildingExtractor.Simplify(closed, toleranceMeters);

    public static IReadOnlyList<GeoPoint> AreaElevationSamples(IReadOnlyList<double[]> polygon)
    {
        var points = polygon.Select(item => new GeoPoint(item[1], item[0])).ToArray();
        if (points.Length < 4) return points.Length == 0 ? [] : [points[0]];
        var local = ToLocalPolygon(points); var minX = local.Min(point => point.X); var maxX = local.Max(point => point.X);
        var minY = local.Min(point => point.Y); var maxY = local.Max(point => point.Y);
        var candidates = new List<GeoPoint>();
        var center = FromLocal((minX + maxX) / 2, (minY + maxY) / 2, points[0]);
        if (PointInPolygon(center, points)) candidates.Add(center);
        foreach (var fx in new[] { .25, .75 })
        foreach (var fy in new[] { .25, .75 })
        {
            var point = FromLocal(minX + (maxX - minX) * fx, minY + (maxY - minY) * fy, points[0]);
            if (PointInPolygon(point, points)) candidates.Add(point);
        }
        if (candidates.Count == 0) candidates.Add(points[0]);
        return candidates.Distinct().ToArray();
    }

    public static double PolygonAreaSquareMeters(IReadOnlyList<GeoPoint> polygon)
    {
        var local = ToLocalPolygon(polygon); var twice = 0d;
        for (var i = 0; i < local.Count; i++)
        {
            var a = local[i]; var b = local[(i + 1) % local.Count]; twice += a.X * b.Y - b.X * a.Y;
        }
        return Math.Abs(twice) / 2;
    }

    public static bool PointInPolygon(GeoPoint point, IReadOnlyList<GeoPoint> polygon)
    {
        if (polygon.Count < 4) return false;
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

    private static bool IsPrivateOrTiny(RawVegetationArea item)
    {
        if (item.Tags.GetValueOrDefault("access") is "private" or "no" || item.Tags.GetValueOrDefault("garden") == "private") return true;
        var minimum = item.Category switch
        {
            "park" => 500d, "garden" => 300d, "nature-reserve" or "wood" or "forest" => 800d,
            "recreation" => 700d, _ => 500d
        };
        if (!string.IsNullOrWhiteSpace(item.Tags.GetValueOrDefault("name"))) minimum *= .5;
        return PolygonAreaSquareMeters(item.Polygon) < minimum;
    }

    private static bool SupportsGeneratedTrees(string category) => category is "park" or "garden" or "nature-reserve" or "wood" or "forest";
    private static bool IsValidPolygon(IReadOnlyList<GeoPoint> polygon) => polygon.Count >= 4
        && GeoMath.HaversineMeters(polygon[0], polygon[^1]) <= .05
        && polygon.Take(polygon.Count - 1).Distinct().Count() >= 3
        && PolygonAreaSquareMeters(polygon) > 1;

    private static double PolylineToRouteDistance(IReadOnlyList<GeoPoint> line, IReadOnlyList<RoutePointOutput> route)
    {
        var best = double.PositiveInfinity;
        for (var i = 1; i < line.Count; i++)
        {
            var length = GeoMath.HaversineMeters(line[i - 1], line[i]); var samples = Math.Max(1, (int)Math.Ceiling(length / 5));
            for (var j = 0; j <= samples; j++)
            {
                var t = j / (double)samples;
                var p = new GeoPoint(line[i - 1].Latitude + (line[i].Latitude - line[i - 1].Latitude) * t,
                    line[i - 1].Longitude + (line[i].Longitude - line[i - 1].Longitude) * t);
                best = Math.Min(best, LandmarkExtractor.PointToPolylineDistanceMeters(p, route));
            }
        }
        return best;
    }

    private static IReadOnlyList<(double X, double Y)> ToLocalPolygon(IReadOnlyList<GeoPoint> polygon)
    {
        var origin = polygon[0]; const double rad = Math.PI / 180;
        var sx = EarthRadiusMeters * Math.Cos(origin.Latitude * rad) * rad; var sy = EarthRadiusMeters * rad;
        return polygon.Select(point => ((point.Longitude - origin.Longitude) * sx, (point.Latitude - origin.Latitude) * sy)).ToArray();
    }

    private static GeoPoint FromLocal(double x, double y, GeoPoint origin)
    {
        const double rad = Math.PI / 180;
        return new GeoPoint(origin.Latitude + y / (EarthRadiusMeters * rad),
            origin.Longitude + x / (EarthRadiusMeters * Math.Cos(origin.Latitude * rad) * rad));
    }

    private static bool PointInPolygon(double x, double y, IReadOnlyList<(double X, double Y)> polygon)
    {
        var inside = false;
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
            if ((polygon[i].Y > y) != (polygon[j].Y > y)
                && x < (polygon[j].X - polygon[i].X) * (y - polygon[i].Y) / (polygon[j].Y - polygon[i].Y) + polygon[i].X)
                inside = !inside;
        return inside;
    }

    private static bool PointInPolygon(GeoPoint point, IReadOnlyList<BuildingFootprintPoint> polygon) =>
        PointInPolygon(point, polygon.Select(item => new GeoPoint(item.Latitude, item.Longitude)).ToArray());

    private static double DistanceMeters(GeoPoint a, GeoPoint b) => GeoMath.HaversineMeters(a, b);
    private static VegetationTree CreateTree(string source, long id, GeoPoint point, double routeDistance, long seed, string type)
    {
        var random = new DeterministicRandom(Mix(unchecked((ulong)seed))); var height = 4 + 8 * random.NextDouble();
        return new VegetationTree(source, id, point.Latitude, point.Longitude, null, type, height, height * .2, routeDistance);
    }
    private static ulong Seed(string route, long id, string type)
    {
        var hash = 14695981039346656037UL;
        foreach (var c in route + ":" + type + ":" + id)
        { hash ^= c; hash *= 1099511628211UL; }
        return Mix(hash);
    }
    private static ulong Mix(ulong value)
    {
        value += 0x9E3779B97F4A7C15UL; value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
        value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL; return value ^ (value >> 31);
    }

    private struct DeterministicRandom(ulong seed)
    {
        private ulong _state = seed == 0 ? 0x2545F4914F6CDD1DUL : seed;
        public double NextDouble()
        {
            var x = _state; x ^= x >> 12; x ^= x << 25; x ^= x >> 27; _state = x;
            return ((x * 2685821657736338717UL) >> 11) * (1.0 / (1UL << 53));
        }
    }
}
