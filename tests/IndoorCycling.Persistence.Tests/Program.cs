using IndoorCycling.Core.Models;
using IndoorCycling.Core.Persistence;
using IndoorCycling.Persistence.Sqlite;
using System.Text.Json;

var temporaryRoot = Path.Combine(Path.GetTempPath(), "wodra-persistence-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temporaryRoot);
try
{
    TestRiderProfileStartupSelection();
    await TestBrandRenameMigration(temporaryRoot);
    await TestProfileSchemaUpgrade(temporaryRoot);
    TestNewProductPaths(temporaryRoot);

    var repository = new SqliteRideHistoryRepository(Path.Combine(temporaryRoot, "history.sqlite3"));
    await repository.InitializeAsync();
    var rider = await repository.EnsureRiderProfileAsync("Test Rider");
    Assert(rider.Name == "Test Rider", "first-run profile name");
    Assert((await repository.EnsureRiderProfileAsync("Ignored")).Id == rider.Id, "profile is stable across launches");
    Assert((await repository.GetProfileAsync(rider.Id))?.Name == "Test Rider", "profile roundtrip");
    Assert(rider.TrainerDifficultyPercent == 50, "new profiles default trainer difficulty to 50 percent");
    var renamed = rider with { Name = "Updated Rider", UpdatedAtUtc = DateTimeOffset.UtcNow, TrainerDifficultyPercent = 75 };
    await repository.SaveProfileAsync(renamed);
    Assert((await repository.GetProfileAsync(rider.Id)) is { Name: "Updated Rider", TrainerDifficultyPercent: 75 }, "profile save retains per-profile trainer difficulty");
    var dev = new RiderProfileRecord(Guid.NewGuid(), "Dev Rider", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 75, 8, true);
    await repository.SaveProfileAsync(dev);
    Assert((await repository.GetAllProfilesAsync()).Count == 2 && (await repository.GetProfileAsync(dev.Id))?.IsTestProfile == true,
        "multiple local profiles roundtrip with test metadata");

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
    var devActivity = second with { Id = Guid.NewGuid(), RiderProfileId = dev.Id, RouteId = "route-a", RouteName = "Route A" };
    await repository.SaveCompletedActivityAsync(devActivity, []);
    Assert((await repository.GetRecentActivitiesAsync()).Count == 3, "recent activities include rides from every profile");
    Assert((await repository.GetActivitiesForRouteAsync("route-a")).Count == 2, "route-filtered rides");
    Assert((await repository.GetRecentActivitiesForProfileAsync(rider.Id)).Select(a => a.Id).SequenceEqual([secondId, id]), "history is scoped by profile id");
    Assert((await repository.GetRecentActivitiesForProfileAsync(dev.Id)).Single().Id == devActivity.Id, "test profile history stays separate");
    Assert(id != secondId, "activity ids are unique");

    var baseDate = new DateTimeOffset(DateTime.Now.Date.AddDays(-15), TimeZoneInfo.Local.GetUtcOffset(DateTime.Now.Date.AddDays(-15))).ToUniversalTime();
    for (var i = 0; i < 125; i++)
    {
        var fixture = second with { Id = Guid.NewGuid(), RiderProfileId = rider.Id, RouteId = i % 2 == 0 ? "route-a" : "route-b", RouteName = i % 2 == 0 ? "Route A" : "Route B", StartedAtUtc = baseDate.AddHours(i), DistanceMeters = 1000 + i, DurationSeconds = 600 + i, ElevationGainMeters = i };
        await repository.SaveCompletedActivityAsync(fixture, []);
    }
    var newestPage = await repository.GetHistoryPageAsync(rider.Id, null, null, null, null, RideHistorySort.Newest, 100, 25);
    Assert(newestPage.TotalCount == 127 && newestPage.Activities.Count == 25 && (await repository.GetHistoryPageAsync(rider.Id, null, null, null, null, RideHistorySort.Newest, 125, 25)).Activities.Count == 2, "history pagination reaches records beyond the previous 100 ride limit");
    var filtered = await repository.GetHistoryPageAsync(rider.Id, "Route A", "route-a", null, null, RideHistorySort.Distance, 0, 25);
    Assert(filtered.TotalCount > 50 && filtered.Activities[0].DistanceMeters >= filtered.Activities[^1].DistanceMeters, "route and name search with distance sorting");
    var dateFiltered = await repository.GetHistoryPageAsync(rider.Id, null, null, baseDate, baseDate.AddDays(1), RideHistorySort.Oldest, 0, 200);
    Assert(dateFiltered.Activities.All(a => a.StartedAtUtc >= baseDate && a.StartedAtUtc < baseDate.AddDays(1)), "history date range uses inclusive start and exclusive end");
    var trend = await repository.GetHistoryTrendAsync(rider.Id, baseDate.AddDays(-7), baseDate.AddDays(28), false);
    Assert(trend.Count is >= 5 and <= 6 && trend.Sum(b => b.RideCount) >= 120, "calendar bucket totals include every matching activity independently of page size");
    Assert((await repository.GetHistoryPageAsync(dev.Id, null, null, null, null, RideHistorySort.Newest, 0, 200)).TotalCount == 1, "history queries preserve profile isolation for explicit test profiles");
    var boundaryRepository = new SqliteRideHistoryRepository(Path.Combine(temporaryRoot, "history-boundaries.sqlite3")); await boundaryRepository.InitializeAsync();
    var boundaryProfile = await boundaryRepository.EnsureRiderProfileAsync("Boundary Rider");
    var localStart = new DateTime(DateTime.Now.Year, DateTime.Now.Month, 1);
    var utcStart = new DateTimeOffset(localStart, TimeZoneInfo.Local.GetUtcOffset(localStart)).ToUniversalTime();
    var utcEnd = new DateTimeOffset(localStart.AddMonths(1), TimeZoneInfo.Local.GetUtcOffset(localStart.AddMonths(1))).ToUniversalTime();
    var atStart = first with { Id = Guid.NewGuid(), RiderProfileId = boundaryProfile.Id, StartedAtUtc = utcStart, AveragePowerWatts = null, MaxPowerWatts = null, AverageSpeedKph = null, MaxSpeedKph = null, AverageCadenceRpm = null, MaxCadenceRpm = null };
    var atEnd = atStart with { Id = Guid.NewGuid(), StartedAtUtc = utcEnd };
    await boundaryRepository.SaveCompletedActivityAsync(atStart, []); await boundaryRepository.SaveCompletedActivityAsync(atEnd, []);
    var exactMonth = await boundaryRepository.GetHistoryPageAsync(boundaryProfile.Id, null, null, utcStart, utcEnd, RideHistorySort.Oldest, 0, 25);
    Assert(exactMonth.TotalCount == 1 && exactMonth.Activities.Single().Id == atStart.Id && exactMonth.Activities.Single().AveragePowerWatts is null,
        "local calendar range includes its start, excludes the next boundary, and preserves unavailable metrics");
    var restarted = new SqliteRideHistoryRepository(boundaryRepository.DatabasePath); await restarted.InitializeAsync();
    Assert((await restarted.GetActivityAsync(atStart.Id))?.Id == atStart.Id, "history query data persists after repository restart");

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
        Assert(Convert.ToInt32(await command.ExecuteScalarAsync()) == 3, "0 to 3 profile schema migration");
    }
    RiderProfileRecord.Validate("Valid", 75, 8);
    foreach (var weights in new[] { (29d, 8d), (201d, 8d), (75d, 3d), (75d, 31d), (75d, double.NaN) })
    {
        try { RiderProfileRecord.Validate("Invalid", weights.Item1, weights.Item2); throw new Exception("invalid rider or bike mass accepted"); }
        catch (ArgumentOutOfRangeException) { }
    }
    foreach (var difficulty in new[] { -5, 105, 52 })
    {
        try { RiderProfileRecord.Validate("Invalid", 75, 8, difficulty); throw new Exception("invalid trainer difficulty accepted"); }
        catch (ArgumentOutOfRangeException) { }
    }
    Console.WriteLine("PASS SQLite migration/profile/activity/media/recent ordering, history filtering/pagination/calendar totals and accumulator tests");
}
finally { try { Directory.Delete(temporaryRoot, true); } catch { } }

