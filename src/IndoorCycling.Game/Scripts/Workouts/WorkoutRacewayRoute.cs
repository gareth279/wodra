using IndoorCycling.Core.Workouts;

/// <summary>Flat workout-only local projection of the reviewed Levels Raceway OSM centreline.</summary>
public static class WorkoutRacewayRoute
{
    public const string EnvironmentId = "raceway";
    public const string RouteId = "levels-raceway-workout";

    public static RouteDefinition Create()
    {
        var route = RouteDataLoader.LoadEmbedded(typeof(WorkoutRacewayRoute).Assembly,
            "Wodra.GameData.Routes.levels-raceway-workout.route-points.json",
            RouteId, "Levels Raceway · Timaru",
            "The mapped anticlockwise 2.40 km Levels Raceway circuit, presented as flat workout scenery.", []);
        return route with
        {
            Segments = [new(0, route.DistanceMeters, 0, "Levels Raceway full circuit")],
            Environment = RouteEnvironment.Raceway,
            Discipline = RouteDiscipline.Track,
            SupportedModes = [RideActivityMode.FreeRide],
            Capabilities = RouteCapabilities.SoloVelodrome,
            RouteType = "Loop",
            Difficulty = "Unrated",
            SourceAttribution = "© OpenStreetMap contributors, ODbL 1.0 (way 101182095, source=LINZ); used as flat workout scenery only.",
            RouteDataVersion = 1,
            PublishedTotalAscentMeters = 0,
            SupportsVerifiedCompletionAchievement = false
        };
    }
}
