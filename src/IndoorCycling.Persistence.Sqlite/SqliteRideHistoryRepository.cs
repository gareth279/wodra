using System.Globalization;
using IndoorCycling.Core.Models;
using IndoorCycling.Core.Persistence;
using IndoorCycling.Core.Progress;
using Microsoft.Data.Sqlite;

namespace IndoorCycling.Persistence.Sqlite;

/// <summary>Small versioned SQLite store for local rider, activity, and media history.</summary>
public sealed class SqliteRideHistoryRepository : IRideHistoryRepository
{
    private readonly string _connectionString;
    public string DatabasePath { get; }

    public SqliteRideHistoryRepository(string databasePath)
    {
        DatabasePath = Path.GetFullPath(databasePath);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = DatabasePath, Mode = SqliteOpenMode.ReadWriteCreate, ForeignKeys = true }.ToString();
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);
        await using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
        await pragma.ExecuteNonQueryAsync(ct);
        return connection;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await OpenAsync(cancellationToken);
        await using (var info = db.CreateCommand())
        {
            info.CommandText = "CREATE TABLE IF NOT EXISTS schema_info (version INTEGER NOT NULL); INSERT INTO schema_info(version) SELECT 0 WHERE NOT EXISTS (SELECT 1 FROM schema_info);";
            await info.ExecuteNonQueryAsync(cancellationToken);
        }
        int version;
        await using (var query = db.CreateCommand())
        {
            query.CommandText = "SELECT version FROM schema_info LIMIT 1";
            version = Convert.ToInt32(await query.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        }
        if (version > 6) throw new InvalidOperationException($"Ride history schema {version} is newer than this application supports.");
        if (version == 0)
        {
            using var transaction = db.BeginTransaction();
            await using var create = db.CreateCommand();
            create.Transaction = transaction;
            create.CommandText = @"
CREATE TABLE rider_profiles (
 id TEXT PRIMARY KEY, name TEXT NOT NULL, created_utc TEXT NOT NULL, updated_utc TEXT NOT NULL);
CREATE TABLE ride_activities (
 id TEXT PRIMARY KEY, rider_profile_id TEXT NOT NULL REFERENCES rider_profiles(id), route_id TEXT NOT NULL, route_name TEXT NOT NULL,
 started_utc TEXT NOT NULL, completed_utc TEXT, created_utc TEXT NOT NULL, status TEXT NOT NULL, duration_seconds REAL NOT NULL, moving_seconds REAL, distance_meters REAL NOT NULL,
 completed_laps INTEGER NOT NULL, average_speed_kph REAL, max_speed_kph REAL, average_power_watts REAL, max_power_watts REAL,
 average_cadence_rpm REAL, max_cadence_rpm REAL, elevation_gain_meters REAL NOT NULL, fit_file_path TEXT);
CREATE INDEX ix_ride_activities_started_utc ON ride_activities(started_utc DESC);
CREATE INDEX ix_ride_activities_profile_started ON ride_activities(rider_profile_id, started_utc DESC);
CREATE INDEX ix_ride_activities_route_started ON ride_activities(route_id, started_utc DESC);
CREATE TABLE activity_media (
 id TEXT PRIMARY KEY, activity_id TEXT NOT NULL REFERENCES ride_activities(id) ON DELETE CASCADE,
 image_path TEXT NOT NULL, sidecar_path TEXT, captured_utc TEXT NOT NULL, media_type TEXT NOT NULL, capture_mode TEXT NOT NULL, marker_number INTEGER);
CREATE INDEX ix_activity_media_activity ON activity_media(activity_id, captured_utc);
UPDATE schema_info SET version=1;";
            await create.ExecuteNonQueryAsync(cancellationToken);
            transaction.Commit();
            version = 1;
        }
        if (version == 1)
        {
            await using var migrate = db.CreateCommand();
            migrate.CommandText = @"ALTER TABLE rider_profiles ADD COLUMN body_weight_kg REAL NOT NULL DEFAULT 75;
ALTER TABLE rider_profiles ADD COLUMN bike_weight_kg REAL NOT NULL DEFAULT 8;
ALTER TABLE rider_profiles ADD COLUMN is_test_profile INTEGER NOT NULL DEFAULT 0;
UPDATE schema_info SET version=2;";
            await migrate.ExecuteNonQueryAsync(cancellationToken);
            version = 2;
        }
        if (version == 2)
        {
            await using var migrate = db.CreateCommand();
            migrate.CommandText = @"ALTER TABLE rider_profiles ADD COLUMN trainer_difficulty_percent INTEGER NOT NULL DEFAULT 50;
UPDATE schema_info SET version=3;";
            await migrate.ExecuteNonQueryAsync(cancellationToken);
            version = 3;
        }
        if (version == 3)
        {
            await using var migrate = db.CreateCommand();
            migrate.CommandText = @"
CREATE TABLE achievement_activity_contributions (
 activity_id TEXT PRIMARY KEY REFERENCES ride_activities(id) ON DELETE CASCADE,
 rider_profile_id TEXT NOT NULL REFERENCES rider_profiles(id), route_id TEXT NOT NULL, route_name TEXT NOT NULL,
 environment_id TEXT NOT NULL, route_definition_version INTEGER NOT NULL, activity_mode_id TEXT NOT NULL,
 eligible_for_progress INTEGER NOT NULL, route_completion_eligible INTEGER NOT NULL, full_route_completed INTEGER NOT NULL,
 started_utc TEXT NOT NULL, distance_meters REAL NOT NULL, riding_seconds REAL NOT NULL, climbing_meters REAL NOT NULL);
CREATE INDEX ix_achievement_activity_profile_started ON achievement_activity_contributions(rider_profile_id, eligible_for_progress, started_utc);
CREATE INDEX ix_achievement_activity_profile_route ON achievement_activity_contributions(rider_profile_id, route_id, full_route_completed);
CREATE TABLE achievement_awards (
 rider_profile_id TEXT NOT NULL REFERENCES rider_profiles(id) ON DELETE CASCADE,
 achievement_id TEXT NOT NULL, earned_utc TEXT NOT NULL,
 activity_id TEXT REFERENCES ride_activities(id) ON DELETE SET NULL,
 PRIMARY KEY(rider_profile_id, achievement_id));
CREATE TABLE personal_goals (
 rider_profile_id TEXT PRIMARY KEY REFERENCES rider_profiles(id) ON DELETE CASCADE,
 metric TEXT NOT NULL, target_value REAL NOT NULL, created_utc TEXT NOT NULL);
UPDATE schema_info SET version=4;";
            await migrate.ExecuteNonQueryAsync(cancellationToken);
            version = 4;
        }
        if (version == 4)
        {
            await using var migrate = db.CreateCommand();
            migrate.CommandText = @"
CREATE TABLE IF NOT EXISTS route_effort_results (
 id TEXT PRIMARY KEY,
 activity_id TEXT NOT NULL REFERENCES ride_activities(id) ON DELETE CASCADE,
 rider_profile_id TEXT NOT NULL REFERENCES rider_profiles(id) ON DELETE CASCADE,
 route_id TEXT NOT NULL, route_name TEXT NOT NULL, geometry_fingerprint TEXT NOT NULL,
 route_definition_version INTEGER NOT NULL, direction TEXT NOT NULL,
 effort_id TEXT NOT NULL, effort_version INTEGER NOT NULL, effort_name TEXT NOT NULL, kind TEXT NOT NULL,
 attempt_index INTEGER NOT NULL, start_route_distance_meters REAL NOT NULL, end_route_distance_meters REAL NOT NULL,
 started_utc TEXT NOT NULL, elapsed_seconds REAL NOT NULL, completed INTEGER NOT NULL, eligible INTEGER NOT NULL,
 invalid_reason TEXT, fit_file_path TEXT,
 UNIQUE(activity_id,effort_id,effort_version,attempt_index));
CREATE INDEX IF NOT EXISTS ix_route_effort_lookup ON route_effort_results(
 rider_profile_id,route_id,geometry_fingerprint,direction,effort_id,eligible,started_utc DESC);
CREATE INDEX IF NOT EXISTS ix_route_effort_activity ON route_effort_results(activity_id,effort_id,attempt_index);
UPDATE schema_info SET version=5;";
            await migrate.ExecuteNonQueryAsync(cancellationToken);
            version = 5;
        }
        if (version == 5)
        {
            await using var migrate = db.CreateCommand();
            migrate.CommandText = @"ALTER TABLE ride_activities ADD COLUMN activity_kind TEXT NOT NULL DEFAULT 'Route';
ALTER TABLE ride_activities ADD COLUMN workout_definition_id TEXT;
ALTER TABLE ride_activities ADD COLUMN workout_name TEXT;
ALTER TABLE ride_activities ADD COLUMN workout_ftp_watts INTEGER;
ALTER TABLE ride_activities ADD COLUMN workout_completed INTEGER NOT NULL DEFAULT 0;
ALTER TABLE ride_activities ADD COLUMN workout_target_adherence_percent REAL;
UPDATE schema_info SET version=6;";
            await migrate.ExecuteNonQueryAsync(cancellationToken);
            version = 6;
        }
    }

    public async Task<RiderProfileRecord> EnsureRiderProfileAsync(string defaultName, CancellationToken cancellationToken = default)
    {
        await using var db = await OpenAsync(cancellationToken);
        await using var query = db.CreateCommand();
        query.CommandText = "SELECT id,name,created_utc,updated_utc,body_weight_kg,bike_weight_kg,is_test_profile,trainer_difficulty_percent FROM rider_profiles ORDER BY created_utc LIMIT 1";
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
            return new(Guid.Parse(reader.GetString(0)), reader.GetString(1), ParseTime(reader.GetString(2)), ParseTime(reader.GetString(3)), reader.GetDouble(4), reader.GetDouble(5), reader.GetInt32(6) != 0, reader.GetInt32(7));
        await reader.DisposeAsync();
        var now = DateTimeOffset.UtcNow;
        var profile = new RiderProfileRecord(Guid.NewGuid(), string.IsNullOrWhiteSpace(defaultName) ? "Rider" : defaultName.Trim(), now, now);
        await using var insert = db.CreateCommand();
        insert.CommandText = "INSERT INTO rider_profiles(id,name,created_utc,updated_utc,body_weight_kg,bike_weight_kg,is_test_profile,trainer_difficulty_percent) VALUES($id,$name,$created,$updated,75,8,0,50)";
        insert.Parameters.AddWithValue("$id", profile.Id.ToString("D"));
        insert.Parameters.AddWithValue("$name", profile.Name);
        insert.Parameters.AddWithValue("$created", Time(profile.CreatedAtUtc));
        insert.Parameters.AddWithValue("$updated", Time(profile.UpdatedAtUtc));
        await insert.ExecuteNonQueryAsync(cancellationToken);
        return profile;
    }

    public async Task<RiderProfileRecord?> GetProfileAsync(Guid profileId, CancellationToken cancellationToken = default)
    {
        await using var db = await OpenAsync(cancellationToken); await using var command = db.CreateCommand();
        command.CommandText = "SELECT id,name,created_utc,updated_utc,body_weight_kg,bike_weight_kg,is_test_profile,trainer_difficulty_percent FROM rider_profiles WHERE id=$id"; command.Parameters.AddWithValue("$id", profileId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? new(Guid.Parse(reader.GetString(0)), reader.GetString(1), ParseTime(reader.GetString(2)), ParseTime(reader.GetString(3)), reader.GetDouble(4), reader.GetDouble(5), reader.GetInt32(6) != 0, reader.GetInt32(7)) : null;
    }

    public async Task SaveProfileAsync(RiderProfileRecord profile, CancellationToken cancellationToken = default)
    {
        RiderProfileRecord.Validate(profile.Name, profile.BodyWeightKg, profile.BikeWeightKg, profile.TrainerDifficultyPercent);
        await using var db = await OpenAsync(cancellationToken); await using var command = db.CreateCommand();
        command.CommandText = "INSERT INTO rider_profiles(id,name,created_utc,updated_utc,body_weight_kg,bike_weight_kg,is_test_profile,trainer_difficulty_percent) VALUES($id,$name,$created,$updated,$body,$bike,$test,$difficulty) ON CONFLICT(id) DO UPDATE SET name=excluded.name,updated_utc=excluded.updated_utc,body_weight_kg=excluded.body_weight_kg,bike_weight_kg=excluded.bike_weight_kg,is_test_profile=excluded.is_test_profile,trainer_difficulty_percent=excluded.trainer_difficulty_percent";
        Add(command, "$id", profile.Id.ToString("D")); Add(command, "$name", profile.Name); Add(command, "$created", Time(profile.CreatedAtUtc)); Add(command, "$updated", Time(profile.UpdatedAtUtc));
        Add(command, "$body", profile.BodyWeightKg); Add(command, "$bike", profile.BikeWeightKg); Add(command, "$test", profile.IsTestProfile ? 1 : 0); Add(command, "$difficulty", profile.TrainerDifficultyPercent);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<RiderProfileRecord>> GetAllProfilesAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await OpenAsync(cancellationToken); await using var command = db.CreateCommand();
        command.CommandText = "SELECT id,name,created_utc,updated_utc,body_weight_kg,bike_weight_kg,is_test_profile,trainer_difficulty_percent FROM rider_profiles ORDER BY created_utc";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<RiderProfileRecord>();
        while (await reader.ReadAsync(cancellationToken)) result.Add(new(Guid.Parse(reader.GetString(0)), reader.GetString(1), ParseTime(reader.GetString(2)), ParseTime(reader.GetString(3)), reader.GetDouble(4), reader.GetDouble(5), reader.GetInt32(6) != 0, reader.GetInt32(7)));
        return result;
    }

    public async Task SaveCompletedActivityAsync(RideActivityDraft a, IReadOnlyCollection<RideActivityMedia> media, CancellationToken cancellationToken = default)
    {
        if (a.Status != RideActivityStatus.Completed) throw new ArgumentException("Only completed activities can be saved through this operation.", nameof(a));
        await using var db = await OpenAsync(cancellationToken);
        using var transaction = db.BeginTransaction();
        await using (var command = db.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = @"INSERT INTO ride_activities(id,rider_profile_id,route_id,route_name,started_utc,completed_utc,created_utc,status,duration_seconds,moving_seconds,distance_meters,completed_laps,
average_speed_kph,max_speed_kph,average_power_watts,max_power_watts,average_cadence_rpm,max_cadence_rpm,elevation_gain_meters,fit_file_path,activity_kind,workout_definition_id,workout_name,workout_ftp_watts,workout_completed,workout_target_adherence_percent)
VALUES($id,$profile,$route,$name,$started,$completed,$created,$status,$duration,NULL,$distance,$laps,$avgSpeed,$maxSpeed,$avgPower,$maxPower,$avgCadence,$maxCadence,$elevation,$fit,$kind,$workoutId,$workoutName,$ftp,$workoutCompleted,$adherence)";
            Add(command, "$id", a.Id.ToString("D")); Add(command, "$profile", a.RiderProfileId.ToString("D")); Add(command, "$route", a.RouteId); Add(command, "$name", a.RouteName);
            Add(command, "$started", Time(a.StartedAtUtc)); Add(command, "$completed", Time(DateTimeOffset.UtcNow)); Add(command, "$created", Time(DateTimeOffset.UtcNow)); Add(command, "$status", a.Status.ToString());
            Add(command, "$duration", a.DurationSeconds); Add(command, "$distance", a.DistanceMeters); Add(command, "$laps", a.CompletedLaps);
            Add(command, "$avgSpeed", a.AverageSpeedKph); Add(command, "$maxSpeed", a.MaxSpeedKph); Add(command, "$avgPower", a.AveragePowerWatts); Add(command, "$maxPower", a.MaxPowerWatts);
            Add(command, "$avgCadence", a.AverageCadenceRpm); Add(command, "$maxCadence", a.MaxCadenceRpm); Add(command, "$elevation", a.ElevationGainMeters); Add(command, "$fit", a.FitFilePath);
            AddWorkoutMetadata(command, a);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        foreach (var m in media.Where(m => m.ActivityId == a.Id))
        {
            await using var command = db.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO activity_media(id,activity_id,image_path,sidecar_path,captured_utc,media_type,capture_mode,marker_number) VALUES($id,$activity,$image,$sidecar,$captured,$type,$mode,$marker)";
            Add(command, "$id", m.Id.ToString("D")); Add(command, "$activity", a.Id.ToString("D")); Add(command, "$image", m.ImagePath);
            Add(command, "$sidecar", m.SidecarPath); Add(command, "$captured", Time(m.CapturedAtUtc)); Add(command, "$type", m.MediaType); Add(command, "$mode", m.CaptureMode); Add(command, "$marker", m.MarkerNumber);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        transaction.Commit();
    }

    public async Task<AchievementActivityResult> SaveCompletedActivityWithAchievementsAsync(RideActivityDraft a,
        IReadOnlyCollection<RideActivityMedia> media, AchievementRideEvidence evidence,
        IReadOnlyCollection<RouteEffortRecord>? routeEfforts = null,
        CancellationToken cancellationToken = default)
    {
        if (a.Status != RideActivityStatus.Completed)
            throw new ArgumentException("Only saved completed activities can contribute to progress.", nameof(a));
        if (string.IsNullOrWhiteSpace(evidence.EnvironmentId) || string.IsNullOrWhiteSpace(evidence.ActivityModeId) ||
            evidence.RouteDefinitionVersion < 1)
            throw new ArgumentException("Achievement evidence must identify its environment, activity mode and route version.", nameof(evidence));

        await using var db = await OpenAsync(cancellationToken);
        using var transaction = db.BeginTransaction();
        await using (var command = db.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = @"INSERT OR IGNORE INTO ride_activities(id,rider_profile_id,route_id,route_name,started_utc,completed_utc,created_utc,status,duration_seconds,moving_seconds,distance_meters,completed_laps,
average_speed_kph,max_speed_kph,average_power_watts,max_power_watts,average_cadence_rpm,max_cadence_rpm,elevation_gain_meters,fit_file_path,activity_kind,workout_definition_id,workout_name,workout_ftp_watts,workout_completed,workout_target_adherence_percent)
VALUES($id,$profile,$route,$name,$started,$completed,$created,$status,$duration,NULL,$distance,$laps,$avgSpeed,$maxSpeed,$avgPower,$maxPower,$avgCadence,$maxCadence,$elevation,$fit,$kind,$workoutId,$workoutName,$ftp,$workoutCompleted,$adherence)";
            Add(command, "$id", a.Id.ToString("D")); Add(command, "$profile", a.RiderProfileId.ToString("D")); Add(command, "$route", a.RouteId); Add(command, "$name", a.RouteName);
            Add(command, "$started", Time(a.StartedAtUtc)); Add(command, "$completed", Time(DateTimeOffset.UtcNow)); Add(command, "$created", Time(DateTimeOffset.UtcNow)); Add(command, "$status", a.Status.ToString());
            Add(command, "$duration", Math.Max(0, FiniteOrZero(a.DurationSeconds))); Add(command, "$distance", Math.Max(0, FiniteOrZero(a.DistanceMeters))); Add(command, "$laps", Math.Max(0, a.CompletedLaps));
            Add(command, "$avgSpeed", a.AverageSpeedKph); Add(command, "$maxSpeed", a.MaxSpeedKph); Add(command, "$avgPower", a.AveragePowerWatts); Add(command, "$maxPower", a.MaxPowerWatts);
            Add(command, "$avgCadence", a.AverageCadenceRpm); Add(command, "$maxCadence", a.MaxCadenceRpm); Add(command, "$elevation", Math.Max(0, FiniteOrZero(a.ElevationGainMeters))); Add(command, "$fit", a.FitFilePath);
            AddWorkoutMetadata(command, a);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var verify = db.CreateCommand())
        {
            verify.Transaction = transaction;
            verify.CommandText = "SELECT rider_profile_id,route_id FROM ride_activities WHERE id=$id";
            Add(verify, "$id", a.Id.ToString("D"));
            await using var reader = await verify.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken) || reader.GetString(0) != a.RiderProfileId.ToString("D") || reader.GetString(1) != a.RouteId)
                throw new InvalidOperationException("Saved activity ID already belongs to a different profile or route.");
        }

        foreach (var item in media.Where(item => item.ActivityId == a.Id))
        {
            await using var command = db.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "INSERT OR IGNORE INTO activity_media(id,activity_id,image_path,sidecar_path,captured_utc,media_type,capture_mode,marker_number) VALUES($id,$activity,$image,$sidecar,$captured,$type,$mode,$marker)";
            Add(command, "$id", item.Id.ToString("D")); Add(command, "$activity", a.Id.ToString("D")); Add(command, "$image", item.ImagePath);
            Add(command, "$sidecar", item.SidecarPath); Add(command, "$captured", Time(item.CapturedAtUtc)); Add(command, "$type", item.MediaType);
            Add(command, "$mode", item.CaptureMode); Add(command, "$marker", item.MarkerNumber);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var effort in routeEfforts ?? Array.Empty<RouteEffortRecord>())
        {
            ValidateEffortForActivity(effort, a);
            await using var command = db.CreateCommand(); command.Transaction = transaction;
            command.CommandText = @"INSERT OR IGNORE INTO route_effort_results(
id,activity_id,rider_profile_id,route_id,route_name,geometry_fingerprint,route_definition_version,direction,
effort_id,effort_version,effort_name,kind,attempt_index,start_route_distance_meters,end_route_distance_meters,
started_utc,elapsed_seconds,completed,eligible,invalid_reason,fit_file_path)
VALUES($id,$activity,$profile,$route,$routeName,$fingerprint,$routeVersion,$direction,
$effort,$effortVersion,$effortName,$kind,$attempt,$startDistance,$endDistance,
$started,$elapsed,$completed,$eligible,$reason,$fit)";
            Add(command, "$id", effort.Id.ToString("D")); Add(command, "$activity", a.Id.ToString("D"));
            Add(command, "$profile", a.RiderProfileId.ToString("D")); Add(command, "$route", effort.RouteId);
            Add(command, "$routeName", effort.RouteName); Add(command, "$fingerprint", effort.GeometryFingerprint);
            Add(command, "$routeVersion", effort.RouteDefinitionVersion); Add(command, "$direction", effort.Direction);
            Add(command, "$effort", effort.EffortId); Add(command, "$effortVersion", effort.EffortVersion);
            Add(command, "$effortName", effort.EffortName); Add(command, "$kind", effort.Kind.ToString());
            Add(command, "$attempt", effort.AttemptIndex); Add(command, "$startDistance", effort.StartRouteDistanceMeters);
            Add(command, "$endDistance", effort.EndRouteDistanceMeters); Add(command, "$started", Time(effort.StartedAtUtc));
            Add(command, "$elapsed", effort.ElapsedSeconds); Add(command, "$completed", effort.Completed ? 1 : 0);
            Add(command, "$eligible", effort.Eligible ? 1 : 0); Add(command, "$reason", effort.InvalidReason);
            Add(command, "$fit", effort.FitFilePath);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        var routeCompletionEligible = evidence.RouteCompletionEligible &&
            string.Equals(evidence.EnvironmentId, "Road", StringComparison.OrdinalIgnoreCase);
        await using (var contribution = db.CreateCommand())
        {
            contribution.Transaction = transaction;
            contribution.CommandText = @"INSERT OR IGNORE INTO achievement_activity_contributions(activity_id,rider_profile_id,route_id,route_name,environment_id,route_definition_version,activity_mode_id,
eligible_for_progress,route_completion_eligible,full_route_completed,started_utc,distance_meters,riding_seconds,climbing_meters)
SELECT $activity,$profile,$route,$name,$environment,$version,$mode,$eligible,$routeEligible,$full,$started,$distance,$time,$climbing
WHERE EXISTS(SELECT 1 FROM rider_profiles WHERE id=$profile)";
            Add(contribution, "$activity", a.Id.ToString("D")); Add(contribution, "$profile", a.RiderProfileId.ToString("D"));
            Add(contribution, "$route", a.RouteId); Add(contribution, "$name", a.RouteName); Add(contribution, "$environment", evidence.EnvironmentId);
            Add(contribution, "$version", evidence.RouteDefinitionVersion); Add(contribution, "$mode", evidence.ActivityModeId);
            Add(contribution, "$eligible", evidence.EligibleForProgress ? 1 : 0); Add(contribution, "$routeEligible", routeCompletionEligible ? 1 : 0);
            Add(contribution, "$full", routeCompletionEligible && evidence.FullRouteCompleted ? 1 : 0); Add(contribution, "$started", Time(a.StartedAtUtc));
            Add(contribution, "$distance", Math.Max(0, FiniteOrZero(a.DistanceMeters)));
            Add(contribution, "$time", Math.Max(0, FiniteOrZero(a.DurationSeconds)));
            // Elevation gain is authoritative route elevation; the visual velodrome bank has a zero-grade profile.
            Add(contribution, "$climbing", Math.Max(0, FiniteOrZero(a.ElevationGainMeters)));
            await contribution.ExecuteNonQueryAsync(cancellationToken);
        }
        bool inserted;
        await using (var changes = db.CreateCommand())
        {
            changes.Transaction = transaction; changes.CommandText = "SELECT changes()";
            inserted = Convert.ToInt64(await changes.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) == 1;
        }

        var newlyEarned = new List<AchievementDefinition>();
        if (inserted && evidence.EligibleForProgress)
        {
            var snapshot = await ReadAchievementProgressAsync(db, a.RiderProfileId, transaction, cancellationToken);
            var eligibleRoutes = await ReadRouteCompletionRowsAsync(db, a.RiderProfileId, transaction, completedOnly: false, cancellationToken);
            var completedRoutes = await ReadRouteCompletionRowsAsync(db, a.RiderProfileId, transaction, completedOnly: true, cancellationToken);
            var definitions = AchievementCatalog.Evaluate(snapshot.Totals,
                completedRoutes.Select(route => route.Id).ToArray(), eligibleRoutes.Select(route => route.Id).ToArray());
            var awardsById = snapshot.Awards.Select(award => award.AchievementId).ToHashSet(StringComparer.Ordinal);
            var now = DateTimeOffset.UtcNow;
            foreach (var definition in definitions.Where(item => !awardsById.Contains(item.Id)))
            {
                var displayDefinition = definition;
                if (definition.Metric == AchievementMetric.RouteCompletion &&
                    eligibleRoutes.FirstOrDefault(route => route.Id == definition.RouteId) is { } route)
                    displayDefinition = AchievementCatalog.RouteCompletion(route.Id, route.Name);
                await using var award = db.CreateCommand(); award.Transaction = transaction;
                award.CommandText = "INSERT OR IGNORE INTO achievement_awards(rider_profile_id,achievement_id,earned_utc,activity_id) VALUES($profile,$achievement,$earned,$activity)";
                Add(award, "$profile", a.RiderProfileId.ToString("D")); Add(award, "$achievement", displayDefinition.Id);
                Add(award, "$earned", Time(now)); Add(award, "$activity", a.Id.ToString("D"));
                if (await award.ExecuteNonQueryAsync(cancellationToken) == 1) newlyEarned.Add(displayDefinition);
            }
        }

        var finalSnapshot = await ReadAchievementProgressAsync(db, a.RiderProfileId, transaction, cancellationToken);
        transaction.Commit();
        return new AchievementActivityResult(newlyEarned, finalSnapshot);
    }

    public async Task<AchievementProgressSnapshot> GetAchievementProgressAsync(Guid profileId, CancellationToken cancellationToken = default)
    {
        await using var db = await OpenAsync(cancellationToken);
        return await ReadAchievementProgressAsync(db, profileId, null, cancellationToken);
    }

    public async Task SetPersonalGoalAsync(Guid profileId, PersonalGoalMetric metric, double targetValue,
        DateTimeOffset createdAtUtc, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(metric) || !double.IsFinite(targetValue) || targetValue <= 0 || targetValue > 100_000_000)
            throw new ArgumentOutOfRangeException(nameof(targetValue), "Goal target must be positive and within the supported range.");
        await using var db = await OpenAsync(cancellationToken); await using var command = db.CreateCommand();
        command.CommandText = @"INSERT INTO personal_goals(rider_profile_id,metric,target_value,created_utc) VALUES($profile,$metric,$target,$created)
