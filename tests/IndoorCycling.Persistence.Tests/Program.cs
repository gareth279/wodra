using IndoorCycling.Core.Models;
using IndoorCycling.Core.Persistence;
using IndoorCycling.Persistence.Sqlite;
using System.Text.Json;

var temporaryRoot = Path.Combine(Path.GetTempPath(), "wodra-persistence-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temporaryRoot);
try
{
    await TestBrandRenameMigration(temporaryRoot);
    TestNewProductPaths(temporaryRoot);

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

static async Task TestBrandRenameMigration(string root)
{
    var legacy = Path.Combine(root, "legacy-GWift");
    var legacyProject = Path.Combine(root, "legacy-IndoorCycling.Game");
    var current = Path.Combine(root, "new-Wodra");
    var legacyFit = Path.Combine(root, "old-media", "gwift-ride.fit");
    var legacyScreenshot = Path.Combine(root, "old-media", "GWift", "ride.png");
    Directory.CreateDirectory(Path.Combine(legacy, "settings"));
    Directory.CreateDirectory(Path.Combine(legacy, "data"));
    Directory.CreateDirectory(Path.Combine(legacy, "logs"));
    Directory.CreateDirectory(Path.Combine(legacy, "screenshots"));
    Directory.CreateDirectory(Path.GetDirectoryName(legacyFit)!);
    Directory.CreateDirectory(Path.GetDirectoryName(legacyScreenshot)!);
    File.WriteAllText(Path.Combine(legacy, "settings", "settings.json"),
        "{\"DebugModeEnabled\":true,\"Rider\":{\"Name\":\"Existing Rider\",\"WeightKg\":77},\"Recording\":{\"OutputFolder\":\"%USERPROFILE%\\\\Documents\\\\gwift\"}}");
    File.WriteAllText(Path.Combine(legacy, "logs", "old.jsonl"), "old log");
    File.WriteAllText(Path.Combine(legacy, "screenshots", "old.png"), "old screenshot");
    Directory.CreateDirectory(Path.Combine(legacyProject, "logs"));
    File.WriteAllText(Path.Combine(legacyProject, "logs", "project-log.jsonl"), "project log");
    File.WriteAllText(legacyFit, "existing FIT bytes");
    File.WriteAllText(legacyScreenshot, "existing screenshot bytes");

    var legacyRepository = new SqliteRideHistoryRepository(Path.Combine(legacy, "data", "gwift.db"));
    await legacyRepository.InitializeAsync();
    var profile = await legacyRepository.EnsureRiderProfileAsync("Existing Rider");
    var rideId = Guid.NewGuid();
    var now = DateTimeOffset.UtcNow;
    var ride = new RideActivityDraft(rideId, profile.Id, "dunedin", "Dunedin", now,
        RideActivityStatus.Completed, 1200, 1000, 1, 30, 42, 240, 300, 80, 120, 33, legacyFit);
    await legacyRepository.SaveCompletedActivityAsync(ride,
        [new RideActivityMedia(Guid.NewGuid(), rideId, legacyScreenshot, null, DateTimeOffset.UtcNow, "Clean", null)]);

    var result = LegacyUserDataMigrator.Migrate(current, [legacy, legacyProject]);
    Assert(result.DatabaseMigrated, "legacy gwift.db migrated to wodra.db");
    Assert(File.Exists(Path.Combine(current, "data", "gwift.db")) == false, "legacy database uses the new filename");
    Assert(File.Exists(Path.Combine(legacy, "data", "gwift.db")), "legacy database remains intact");
    Assert(File.ReadAllText(Path.Combine(current, "settings", "settings.json")).Contains("Existing Rider"), "settings copied intact");
    Assert(File.Exists(Path.Combine(current, "logs", "old.jsonl")) && File.Exists(Path.Combine(current, "screenshots", "old.png")),
        "legacy logs and user-data screenshots copied");
    Assert(File.Exists(Path.Combine(current, "logs", "project-log.jsonl")), "former project-name user-data directory is detected");
    using (var migratedSettings = JsonDocument.Parse(File.ReadAllText(Path.Combine(current, "settings", "settings.json"))))
    {
        var settings = migratedSettings.RootElement;
        Assert(settings.GetProperty("DebugModeEnabled").GetBoolean()
            && settings.GetProperty("Rider").GetProperty("Name").GetString() == "Existing Rider"
            && settings.GetProperty("Rider").GetProperty("WeightKg").GetDouble() == 77,
            "user settings survive legacy migration");
    }

    var migrated = new SqliteRideHistoryRepository(Path.Combine(current, "data", "wodra.db"));
    await migrated.InitializeAsync();
    var migratedRide = await migrated.GetActivityAsync(rideId);
    var migratedMedia = await migrated.GetActivityMediaAsync(rideId);
    Assert(migratedRide?.FitFilePath == legacyFit && File.Exists(migratedRide.FitFilePath), "absolute legacy FIT path remains readable and unchanged");
    Assert(migratedMedia.Single().ImagePath == legacyScreenshot && File.Exists(migratedMedia.Single().ImagePath),
        "absolute legacy screenshot path remains readable and unchanged");
    var secondRun = LegacyUserDataMigrator.Migrate(current, [legacy, legacyProject]);
    Assert(secondRun.CopiedFiles.Count == 0 && !secondRun.DatabaseMigrated, "migration is idempotent");

    var populatedWodra = Path.Combine(root, "existing-Wodra");
    Directory.CreateDirectory(Path.Combine(populatedWodra, "settings"));
    File.WriteAllText(Path.Combine(populatedWodra, "settings", "settings.json"), "Wodra settings win");
    var existingRepository = new SqliteRideHistoryRepository(Path.Combine(populatedWodra, "data", "wodra.db"));
    await existingRepository.InitializeAsync();
    await existingRepository.EnsureRiderProfileAsync("Wodra Profile");
    var blockedCopy = LegacyUserDataMigrator.Migrate(populatedWodra, [legacy, legacyProject]);
    Assert(!blockedCopy.DatabaseMigrated && File.ReadAllText(Path.Combine(populatedWodra, "settings", "settings.json")) == "Wodra settings win",
        "existing Wodra data is never overwritten");
    var stillExisting = new SqliteRideHistoryRepository(Path.Combine(populatedWodra, "data", "wodra.db"));
    await stillExisting.InitializeAsync();
    Assert((await stillExisting.GetProfileAsync((await stillExisting.EnsureRiderProfileAsync("Ignore")).Id))?.Name == "Wodra Profile",
        "existing Wodra database remains readable");
}

static void TestNewProductPaths(string root)
{
    var profile = Path.Combine(root, "profile");
    var documents = Path.Combine(root, "Documents");
    var migratedSetting = ProductFilePaths.UpgradeLegacyFitOutputFolder(
        "%USERPROFILE%\\Documents\\gwift", profile, documents);
    Assert(migratedSetting == ProductFilePaths.DefaultFitOutputFolderSetting, "legacy default FIT directory upgrades to Wodra");
    Assert(ProductFilePaths.ResolveFitOutputFolder("", profile, documents) == Path.Combine(documents, "Wodra"),
        "fresh install FIT folder is Wodra");
    Assert(ProductFilePaths.ResolveFitOutputFolder(ProductFilePaths.DefaultFitOutputFolderSetting, profile, documents)
        == Path.Combine(profile, "Documents", "Wodra"), "configured FIT folder resolves under Documents/Wodra");
    Assert(ProductFilePaths.CreateFitFileName(new DateTime(2026, 10, 9, 14, 23, 15)) == "wodra-20261009-142315.fit",
        "new FIT filename uses lowercase Wodra brand");
}
