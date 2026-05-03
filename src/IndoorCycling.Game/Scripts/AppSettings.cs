using System;
using System.IO;
using System.Text.Json;
using Godot;

public sealed class AppSettings
{
	public RiderSettings Rider { get; set; } = new();
	public RecordingSettings Recording { get; set; } = new();
	public StravaSettings Strava { get; set; } = new();

	public static string SettingsPath
	{
		get
		{
			var dir = ProjectSettings.GlobalizePath("user://settings");
			return Path.Combine(dir, "settings.json");
		}
	}

	public static AppSettings LoadOrCreate()
	{
		try
		{
			var dir = Path.GetDirectoryName(SettingsPath)!;
			Directory.CreateDirectory(dir);

			if (!File.Exists(SettingsPath))
			{
				var defaults = new AppSettings();
				defaults.Save();

				GameLogger.Log("settings_created", new
				{
					path = SettingsPath
				});

				return defaults;
			}

			var json = File.ReadAllText(SettingsPath);
			var settings = JsonSerializer.Deserialize<AppSettings>(
				json,
				new JsonSerializerOptions
				{
					PropertyNameCaseInsensitive = true
				}
			);

			if (settings is null)
			{
				var defaults = new AppSettings();
				defaults.Save();
				return defaults;
			}

			GameLogger.Log("settings_loaded", new
			{
				path = SettingsPath,
				riderName = settings.Rider.Name,
				riderWeightKg = settings.Rider.WeightKg,
				bikeWeightKg = settings.Rider.BikeWeightKg
			});

			return settings;
		}
		catch (Exception ex)
		{
			GameLogger.Log("settings_load_failed", new
			{
				path = SettingsPath,
				message = ex.Message,
				exceptionType = ex.GetType().FullName
			});

			return new AppSettings();
		}
	}

	public void Save()
	{
		var dir = Path.GetDirectoryName(SettingsPath)!;
		Directory.CreateDirectory(dir);

		var json = JsonSerializer.Serialize(
			this,
			new JsonSerializerOptions
			{
				WriteIndented = true
			}
		);

		File.WriteAllText(SettingsPath, json);

		GameLogger.Log("settings_saved", new
		{
			path = SettingsPath
		});
	}
}

public sealed class RiderSettings
{
	public string Name { get; set; } = "Gareth";
	public double WeightKg { get; set; } = 88.0;
	public string BikeName { get; set; } = "Indoor Bike";
	public double BikeWeightKg { get; set; } = 9.0;
}

public sealed class RecordingSettings
{
	public string OutputFolder { get; set; } = "%USERPROFILE%\\Documents\\gwift";
}

public sealed class StravaSettings
{
	public string ClientId { get; set; } = "";
	public string ClientSecret { get; set; } = "";
	public string RefreshToken { get; set; } = "";
	public string AccessToken { get; set; } = "";
	public long ExpiresAtUnix { get; set; } = 0;
}