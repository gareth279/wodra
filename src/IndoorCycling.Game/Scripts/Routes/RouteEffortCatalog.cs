using IndoorCycling.Core.Models;
using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>Small authored route-effort catalogue. A geometry fingerprint mismatch disables only that definition.</summary>
public static class RouteEffortCatalog
{
    public const string ForwardDirection = "forward";
    public const string FullRouteEffortId = "route-lap.v1";

    // Signal Hill: route chainage 2,512.568 m (foothills, 7.4 m) to 6,753.961 m (upper lookout, 300.1 m).
    private const string SignalHillRouteId = "dunedin-signal-hill-climbing-loop";
    private const string SignalHillGeometry = "c40dc0e974b6ac57c70c0e3879c2620deabefbe4537ee5f63958bf222eed915b";
    private const int SignalHillRouteVersion = 1;
    private const double SignalHillStartMeters = 2512.5683455337644;
    private const double SignalHillFinishMeters = 6753.960514032031;

    // Existing built-in route segment: the northbound Cumberland Street leg, not its return leg.
    private const string OvalRouteId = "dunedin-one-way-oval-gardens";
    private const string OvalGeometry = "b8a2bbca1d952379b5fc0a8e44fcb44221f0db157241640112aef38c13ffcfd0";
    private const int OvalRouteVersion = 1;
    private const double CumberlandStartMeters = 998.6737252213936;
    private const double CumberlandFinishMeters = 1925.1127519957927;

    public static IReadOnlyList<RouteEffortDefinition> ForRoute(RouteDefinition route)
    {
        if (route.Environment != RouteEnvironment.Road || route.Points is not { Count: >= 2 } ||
            string.IsNullOrWhiteSpace(route.GeometryFingerprint) || route.DistanceMeters <= 0)
            return Array.Empty<RouteEffortDefinition>();

        var efforts = new List<RouteEffortDefinition>
        {
            new(FullRouteEffortId, 1, "Full route", RouteEffortKind.FullRoute, 0,
                route.DistanceMeters, ForwardDirection)
        };
        if (route.Id == SignalHillRouteId && route.RouteDataVersion == SignalHillRouteVersion &&
            string.Equals(route.GeometryFingerprint, SignalHillGeometry, StringComparison.OrdinalIgnoreCase))
            efforts.Add(new RouteEffortDefinition("segment.dunedin.signal-hill-climb.v1", 1,
                "Signal Hill climb", RouteEffortKind.Segment, SignalHillStartMeters,
                SignalHillFinishMeters, ForwardDirection));
        if (route.Id == OvalRouteId && route.RouteDataVersion == OvalRouteVersion &&
            string.Equals(route.GeometryFingerprint, OvalGeometry, StringComparison.OrdinalIgnoreCase))
            efforts.Add(new RouteEffortDefinition("segment.dunedin.cumberland-northbound.v1", 1,
                "Cumberland Street northbound", RouteEffortKind.Segment, CumberlandStartMeters,
                CumberlandFinishMeters, ForwardDirection));
        return efforts;
    }

    public static bool IsCurrentDefinition(RouteDefinition route, RouteEffortRecord effort)
    {
        var definition = ForRoute(route).FirstOrDefault(candidate => candidate.Id == effort.EffortId &&
            candidate.Version == effort.EffortVersion);
        return definition is not null && effort.RouteId == route.Id &&
            effort.RouteDefinitionVersion == route.RouteDataVersion &&
            string.Equals(effort.GeometryFingerprint, route.GeometryFingerprint, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(effort.Direction, definition.Direction, StringComparison.OrdinalIgnoreCase);
    }
}
