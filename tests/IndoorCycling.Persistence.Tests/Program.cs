using IndoorCycling.Core.Models;
using IndoorCycling.Persistence.Sqlite;

var temporaryRoot = Path.Combine(Path.GetTempPath(), "gwift-persistence-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temporaryRoot);
try
{
    var repository = new SqliteRideHistoryRepository(Path.Combine(temporaryRoot, "history.sqlite3"));
    await repository.InitializeAsync();
    var rider = await repository.EnsureRiderProfileAsync("Test Rider");
    Assert(rider.Name == "Test Rider", "first-run profile name");
    Assert((await repository.EnsureRiderProfileAsync("Ignored")).Id == rider.Id, "profile is stable across launches");
    Assert((await repository.GetProfileAsync(rider.Id))?.Name == "Test Rider", "profile roundtrip");
    var renamed = rider with { Name = "Updated Rider", UpdatedAtUtc = DateTimeOffset.UtcNow };
    await repository.SaveProfileAsync(renamed);
    Assert((await repository.GetProfileAsync(rider.Id))?.Name == "Updated Rider", "profile save");

    var accumulator = new RideActivityAccumulator();
    accumulator.AddFrame(2, 20, 100, 80, 20);
    accumulator.AddFrame(1, 40, 200, 100, 33);
    accumulator.AddFrame(0, 140, 4000, 300, 100); // paused/invalid frame is ignored
    accumulator.AddFrame(1, 30, 0, 0, 33); // missing sensor readings do not bias sensor averages
    var id = Guid.NewGuid();
    var started = DateTimeOffset.UtcNow.AddMinutes(-30);
    var first = accumulator.CreateDraft(id, rider.Id, "route-a", "Route A", started, 2500, 1000, 33, null);
    Assert(Math.Abs(first.DurationSeconds - 4) < .001, "active elapsed excludes paused frame");
    Assert(first.CompletedLaps == 2, "completed laps");
    Assert(Math.Abs(first.AverageSpeedKph!.Value - 27.5) < .001, "time-weighted average speed");
    Assert(first.MaxSpeedKph == 40, "maximum speed");
    Assert(Math.Abs(first.AveragePowerWatts!.Value - (400d / 3d)) < .001, "time-weighted valid power");
    Assert(first.MaxPowerWatts == 200, "maximum valid power");
    Assert(Math.Abs(first.AverageCadenceRpm!.Value - (260d / 3d)) < .001, "time-weighted valid cadence");
    Assert(first.MaxCadenceRpm == 100, "maximum valid cadence");
    Assert(first.ElevationGainMeters == 33 && first.FitFilePath is null, "elevation and optional FIT path");
    var media = new RideActivityMedia(Guid.NewGuid(), id, Path.Combine(temporaryRoot, "ride.png"), null, DateTimeOffset.UtcNow, "Clean", null);
    await repository.SaveCompletedActivityAsync(first, [media]);
    var saved = await repository.GetActivityAsync(id);
    Assert(saved is not null && saved.Status == RideActivityStatus.Completed && saved.FitFilePath is null, "activity and optional FIT path persist");
    Assert((await repository.GetActivityMediaAsync(id)).Single().ImagePath == media.ImagePath, "media association persists without copying");
    Assert(saved!.RouteId == "route-a" && Math.Abs(saved.DurationSeconds - 4) < .001 && saved.ElevationGainMeters == 33, "activity fields roundtrip");

    var secondId = Guid.NewGuid();
    var second = first with { Id = secondId, RouteId = "route-b", RouteName = "Route B", StartedAtUtc = started.AddMinutes(1) };
    await repository.SaveCompletedActivityAsync(second, []);
    Assert((await repository.GetRecentActivitiesAsync()).Select(a => a.Id).SequenceEqual([secondId, id]), "recent activities newest first");
    Assert((await repository.GetActivitiesForRouteAsync("route-a")).Select(a => a.Id).SequenceEqual([id]), "route-filtered rides");
    Assert(id != secondId, "activity ids are unique");

    var beforeFailedWrite = (await repository.GetRecentActivitiesAsync()).Count;
    var invalid = second with { Id = Guid.NewGuid(), RiderProfileId = Guid.NewGuid() };
    try { await repository.SaveCompletedActivityAsync(invalid, []); throw new Exception("invalid foreign key should fail"); }
    catch (Microsoft.Data.Sqlite.SqliteException) { }
    Assert((await repository.GetRecentActivitiesAsync()).Count == beforeFailedWrite, "failed persistence leaves existing activity data intact");
    Assert(accumulator.CreateDraft(id, rider.Id, "route-a", "Route A", started, 2500, 1000, 33, null) == first,
        "failed persistence leaves live activity statistics unchanged");
    await using (var database = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={repository.DatabasePath}"))
    {
        await database.OpenAsync(); await using var command = database.CreateCommand(); command.CommandText = "SELECT version FROM schema_info";
        Assert(Convert.ToInt32(await command.ExecuteScalarAsync()) == 1, "0 to 1 migration");
    }
    Console.WriteLine("PASS SQLite migration/profile/activity/media/recent ordering and accumulator tests");
}
finally { try { Directory.Delete(temporaryRoot, true); } catch { } }

static void Assert(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException("FAIL " + name);
}
