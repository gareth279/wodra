namespace IndoorCycling.Core.Models;

public enum RouteEffortKind { FullRoute, Segment }

/// <summary>An authored course interval tied to one exact route geometry and direction.</summary>
public sealed record RouteEffortDefinition(
    string Id,
    int Version,
    string Name,
    RouteEffortKind Kind,
    double StartMeters,
    double EndMeters,
    string Direction = "forward");

/// <summary>A completed or practice timing attempt captured from authoritative route chainage.</summary>
public sealed record RouteEffortTimingResult(
    RouteEffortDefinition Definition,
    int AttemptIndex,
    double StartRouteDistanceMeters,
    double EndRouteDistanceMeters,
    double StartElapsedSeconds,
    double EndElapsedSeconds,
    bool Completed,
    bool Eligible,
    string? InvalidReason)
{
    public double ElapsedSeconds => Math.Max(0, EndElapsedSeconds - StartElapsedSeconds);
}

/// <summary>Durable per-profile result; activity linkage keeps the existing ride history authoritative.</summary>
public sealed record RouteEffortRecord(
    Guid Id,
    Guid ActivityId,
    Guid RiderProfileId,
    string RouteId,
    string RouteName,
    string GeometryFingerprint,
    int RouteDefinitionVersion,
    string Direction,
    string EffortId,
    int EffortVersion,
    string EffortName,
    RouteEffortKind Kind,
    int AttemptIndex,
    double StartRouteDistanceMeters,
    double EndRouteDistanceMeters,
    DateTimeOffset StartedAtUtc,
    double ElapsedSeconds,
    bool Completed,
    bool Eligible,
    string? InvalidReason,
    string? FitFilePath);

public sealed record RouteEffortHistory(IReadOnlyList<RouteEffortRecord> Attempts)
{
    public RouteEffortRecord? PersonalBest => Attempts.Where(item => item.Completed && item.Eligible)
        .OrderBy(item => item.ElapsedSeconds).ThenBy(item => item.StartedAtUtc).FirstOrDefault();
    public RouteEffortRecord? Previous => Attempts.Where(item => item.Completed && item.Eligible)
        .OrderByDescending(item => item.StartedAtUtc).ThenByDescending(item => item.AttemptIndex).FirstOrDefault();
}
