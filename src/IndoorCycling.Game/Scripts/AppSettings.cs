using System;
using System.IO;
using System.Text.Json;
using Godot;
using IndoorCycling.Core.Persistence;
using IndoorCycling.Core.Simulation;

public sealed class AppSettings
{
	public RiderSettings Rider { get; set; } = new();
	public RecordingSettings Recording { get; set; } = new();
	public StravaSettings Strava { get; set; } = new();
	public bool DebugModeEnabled { get; set; }
	public bool OnlineRidingEnabled { get; set; }
	public string OnlineMultiplayerProvider { get; set; } = "EpicOnlineServices";
	public string MultiplayerServerAddress { get; set; } = "ws://127.0.0.1:8787/ws";
	public Guid? ActiveProfileId { get; set; }
	public DummyTrainerSettings DummyTrainer { get; set; } = new();
	public RideDisplayPreferences Display { get; set; } = new();
	/// <summary>Last successfully used Bluetooth trainer address; reserved for protocol-neutral preference storage.</summary>
	public ulong? LastTrainerBluetoothAddress { get; set; }
	public int WindowedWidth { get; set; } = 1152;
	public int WindowedHeight { get; set; } = 648;
	public bool Fullscreen { get; set; }

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
			settings.Display ??= new RideDisplayPreferences();

			settings.Recording.OutputFolder = ProductFilePaths.UpgradeLegacyFitOutputFolder(
				settings.Recording.OutputFolder,
				System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile),
				System.Environment.GetFolderPath(System.Environment.SpecialFolder.MyDocuments));

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
	public string Name { get; set; } = "Rider";
	public double WeightKg { get; set; } = 88.0;
	public string BikeName { get; set; } = "Indoor Bike";
	public double BikeWeightKg { get; set; } = 9.0;
}

public sealed class RecordingSettings
{
	public string OutputFolder { get; set; } = ProductFilePaths.DefaultFitOutputFolderSetting;
}

public sealed class DummyTrainerSettings
{
	public string Mode { get; set; } = "W/kg";
	public double WattsPerKg { get; set; } = 2.0;
	public int TargetWatts { get; set; } = 150;
	public int CadenceRpm { get; set; } = 85;
	public int CalculateWatts(double bodyWeightKg) => DummyTrainerMath.CalculateWatts(Mode, TargetWatts, WattsPerKg, bodyWeightKg);
	public void Validate()
	{
		if (Mode is not ("W/kg" or "Watts")) Mode = "W/kg";
		WattsPerKg = Math.Clamp(double.IsFinite(WattsPerKg) ? WattsPerKg : 2, 0, 12);
		TargetWatts = Math.Clamp(TargetWatts, 0, 1500);
		CadenceRpm = Math.Clamp(CadenceRpm, 0, 160);
	}
}

public sealed class StravaSettings
{
	public string ClientId { get; set; } = "";
	public string ClientSecret { get; set; } = "";
	public string RefreshToken { get; set; } = "";
	public string AccessToken { get; set; } = "";
	public long ExpiresAtUnix { get; set; } = 0;
}