static void Assert(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException("FAIL " + name);
}

static void TestRiderProfileStartupSelection()
{
    var now = DateTimeOffset.UtcNow;
    var first = new RiderProfileRecord(Guid.NewGuid(), "First", now, now, 75, 8, false);
    var second = new RiderProfileRecord(Guid.NewGuid(), "Second", now, now, 80, 9, false);

    var active = RiderProfileStartupSelection.Resolve([first], first.Id);
    Assert(active.ActiveProfile?.Id == first.Id && !active.OpenCreateProfile,
        "existing active profile selected and editor stays closed");

    var selectedSecond = RiderProfileStartupSelection.Resolve([first, second], second.Id);
    Assert(selectedSecond.ActiveProfile?.Id == second.Id && !selectedSecond.OpenCreateProfile,
        "valid active profile selected from multiple profiles");

    var invalidActive = RiderProfileStartupSelection.Resolve([first, second], Guid.NewGuid());
    var missingActive = RiderProfileStartupSelection.Resolve([first, second], null);
    Assert(invalidActive.ActiveProfile?.Id == first.Id && !invalidActive.OpenCreateProfile
        && missingActive.ActiveProfile?.Id == first.Id && !missingActive.OpenCreateProfile,
        "missing or invalid active profile falls back to first existing profile");
    var sueOnly = RiderProfileStartupSelection.Resolve([new RiderProfileRecord(Guid.NewGuid(), "Sue", now, now)], Guid.NewGuid());
    Assert(sueOnly.ActiveProfile?.Name == "Sue", "single Sue profile remains selected when no matching or Gareth profile exists");

    var empty = RiderProfileStartupSelection.Resolve([], null);
    Assert(empty.ActiveProfile is null && empty.OpenCreateProfile,
        "zero profiles requests initial new-profile editor");
    Assert(RiderProfileStartupSelection.ShouldAutoOpenCreateEditor(0, isInitialProfileLoad: true)
        && !RiderProfileStartupSelection.ShouldAutoOpenCreateEditor(0, isInitialProfileLoad: false)
        && !RiderProfileStartupSelection.ShouldAutoOpenCreateEditor(2, isInitialProfileLoad: true),
        "only initial empty profile load auto-opens editor");

    Assert(RiderProfileStartupSelection.ShouldActivateProfileAfterSave(isNewProfile: true, currentActiveProfileId: null)
        && RiderProfileStartupSelection.ShouldActivateProfileAfterSave(isNewProfile: true, currentActiveProfileId: first.Id),
        "saving a new first profile activates it");
    Assert(!RiderProfileStartupSelection.ShouldActivateProfileAfterSave(isNewProfile: false, currentActiveProfileId: first.Id),
        "saving edits does not change active profile");
    Console.WriteLine("PASS rider profile startup selection tests");
}

