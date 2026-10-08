using System.Diagnostics;
using OsmSharp;
using OsmSharp.Streams;

namespace Wodra.RouteBuilder;

public sealed record GraphLoadResult(RoadGraph Graph, GraphBuildStats Stats);
internal sealed record WayRecord(long Id, long[] NodeIds, Dictionary<string, string> Tags);

public static class PbfRoadGraphLoader
{
    public static GraphLoadResult Load(string pbfPath, RouteAuthoringConfig config, string? cacheDirectory = null)
    {
        if (!File.Exists(pbfPath)) throw new FileNotFoundException("OSM PBF file was not found.", pbfPath);
        config.Validate();
        var cacheKey = RoadGraphCache.GetCacheKey(pbfPath, config);
        if (!string.IsNullOrWhiteSpace(cacheDirectory))
        {
            var cacheTimer = Stopwatch.StartNew();
            if (RoadGraphCache.TryLoad(cacheDirectory, cacheKey, out var cachedGraph,
                out var cachedNodeCount, out var cachedWayCount, out var loadedCacheBuildSeconds))
            {
                cacheTimer.Stop();
                var cached = cachedGraph!;
                return new GraphLoadResult(cached, new GraphBuildStats(cachedNodeCount, cachedWayCount,
                    cached.Nodes.Count, cached.EdgeCount, 0, 0, 0, true, cacheTimer.Elapsed.TotalSeconds,
                    0, loadedCacheBuildSeconds));
            }
        }
        var marginLat = config.MarginMeters / 111_320d;
        var middleLatitudeRadians = (config.Bbox.MinLatitude + config.Bbox.MaxLatitude) / 2 * Math.PI / 180;
        var marginLon = marginLat / Math.Max(0.01, Math.Cos(middleLatitudeRadians));
        var nodeBox = new BoundingBox(config.Bbox.MinLatitude - marginLat, config.Bbox.MinLongitude - marginLon,
            config.Bbox.MaxLatitude + marginLat, config.Bbox.MaxLongitude + marginLon);

        var nodeTimer = Stopwatch.StartNew();
        var coordinates = new Dictionary<long, GeoPoint>();
        using (var stream = File.OpenRead(pbfPath))
        foreach (var item in new PBFOsmStreamSource(stream))
        {
            if (item is not Node node || node.Id is not long nodeId
                || node.Latitude is not double latitude || node.Longitude is not double longitude) continue;
            if (latitude < nodeBox.MinLatitude || latitude > nodeBox.MaxLatitude
                || longitude < nodeBox.MinLongitude || longitude > nodeBox.MaxLongitude) continue;
            coordinates[nodeId] = new GeoPoint(latitude, longitude);
        }
        nodeTimer.Stop();

        var wayTimer = Stopwatch.StartNew();
        var wayRecords = new List<WayRecord>();
        using (var stream = File.OpenRead(pbfPath))
        foreach (var item in new PBFOsmStreamSource(stream))
        {
            if (item is not Way way || way.Id is not long wayId || way.Nodes is null || way.Tags is null) continue;
            var tags = way.Tags.ToDictionary(tag => tag.Key, tag => tag.Value, StringComparer.OrdinalIgnoreCase);
            if (!RoadEligibility.TryGetHighway(tags, out _)) continue;
            if (!HasRetainedSegment(way.Nodes, coordinates)) continue;
            wayRecords.Add(new WayRecord(wayId, way.Nodes, tags));
        }
        wayTimer.Stop();

        var graphTimer = Stopwatch.StartNew();
        var graph = new RoadGraph();
        foreach (var way in wayRecords)
            graph.AddWay(way.Id, way.NodeIds, way.Tags, coordinates);
        graphTimer.Stop();
        var cacheBuildSeconds = nodeTimer.Elapsed.TotalSeconds + wayTimer.Elapsed.TotalSeconds + graphTimer.Elapsed.TotalSeconds;

        var cacheSaveSeconds = 0d;
        if (!string.IsNullOrWhiteSpace(cacheDirectory))
        {
            var cacheTimer = Stopwatch.StartNew();
            RoadGraphCache.Save(cacheDirectory, cacheKey, graph, coordinates.Count, wayRecords.Count, cacheBuildSeconds);
            cacheTimer.Stop();
            cacheSaveSeconds = cacheTimer.Elapsed.TotalSeconds;
        }

        return new GraphLoadResult(graph, new GraphBuildStats(coordinates.Count, wayRecords.Count,
            graph.Nodes.Count, graph.EdgeCount, nodeTimer.Elapsed.TotalSeconds, wayTimer.Elapsed.TotalSeconds,
            graphTimer.Elapsed.TotalSeconds, false, 0, cacheSaveSeconds, cacheBuildSeconds));
    }

    private static bool HasRetainedSegment(IReadOnlyList<long> nodeIds, IReadOnlyDictionary<long, GeoPoint> coordinates)
    {
        for (var i = 1; i < nodeIds.Count; i++)
            if (coordinates.ContainsKey(nodeIds[i - 1]) && coordinates.ContainsKey(nodeIds[i])) return true;
        return false;
    }
}
