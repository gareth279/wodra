namespace IndoorCycling.Core.Models;

public sealed record RouteProfile(
    string Name,
    IReadOnlyList<RoutePoint> Points
)
{
    public double TotalDistanceMeters =>
        Points.Count == 0 ? 0 : Points[^1].DistanceMeters;
}