static async Task TestProfileSchemaUpgrade(string root)
{
    var databasePath = Path.Combine(root, "profile-v1.sqlite3");
    var profileId = Guid.NewGuid(); var activityId = Guid.NewGuid(); var now = DateTimeOffset.UtcNow.ToString("O");
    await using (var db = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={databasePath}"))
    {
        await db.OpenAsync(); await using var command = db.CreateCommand();
        command.CommandText = @"CREATE TABLE schema_info(version INTEGER NOT NULL); INSERT INTO schema_info VALUES(1);
CREATE TABLE rider_profiles(id TEXT PRIMARY KEY,name TEXT NOT NULL,created_utc TEXT NOT NULL,updated_utc TEXT NOT NULL);
INSERT INTO rider_profiles VALUES($profile,'Legacy Rider',$now,$now);
CREATE TABLE ride_activities(id TEXT PRIMARY KEY,rider_profile_id TEXT NOT NULL,route_id TEXT NOT NULL,route_name TEXT NOT NULL,started_utc TEXT NOT NULL,completed_utc TEXT,created_utc TEXT NOT NULL,status TEXT NOT NULL,duration_seconds REAL NOT NULL,moving_seconds REAL,distance_meters REAL NOT NULL,completed_laps INTEGER NOT NULL,average_speed_kph REAL,max_speed_kph REAL,average_power_watts REAL,max_power_watts REAL,average_cadence_rpm REAL,max_cadence_rpm REAL,elevation_gain_meters REAL NOT NULL,fit_file_path TEXT);
INSERT INTO ride_activities VALUES($activity,$profile,'route','Route',$now,$now,$now,'Completed',60,NULL,100,0,10,12,150,180,80,90,3,NULL);";
        command.Parameters.AddWithValue("$profile", profileId.ToString("D")); command.Parameters.AddWithValue("$activity", activityId.ToString("D")); command.Parameters.AddWithValue("$now", now);
        await command.ExecuteNonQueryAsync();
    }
    var repository = new SqliteRideHistoryRepository(databasePath); await repository.InitializeAsync();
    var profile = await repository.GetProfileAsync(profileId); var ride = await repository.GetActivityAsync(activityId);
    Assert(profile is { Id: var id, Name: "Legacy Rider", BodyWeightKg: 75, BikeWeightKg: 8, TrainerDifficultyPercent: 50 } && id == profileId,
        "v1 profile row is retained with conservative weight defaults");
    Assert(ride is { Id: var savedId, RiderProfileId: var owner } && savedId == activityId && owner == profileId,
        "existing ride and ProfileId survive profile schema upgrade");
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
