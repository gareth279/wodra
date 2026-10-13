using System.Globalization;
using IndoorCycling.Core.Models;
using IndoorCycling.Core.Persistence;
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
        if (version > 3) throw new InvalidOperationException($"Ride history schema {version} is newer than this application supports.");
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
average_speed_kph,max_speed_kph,average_power_watts,max_power_watts,average_cadence_rpm,max_cadence_rpm,elevation_gain_meters,fit_file_path)
VALUES($id,$profile,$route,$name,$started,$completed,$created,$status,$duration,NULL,$distance,$laps,$avgSpeed,$maxSpeed,$avgPower,$maxPower,$avgCadence,$maxCadence,$elevation,$fit)";
            Add(command, "$id", a.Id.ToString("D")); Add(command, "$profile", a.RiderProfileId.ToString("D")); Add(command, "$route", a.RouteId); Add(command, "$name", a.RouteName);
            Add(command, "$started", Time(a.StartedAtUtc)); Add(command, "$completed", Time(DateTimeOffset.UtcNow)); Add(command, "$created", Time(DateTimeOffset.UtcNow)); Add(command, "$status", a.Status.ToString());
            Add(command, "$duration", a.DurationSeconds); Add(command, "$distance", a.DistanceMeters); Add(command, "$laps", a.CompletedLaps);
            Add(command, "$avgSpeed", a.AverageSpeedKph); Add(command, "$maxSpeed", a.MaxSpeedKph); Add(command, "$avgPower", a.AveragePowerWatts); Add(command, "$maxPower", a.MaxPowerWatts);
            Add(command, "$avgCadence", a.AverageCadenceRpm); Add(command, "$maxCadence", a.MaxCadenceRpm); Add(command, "$elevation", a.ElevationGainMeters); Add(command, "$fit", a.FitFilePath);
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

    private const string Columns = "id,rider_profile_id,route_id,route_name,started_utc,completed_utc,created_utc,status,duration_seconds,moving_seconds,distance_meters,completed_laps,average_speed_kph,max_speed_kph,average_power_watts,max_power_watts,average_cadence_rpm,max_cadence_rpm,elevation_gain_meters,fit_file_path";
    private static RideActivity ReadActivity(SqliteDataReader r) => new(Guid.Parse(r.GetString(0)), Guid.Parse(r.GetString(1)), r.GetString(2), r.GetString(3), ParseTime(r.GetString(4)), r.IsDBNull(5) ? null : ParseTime(r.GetString(5)), Enum.Parse<RideActivityStatus>(r.GetString(7)), r.GetDouble(8), N(r,9), r.GetDouble(10), r.GetInt32(11), N(r,12), N(r,13), N(r,14), N(r,15), N(r,16), N(r,17), r.GetDouble(18), NullableString(r,19), ParseTime(r.GetString(6)));
    private static double? N(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetDouble(i);
    private static string? NullableString(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    private static DateTimeOffset ParseTime(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    private static string Time(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static void Add(SqliteCommand command, string name, object? value) => command.Parameters.AddWithValue(name, value ?? DBNull.Value);
}
