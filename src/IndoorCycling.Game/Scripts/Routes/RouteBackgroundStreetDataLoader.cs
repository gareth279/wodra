using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;

public sealed record RouteBackgroundStreetPoint(double Latitude,double Longitude,double ElevationMeters);
public sealed record RouteBackgroundStreet(long OsmId,string? Name,string? Ref,string Highway,bool Oneway,
    double WidthMeters,string WidthSource,IReadOnlyList<RouteBackgroundStreetPoint> Points,
    double DistanceFromRouteMeters,double NearestRouteDistanceMeters);
public sealed record RouteBackgroundStreetData(string Id,IReadOnlyList<RouteBackgroundStreet> Streets)
{
    public int ElevationSamplerVersion { get; init; }
}

public static class RouteBackgroundStreetDataLoader
{
    private sealed record StreetFile(string? Id,IReadOnlyList<RouteBackgroundStreet>? Streets,
        int ElevationSamplerVersion = 0);
    private static readonly JsonSerializerOptions Options=new(){PropertyNameCaseInsensitive=true};
    public static RouteBackgroundStreetData LoadEmbedded(Assembly assembly,string resourceName,string routeId)
    {
        using var stream=RoutePackageFiles.Open(assembly,resourceName,routeId)??throw new InvalidDataException($"Route background streets '{resourceName}' not found.");
        var file=JsonSerializer.Deserialize<StreetFile>(stream,Options)??throw new InvalidDataException($"Embedded background streets '{resourceName}' is empty.");
        if(file.Id!=routeId||file.Streets is null)throw new InvalidDataException($"Embedded background streets do not match '{routeId}'.");
        if(file.Streets.Any(s=>s.OsmId<=0||!double.IsFinite(s.WidthMeters)||s.WidthMeters is <2 or >40||s.Points.Count<2||s.Points.Any(p=>!double.IsFinite(p.Latitude)||!double.IsFinite(p.Longitude)||!double.IsFinite(p.ElevationMeters))))
            throw new InvalidDataException("Embedded background street geometry contains invalid dimensions or coordinates.");
        return new RouteBackgroundStreetData(routeId,file.Streets)
        {
            ElevationSamplerVersion = file.ElevationSamplerVersion
        };
    }
    public static RouteBackgroundStreetData? TryLoadEmbedded(Assembly assembly,string resourceName,string routeId)
    {
        using var probe=RoutePackageFiles.Open(assembly,resourceName,routeId);if(probe is null)return null;
        return LoadEmbedded(assembly,resourceName,routeId);
    }
}