ON CONFLICT(rider_profile_id) DO UPDATE SET metric=excluded.metric,target_value=excluded.target_value,created_utc=excluded.created_utc";
        Add(command, "$profile", profileId.ToString("D")); Add(command, "$metric", metric.ToString());
        Add(command, "$target", targetValue); Add(command, "$created", Time(createdAtUtc));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task ClearPersonalGoalAsync(Guid profileId, CancellationToken cancellationToken = default)
    {
        await using var db = await OpenAsync(cancellationToken); await using var command = db.CreateCommand();
        command.CommandText = "DELETE FROM personal_goals WHERE rider_profile_id=$profile";
        Add(command, "$profile", profileId.ToString("D")); await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<RouteEffortHistory> GetRouteEffortHistoryAsync(Guid profileId, string routeId,
        string geometryFingerprint, string direction, string effortId, int limit = 100,
        CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(limit));
        await using var db = await OpenAsync(cancellationToken);
        await using var command = db.CreateCommand();
        command.CommandText = @"SELECT id,activity_id,rider_profile_id,route_id,route_name,geometry_fingerprint,
route_definition_version,direction,effort_id,effort_version,effort_name,kind,attempt_index,
start_route_distance_meters,end_route_distance_meters,started_utc,elapsed_seconds,completed,eligible,invalid_reason,fit_file_path
FROM route_effort_results WHERE rider_profile_id=$profile AND route_id=$route AND geometry_fingerprint=$fingerprint
AND direction=$direction AND effort_id=$effort AND completed=1 AND eligible=1 AND (
 id IN (SELECT id FROM route_effort_results WHERE rider_profile_id=$profile AND route_id=$route AND geometry_fingerprint=$fingerprint
 AND direction=$direction AND effort_id=$effort AND completed=1 AND eligible=1 ORDER BY started_utc DESC,attempt_index DESC LIMIT $limit)
 OR id=(SELECT id FROM route_effort_results WHERE rider_profile_id=$profile AND route_id=$route AND geometry_fingerprint=$fingerprint
 AND direction=$direction AND effort_id=$effort AND completed=1 AND eligible=1 ORDER BY elapsed_seconds ASC,started_utc ASC LIMIT 1))
ORDER BY started_utc DESC,attempt_index DESC";
        Add(command, "$profile", profileId.ToString("D")); Add(command, "$route", routeId);
        Add(command, "$fingerprint", geometryFingerprint); Add(command, "$direction", direction);
        Add(command, "$effort", effortId); Add(command, "$limit", limit);
        var rows = new List<RouteEffortRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) rows.Add(ReadRouteEffortRecord(reader));
        return new RouteEffortHistory(rows);
    }

    public async Task<IReadOnlyList<RouteEffortRecord>> GetRouteEffortsForActivityAsync(Guid activityId,
        bool eligibleOnly = false, CancellationToken cancellationToken = default)
    {
        await using var db = await OpenAsync(cancellationToken);
        await using var command = db.CreateCommand();
        command.CommandText = @"SELECT id,activity_id,rider_profile_id,route_id,route_name,geometry_fingerprint,
route_definition_version,direction,effort_id,effort_version,effort_name,kind,attempt_index,
start_route_distance_meters,end_route_distance_meters,started_utc,elapsed_seconds,completed,eligible,invalid_reason,fit_file_path
FROM route_effort_results WHERE activity_id=$activity AND completed=1" + (eligibleOnly ? " AND eligible=1" : "") +
" ORDER BY start_route_distance_meters,effort_id,attempt_index";
        Add(command, "$activity", activityId.ToString("D"));
        var rows = new List<RouteEffortRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) rows.Add(ReadRouteEffortRecord(reader));
        return rows;
    }

    private static RouteEffortRecord ReadRouteEffortRecord(SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), Guid.Parse(reader.GetString(2)),
        reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetInt32(6), reader.GetString(7),
        reader.GetString(8), reader.GetInt32(9), reader.GetString(10), Enum.Parse<RouteEffortKind>(reader.GetString(11)),
        reader.GetInt32(12), reader.GetDouble(13), reader.GetDouble(14), ParseTime(reader.GetString(15)),
        reader.GetDouble(16), reader.GetInt32(17) != 0, reader.GetInt32(18) != 0,
        reader.IsDBNull(19) ? null : reader.GetString(19), reader.IsDBNull(20) ? null : reader.GetString(20));

    private static void ValidateEffortForActivity(RouteEffortRecord effort, RideActivityDraft activity)
    {
        if (effort.ActivityId != activity.Id || effort.RiderProfileId != activity.RiderProfileId ||
            !string.Equals(effort.RouteId, activity.RouteId, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(effort.RouteName) || string.IsNullOrWhiteSpace(effort.GeometryFingerprint) ||
            effort.GeometryFingerprint.Length != 64 || !effort.GeometryFingerprint.All(Uri.IsHexDigit) ||
            effort.RouteDefinitionVersion < 1 || effort.Direction is not ("forward" or "reverse") ||
            string.IsNullOrWhiteSpace(effort.EffortId) || effort.EffortVersion < 1 ||
            string.IsNullOrWhiteSpace(effort.EffortName) || !Enum.IsDefined(effort.Kind) || effort.AttemptIndex < 0 ||
            !double.IsFinite(effort.StartRouteDistanceMeters) || !double.IsFinite(effort.EndRouteDistanceMeters) ||
            effort.StartRouteDistanceMeters < 0 || effort.EndRouteDistanceMeters <= effort.StartRouteDistanceMeters ||
            !double.IsFinite(effort.ElapsedSeconds) || effort.ElapsedSeconds <= 0 || !effort.Completed)
            throw new ArgumentException("Route effort result does not match its completed ride or contains invalid timing metadata.", nameof(effort));
    }

    private static double FiniteOrZero(double value) => double.IsFinite(value) ? value : 0;

    private static async Task<AchievementProgressSnapshot> ReadAchievementProgressAsync(SqliteConnection db,
        Guid profileId, SqliteTransaction? transaction, CancellationToken cancellationToken)
    {
        AchievementTotals totals;
        await using (var command = db.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = @"SELECT COALESCE(SUM(distance_meters),0),COALESCE(SUM(riding_seconds),0),COALESCE(SUM(climbing_meters),0)
FROM achievement_activity_contributions WHERE rider_profile_id=$profile AND eligible_for_progress=1";
            Add(command, "$profile", profileId.ToString("D")); await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken); totals = new(reader.GetDouble(0), reader.GetDouble(1), reader.GetDouble(2));
        }
        var awards = new List<AchievementAwardRecord>();
        await using (var command = db.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT achievement_id,earned_utc,activity_id FROM achievement_awards WHERE rider_profile_id=$profile ORDER BY earned_utc,achievement_id";
            Add(command, "$profile", profileId.ToString("D")); await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) awards.Add(new(reader.GetString(0), ParseTime(reader.GetString(1)), reader.IsDBNull(2) ? null : Guid.Parse(reader.GetString(2))));
        }

        PersonalGoalRecord? goal = null;
        await using (var command = db.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT metric,target_value,created_utc FROM personal_goals WHERE rider_profile_id=$profile";
            Add(command, "$profile", profileId.ToString("D")); await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                var metric = Enum.Parse<PersonalGoalMetric>(reader.GetString(0)); var target = reader.GetDouble(1); var created = ParseTime(reader.GetString(2));
                await reader.DisposeAsync();
                await using var progress = db.CreateCommand(); progress.Transaction = transaction;
                var column = metric == PersonalGoalMetric.DistanceMeters ? "distance_meters" : "climbing_meters";
                progress.CommandText = $"SELECT COALESCE(SUM({column}),0) FROM achievement_activity_contributions WHERE rider_profile_id=$profile AND eligible_for_progress=1 AND started_utc >= $created";
                Add(progress, "$profile", profileId.ToString("D")); Add(progress, "$created", Time(created));
                var amount = Convert.ToDouble(await progress.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
                goal = new(profileId, metric, target, created, amount);
            }
        }
        return new AchievementProgressSnapshot(profileId, totals, awards, goal);
    }

    private static async Task<IReadOnlyList<(string Id, string Name)>> ReadRouteCompletionRowsAsync(SqliteConnection db,
        Guid profileId, SqliteTransaction transaction, bool completedOnly, CancellationToken cancellationToken)
    {
        await using var command = db.CreateCommand(); command.Transaction = transaction;
        command.CommandText = $@"SELECT route_id,MIN(route_name) FROM achievement_activity_contributions
WHERE rider_profile_id=$profile AND eligible_for_progress=1 AND environment_id='Road' AND route_completion_eligible=1 {(completedOnly ? "AND full_route_completed=1" : "")}
GROUP BY route_id ORDER BY route_id";
        Add(command, "$profile", profileId.ToString("D")); await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<(string Id, string Name)>(); while (await reader.ReadAsync(cancellationToken)) rows.Add((reader.GetString(0), reader.GetString(1)));
        return rows;
    }

    public async Task<IReadOnlyList<RideActivity>> GetRecentActivitiesAsync(int limit = 50, CancellationToken cancellationToken = default)
    {
        await using var db = await OpenAsync(cancellationToken);
        await using var command = db.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM ride_activities ORDER BY started_utc DESC LIMIT $limit";
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 500));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<RideActivity>(); while (await reader.ReadAsync(cancellationToken)) result.Add(ReadActivity(reader));
        return result;
    }

    public async Task<IReadOnlyList<RideActivity>> GetRecentActivitiesForProfileAsync(Guid profileId, int limit = 50, CancellationToken cancellationToken = default)
    {
        await using var db = await OpenAsync(cancellationToken); await using var command = db.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM ride_activities WHERE rider_profile_id=$profile ORDER BY started_utc DESC LIMIT $limit";
        command.Parameters.AddWithValue("$profile", profileId.ToString("D")); command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 500));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<RideActivity>(); while (await reader.ReadAsync(cancellationToken)) result.Add(ReadActivity(reader));
        return result;
    }

    public async Task<RideHistoryPage> GetHistoryPageAsync(Guid profileId, string? search, string? routeId, DateTimeOffset? fromUtc, DateTimeOffset? toUtcExclusive, RideHistorySort sort, int offset, int pageSize, CancellationToken cancellationToken = default)
    {
        await using var db = await OpenAsync(cancellationToken);
        var where = "rider_profile_id=$profile AND status='Completed'" +
            (string.IsNullOrWhiteSpace(search) ? "" : " AND route_name LIKE $search COLLATE NOCASE") +
            (string.IsNullOrWhiteSpace(routeId) ? "" : " AND route_id=$route") +
            (fromUtc is null ? "" : " AND started_utc >= $from") + (toUtcExclusive is null ? "" : " AND started_utc < $to");
        await using var count = db.CreateCommand(); count.CommandText = $"SELECT COUNT(*) FROM ride_activities WHERE {where}";
        Bind(count, profileId, search, routeId, fromUtc, toUtcExclusive);
        var total = Convert.ToInt32(await count.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        var order = sort switch { RideHistorySort.Oldest => "started_utc ASC", RideHistorySort.Distance => "distance_meters DESC,started_utc DESC", RideHistorySort.Duration => "duration_seconds DESC,started_utc DESC", RideHistorySort.Elevation => "elevation_gain_meters DESC,started_utc DESC", _ => "started_utc DESC" };
        await using var query = db.CreateCommand(); query.CommandText = $"SELECT {Columns} FROM ride_activities WHERE {where} ORDER BY {order} LIMIT $limit OFFSET $offset";
        Bind(query, profileId, search, routeId, fromUtc, toUtcExclusive); query.Parameters.AddWithValue("$limit", Math.Clamp(pageSize, 1, 100)); query.Parameters.AddWithValue("$offset", Math.Max(0, offset));
        await using var reader = await query.ExecuteReaderAsync(cancellationToken); var activities = new List<RideActivity>();
        while (await reader.ReadAsync(cancellationToken)) activities.Add(ReadActivity(reader));
        return new(activities, total);
    }

    public async Task<IReadOnlyList<RideHistoryBucket>> GetHistoryTrendAsync(Guid profileId, DateTimeOffset fromUtc, DateTimeOffset toUtcExclusive, bool monthly, string? search = null, string? routeId = null, CancellationToken cancellationToken = default)
    {
        // Buckets are grouped against local calendar keys supplied in timestamps; callers choose consistent local boundaries.
        await using var db = await OpenAsync(cancellationToken); await using var command = db.CreateCommand();
        command.CommandText = $"SELECT started_utc,distance_meters,duration_seconds,elevation_gain_meters FROM ride_activities WHERE rider_profile_id=$profile AND status='Completed' AND started_utc >= $from AND started_utc < $to" +
            (string.IsNullOrWhiteSpace(search) ? "" : " AND route_name LIKE $search COLLATE NOCASE") + (string.IsNullOrWhiteSpace(routeId) ? "" : " AND route_id=$route") + " ORDER BY started_utc";
        command.Parameters.AddWithValue("$profile", profileId.ToString("D")); command.Parameters.AddWithValue("$from", Time(fromUtc)); command.Parameters.AddWithValue("$to", Time(toUtcExclusive));
        if (!string.IsNullOrWhiteSpace(search)) command.Parameters.AddWithValue("$search", "%" + search.Trim() + "%");
        if (!string.IsNullOrWhiteSpace(routeId)) command.Parameters.AddWithValue("$route", routeId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var grouped = new SortedDictionary<DateTime, (int Count, double Distance, double Duration, double Elevation)>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var local = ParseTime(reader.GetString(0)).ToLocalTime().Date;
            var start = monthly ? new DateTime(local.Year, local.Month, 1) : local.AddDays(-(((int)local.DayOfWeek + 6) % 7));
            var current = grouped.GetValueOrDefault(start); grouped[start] = (current.Count + 1, current.Distance + reader.GetDouble(1), current.Duration + reader.GetDouble(2), current.Elevation + reader.GetDouble(3));
        }
        var cursor = fromUtc.ToLocalTime().Date;
        if (monthly) cursor = new DateTime(cursor.Year, cursor.Month, 1);
        else cursor = cursor.AddDays(-(((int)cursor.DayOfWeek + 6) % 7));
        var endLocal = toUtcExclusive.ToLocalTime().Date;
        var buckets = new List<RideHistoryBucket>();
        while (cursor < endLocal)
        {
            var value = grouped.GetValueOrDefault(cursor);
            buckets.Add(new RideHistoryBucket(new DateTimeOffset(cursor, TimeZoneInfo.Local.GetUtcOffset(cursor)).ToUniversalTime(), value.Count, value.Distance, value.Duration, value.Elevation));
            cursor = monthly ? cursor.AddMonths(1) : cursor.AddDays(7);
        }
        return buckets;
    }

    public async Task<IReadOnlyList<(string RouteId, string RouteName)>> GetHistoryRoutesAsync(Guid profileId, CancellationToken cancellationToken = default)
    {
        await using var db = await OpenAsync(cancellationToken); await using var command = db.CreateCommand();
        command.CommandText = "SELECT route_id,MIN(route_name) FROM ride_activities WHERE rider_profile_id=$profile AND status='Completed' GROUP BY route_id ORDER BY MIN(route_name) COLLATE NOCASE";
        command.Parameters.AddWithValue("$profile", profileId.ToString("D")); await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<(string, string)>(); while (await reader.ReadAsync(cancellationToken)) result.Add((reader.GetString(0), reader.GetString(1))); return result;
    }

    private static void Bind(SqliteCommand command, Guid profileId, string? search, string? routeId, DateTimeOffset? fromUtc, DateTimeOffset? toUtc)
    {
        command.Parameters.AddWithValue("$profile", profileId.ToString("D"));
        if (!string.IsNullOrWhiteSpace(search)) command.Parameters.AddWithValue("$search", "%" + search.Trim() + "%");
        if (!string.IsNullOrWhiteSpace(routeId)) command.Parameters.AddWithValue("$route", routeId);
        if (fromUtc is not null) command.Parameters.AddWithValue("$from", Time(fromUtc.Value));
        if (toUtc is not null) command.Parameters.AddWithValue("$to", Time(toUtc.Value));
    }

    public async Task<RideActivity?> GetActivityAsync(Guid activityId, CancellationToken cancellationToken = default)
    {
        await using var db = await OpenAsync(cancellationToken); await using var command = db.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM ride_activities WHERE id=$id"; command.Parameters.AddWithValue("$id", activityId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadActivity(reader) : null;
    }

    public async Task<IReadOnlyList<RideActivity>> GetActivitiesForRouteAsync(string routeId, int limit = 50, CancellationToken cancellationToken = default)
    {
        await using var db = await OpenAsync(cancellationToken); await using var command = db.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM ride_activities WHERE route_id=$route ORDER BY started_utc DESC LIMIT $limit";
        command.Parameters.AddWithValue("$route", routeId); command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 500));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<RideActivity>(); while (await reader.ReadAsync(cancellationToken)) result.Add(ReadActivity(reader));
        return result;
    }

    public async Task<IReadOnlyList<RideActivityMedia>> GetActivityMediaAsync(Guid activityId, CancellationToken cancellationToken = default)
    {
        await using var db = await OpenAsync(cancellationToken); await using var command = db.CreateCommand();
        command.CommandText = "SELECT id,activity_id,image_path,sidecar_path,captured_utc,capture_mode,marker_number,media_type FROM activity_media WHERE activity_id=$id ORDER BY captured_utc";
        command.Parameters.AddWithValue("$id", activityId.ToString("D")); await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<RideActivityMedia>();
        while (await reader.ReadAsync(cancellationToken)) result.Add(new(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), reader.GetString(2), NullableString(reader,3), ParseTime(reader.GetString(4)), reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetInt32(6), reader.GetString(7)));
        return result;
    }

    private const string Columns = "id,rider_profile_id,route_id,route_name,started_utc,completed_utc,created_utc,status,duration_seconds,moving_seconds,distance_meters,completed_laps,average_speed_kph,max_speed_kph,average_power_watts,max_power_watts,average_cadence_rpm,max_cadence_rpm,elevation_gain_meters,fit_file_path,activity_kind,workout_definition_id,workout_name,workout_ftp_watts,workout_completed,workout_target_adherence_percent";
    private static RideActivity ReadActivity(SqliteDataReader r) => new(Guid.Parse(r.GetString(0)), Guid.Parse(r.GetString(1)), r.GetString(2), r.GetString(3), ParseTime(r.GetString(4)), r.IsDBNull(5) ? null : ParseTime(r.GetString(5)), Enum.Parse<RideActivityStatus>(r.GetString(7)), r.GetDouble(8), N(r,9), r.GetDouble(10), r.GetInt32(11), N(r,12), N(r,13), N(r,14), N(r,15), N(r,16), N(r,17), r.GetDouble(18), NullableString(r,19), ParseTime(r.GetString(6)))
    { Kind = Enum.Parse<RideActivityKind>(r.GetString(20)), WorkoutDefinitionId = NullableString(r,21), WorkoutName = NullableString(r,22), WorkoutFtpWatts = r.IsDBNull(23) ? null : r.GetInt32(23), WorkoutCompleted = r.GetInt32(24) != 0, WorkoutTargetAdherencePercent = N(r,25) };

    private static void AddWorkoutMetadata(SqliteCommand command, RideActivityDraft activity)
    {
        Add(command, "$kind", activity.Kind.ToString()); Add(command, "$workoutId", activity.WorkoutDefinitionId);
        Add(command, "$workoutName", activity.WorkoutName); Add(command, "$ftp", activity.WorkoutFtpWatts);
        Add(command, "$workoutCompleted", activity.WorkoutCompleted ? 1 : 0);
        Add(command, "$adherence", activity.WorkoutTargetAdherencePercent);
    }
    private static double? N(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetDouble(i);
    private static string? NullableString(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    private static DateTimeOffset ParseTime(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    private static string Time(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static void Add(SqliteCommand command, string name, object? value) => command.Parameters.AddWithValue(name, value ?? DBNull.Value);
}
