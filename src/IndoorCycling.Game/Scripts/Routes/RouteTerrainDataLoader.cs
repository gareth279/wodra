using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;

public sealed record RouteTerrainChunkData(string Id, double OriginX, double OriginZ, int Columns, int Rows,
    double NearestRouteDistanceMeters, double?[] ElevationsMeters, bool[] CellMask);

public sealed record RouteTerrainData(string Id, string SourceDataset, string SourceCrs,
    double SpacingMeters, double CorridorRadiusMeters, double ChunkSizeMeters,
    double RouteStartElevationMeters, double VerticalBiasMeters, double RoadClearanceMeters,
    double RoadClearanceRadiusMeters, double MinimumSourceElevationMeters,
    double MaximumSourceElevationMeters, int VertexCount, int TriangleCount,
    int NoDataCells, int InterpolatedVertices, RouteTerrainChunkData[] Chunks)
{
    public double FarSpacingMeters { get; init; }
    public double FarRadiusMeters { get; init; }
    public double NearRadiusMeters { get; init; }
    public double TransitionOverlapMeters { get; init; }
    public double FarVerticalBiasMeters { get; init; }
    public int ElevationSamplerVersion { get; init; }
    public string ElevationSamplingMethod { get; init; } = "";
    public RouteTerrainChunkData[] FarChunks { get; init; } = [];
}

/// <summary>Loads compact, offline terrain authored from the route's LINZ DEM cache.</summary>
public static class RouteTerrainDataLoader
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    public static RouteTerrainData? TryLoadEmbedded(Assembly assembly, string resourceName, string routeId)
    {
        using var stream = RoutePackageFiles.Open(assembly, resourceName, routeId);
        if (stream is null) return null;
        var data = JsonSerializer.Deserialize<RouteTerrainData>(stream, Options)
            ?? throw new InvalidDataException($"Embedded terrain resource '{resourceName}' is empty.");
        if (!string.Equals(data.Id, routeId, StringComparison.Ordinal) || data.Chunks is null)
            throw new InvalidDataException($"Embedded terrain resource '{resourceName}' does not match route '{routeId}'.");
        if (string.IsNullOrWhiteSpace(data.SourceDataset) || !(data.SpacingMeters > 0)
            || !(data.CorridorRadiusMeters > 0) || !(data.ChunkSizeMeters > 0)
            || !double.IsFinite(data.RouteStartElevationMeters) || data.Chunks.Length == 0)
            throw new InvalidDataException($"Embedded terrain resource '{resourceName}' has invalid metadata.");
        foreach (var chunk in data.Chunks.Concat(data.FarChunks ?? []))
        {
            if (chunk.Columns < 1 || chunk.Rows < 1
                || chunk.ElevationsMeters.Length != (chunk.Columns + 1) * (chunk.Rows + 1)
                || chunk.CellMask.Length != chunk.Columns * chunk.Rows
                || chunk.ElevationsMeters.Any(value => value is { } elevation && !double.IsFinite(elevation)))
                throw new InvalidDataException($"Embedded terrain chunk '{chunk.Id}' has invalid grid data.");
        }
        return data;
    }
}
