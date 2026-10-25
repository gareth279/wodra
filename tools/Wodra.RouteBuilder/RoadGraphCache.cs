using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Wodra.RouteBuilder;

internal sealed record RoadGraphCacheData(string CacheKey, List<RoadNode> Nodes, List<RoadEdge> Edges,
    int EligibleWayCount, int RetainedNodeCount, double CacheBuildSeconds);

public static class RoadGraphCache
{
    private const int RoadFilteringVersion = 4;
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    public static string GetCacheKey(string pbfPath, RouteAuthoringConfig config)
    {
        var file = new FileInfo(pbfPath);
        return BuildCacheKey(file.Name, file.Length, file.LastWriteTimeUtc.Ticks, config.Bbox, config.MarginMeters,
            config.ApprovedTrailWayIds, config.RestrictToApprovedTrailWayIds);
    }

    public static string BuildCacheKey(string filename, long fileLength, long lastWriteUtcTicks,
        BoundingBox bbox, double marginMeters, IReadOnlyCollection<long>? approvedTrailWayIds = null,
        bool restrictToApprovedTrailWayIds = false)
    {
        var identity = JsonSerializer.Serialize(new
        {
            pbfFilename = filename,
            pbfLength = fileLength,
            pbfLastWriteUtcTicks = lastWriteUtcTicks,
            bbox,
            marginMeters,
            roadFilteringVersion = RoadFilteringVersion,
            approvedTrailWayIds = approvedTrailWayIds?.Order().ToArray() ?? [],
            restrictToApprovedTrailWayIds
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
    }

    public static bool TryLoad(string cacheDirectory, string cacheKey, out RoadGraph? graph,
        out int retainedNodeCount, out int eligibleWayCount, out double cacheBuildSeconds)
    {
        graph = null;
        retainedNodeCount = 0;
        eligibleWayCount = 0;
        cacheBuildSeconds = 0;
        var path = CachePath(cacheDirectory, cacheKey);
        if (!File.Exists(path)) return false;
        try
        {
            return TryDeserializeGraph(File.ReadAllBytes(path), cacheKey, out graph,
                out retainedNodeCount, out eligibleWayCount, out cacheBuildSeconds);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static void Save(string cacheDirectory, string cacheKey, RoadGraph graph,
        int retainedNodeCount, int eligibleWayCount, double cacheBuildSeconds)
    {
        Directory.CreateDirectory(cacheDirectory);
        File.WriteAllBytes(CachePath(cacheDirectory, cacheKey), SerializeGraph(cacheKey, graph,
            retainedNodeCount, eligibleWayCount, cacheBuildSeconds));
    }

    public static byte[] SerializeGraph(string cacheKey, RoadGraph graph, int retainedNodeCount,
        int eligibleWayCount, double cacheBuildSeconds = 0)
    {
        var data = new RoadGraphCacheData(cacheKey, graph.Nodes.Values.ToList(), graph.Edges.ToList(),
            eligibleWayCount, retainedNodeCount, cacheBuildSeconds);
        return JsonSerializer.SerializeToUtf8Bytes(data);
    }

    public static bool TryDeserializeGraph(byte[] bytes, string expectedKey, out RoadGraph? graph,
        out int retainedNodeCount, out int eligibleWayCount, out double cacheBuildSeconds)
    {
        graph = null;
        retainedNodeCount = 0;
        eligibleWayCount = 0;
        cacheBuildSeconds = 0;
        try
        {
            var data = JsonSerializer.Deserialize<RoadGraphCacheData>(bytes, Options);
            if (data is null || data.CacheKey != expectedKey) return false;
            var result = new RoadGraph();
            foreach (var node in data.Nodes) result.AddNode(node);
            foreach (var edge in data.Edges) result.AddDirectedEdge(edge);
            graph = result;
            retainedNodeCount = data.RetainedNodeCount;
            eligibleWayCount = data.EligibleWayCount;
            cacheBuildSeconds = data.CacheBuildSeconds;
            return true;
        }
        catch (JsonException) { return false; }
    }

    private static string CachePath(string directory, string key) => Path.Combine(directory, $"road-graph-{key}.json");
}
