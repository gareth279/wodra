using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace IndoorCycling.Core.Persistence;

/// <summary>Copies files from prior Godot application-data folders without replacing Wodra data.</summary>
public static class LegacyUserDataMigrator
{
    private static readonly string[] MigratedDirectories = ["settings", "data", "logs", "screenshots"];
    private const string MigrationMarker = ".legacy-user-data-migrated";

    public static UserDataMigrationResult Migrate(string newUserDataDirectory,
        IEnumerable<string> legacyUserDataDirectories)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(newUserDataDirectory);
        ArgumentNullException.ThrowIfNull(legacyUserDataDirectories);

        var destination = Path.GetFullPath(newUserDataDirectory);
        Directory.CreateDirectory(destination);
        var markerPath = Path.Combine(destination, MigrationMarker);
        if (File.Exists(markerPath)) return new UserDataMigrationResult(Array.Empty<string>(), false);
        var legacyDirectories = legacyUserDataDirectories
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Where(path => !string.Equals(path, destination, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var copied = new List<string>();
        string? databaseSource = null;

        foreach (var legacy in legacyDirectories)
        {
            if (!Directory.Exists(legacy)) continue;
            foreach (var directoryName in MigratedDirectories)
            {
                var oldDirectory = Path.Combine(legacy, directoryName);
                if (!Directory.Exists(oldDirectory)) continue;
                foreach (var sourceFile in Directory.EnumerateFiles(oldDirectory, "*", SearchOption.AllDirectories))
                {
                    var relative = Path.GetRelativePath(oldDirectory, sourceFile);
                    if (directoryName == "data" && IsLegacyDatabase(relative))
                    {
                        databaseSource ??= sourceFile;
                        continue;
                    }
                    var targetFile = Path.Combine(destination, directoryName, relative);
                    if (TryCopyMissing(sourceFile, targetFile)) copied.Add(targetFile);
                }
            }
            var legacyDatabase = Path.Combine(legacy, "data", "gwift.db");
            if (databaseSource is null && File.Exists(legacyDatabase)) databaseSource = legacyDatabase;
        }

        var databaseDestination = Path.Combine(destination, "data", "wodra.db");
        var databaseMigrated = databaseSource is not null
            && TryMigrateDatabase(databaseSource, databaseDestination, copied);

        File.WriteAllText(markerPath, "Completed");

        return new UserDataMigrationResult(copied, databaseMigrated);
    }

    private static bool IsLegacyDatabase(string relativePath) =>
        string.Equals(relativePath, "gwift.db", StringComparison.OrdinalIgnoreCase);

    private static bool TryMigrateDatabase(string source, string destination, List<string> copied)
    {
        if (File.Exists(destination)) return false;
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var stagingDatabase = destination + ".migration-" + Guid.NewGuid().ToString("N");
        var sourceWal = source + "-wal";
        var destinationWal = destination + "-wal";
        var stagingWal = stagingDatabase + "-wal";
        try
        {
            File.Copy(source, stagingDatabase, overwrite: false);
            if (File.Exists(sourceWal)) File.Copy(sourceWal, stagingWal, overwrite: false);

            // Publish the WAL before the database: the final database filename is the
            // completion marker, and SQLite can rebuild the transient -shm file.
            if (File.Exists(stagingWal) && !File.Exists(destinationWal))
            {
                try { File.Move(stagingWal, destinationWal, overwrite: false); }
                catch (IOException) when (File.Exists(destinationWal)) { }
            }
            try
            {
                File.Move(stagingDatabase, destination, overwrite: false);
                copied.Add(destination);
                if (File.Exists(destinationWal)) copied.Add(destinationWal);
                return true;
            }
            catch (IOException) when (File.Exists(destination)) { return false; }
        }
        finally
        {
            if (File.Exists(stagingDatabase)) File.Delete(stagingDatabase);
            if (File.Exists(stagingWal)) File.Delete(stagingWal);
        }
    }

    private static bool TryCopyMissing(string source, string destination)
    {
        if (File.Exists(destination)) return false;
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + ".migration-" + Guid.NewGuid().ToString("N");
        try
        {
            File.Copy(source, temporary, overwrite: false);
            try
            {
                File.Move(temporary, destination, overwrite: false);
                return true;
            }
            catch (IOException) when (File.Exists(destination)) { return false; }
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}

public sealed record UserDataMigrationResult(IReadOnlyList<string> CopiedFiles, bool DatabaseMigrated);
