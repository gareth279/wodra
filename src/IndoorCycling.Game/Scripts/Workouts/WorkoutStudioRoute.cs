using System.Collections.Generic;
using System.Linq;
using IndoorCycling.Core.Workouts;

/// <summary>Fictional flat in-memory studio geometry; it carries no real-world coordinates or route identity.</summary>
public static class WorkoutStudioRoute
{
    public const string EnvironmentId = "training-studio";
    public static RouteDefinition Create()
    {
        const double length = 800, straight = 200, radius = 400 / (2 * System.Math.PI);
        var points = new List<RoutePoint>(81);
        for (var i = 0; i <= 80; i++)
        {
            var d = length * i / 80;
            double x, z;
            if (d <= straight) { x = -radius; z = -straight / 2 + d; }
            else if (d <= 400) { var a = System.Math.PI - (d - straight) / radius; x = radius * System.Math.Cos(a); z = straight / 2 + radius * System.Math.Sin(a); }
            else if (d <= 600) { x = radius; z = straight / 2 - (d - 400); }
            else { var a = -(d - 600) / radius; x = radius * System.Math.Cos(a); z = -straight / 2 + radius * System.Math.Sin(a); }
            points.Add(new RoutePoint(d, null, null, 0, 0, 0, x, z));
        }
        return new RouteDefinition("workout-studio-local-layout", "Training Studio", "Flat fictional indoor circuit for structured workouts.",
            length, [new(0, length, 0, "Studio circuit")], points)
        {
            Environment = RouteEnvironment.TrainingStudio,
            Discipline = RouteDiscipline.IndoorTraining,
            RouteType = "Loop", Difficulty = "Unrated", RouteDataVersion = 1,
            Capabilities = RouteCapabilities.SoloVelodrome,
            SupportedModes = [RideActivityMode.FreeRide],
            SourceAttribution = "Original fictional local-coordinate studio geometry; no geographic or GPS data.",
            PublishedTotalAscentMeters = 0
        };
    }
}

public static class WorkoutSelectionState
{
    public static string Normalize(string? id) => WorkoutCatalog.Find(id ?? "")?.Id ?? WorkoutCatalog.BuiltIns[0].Id;
}
