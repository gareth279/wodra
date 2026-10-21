using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Godot;
using IndoorCycling.Core.Models;
using IndoorCycling.Core.Persistence;
using IndoorCycling.Core.Simulation;
using IndoorCycling.Trainer.Models;

public sealed class AppSettings
{
	public RiderSettings Rider { get; set; } = new();
	public RecordingSettings Recording { get; set; } = new();
	public StravaClientSettings StravaClient { get; set; } = new();
	public bool DebugModeEnabled { get; set; }
	public bool OnlineRidingEnabled { get; set; }
	public string OnlineMultiplayerProvider { get; set; } = "EpicOnlineServices";
	public string MultiplayerServerAddress { get; set; } = "ws://127.0.0.1:8787/ws";
	public Guid? ActiveProfileId { get; set; }
	public DummyTrainerSettings DummyTrainer { get; set; } = new();
	public TrainerSettings Trainer { get; set; } = new();
	public OptionalDeviceProtocolPreferences OptionalDeviceProtocols { get; set; } = new();
	public ControllerDeviceTypePreference ControllerDeviceType { get; set; } = ControllerDeviceTypePreference.AutoDetect;
	public RideDisplayPreferences Display { get; set; } = new();
	public SteeringControlSettings Steering { get; set; } = new();
	/// <summary>Per-profile opt-in. Missing profiles default to OFF.</summary>
	public System.Collections.Generic.Dictionary<string, bool> GameFeaturesByProfile { get; set; } = new(StringComparer.OrdinalIgnoreCase);
	public int SteeringSensitivityPreferenceVersion { get; set; }
	/// <summary>Last successfully used Bluetooth identity; never matched against an ANT device number.</summary>
	public ulong? LastTrainerBluetoothAddress { get; set; }
	/// <summary>Last successfully connected Bluetooth heart-rate monitor.</summary>
	public ulong? LastHeartRateBluetoothAddress { get; set; }
	/// <summary>Last successfully connected Bluetooth steering controller.</summary>
	public ulong? LastSteeringBluetoothAddress { get; set; }
	public int WindowedWidth { get; set; } = 1152;
	public int WindowedHeight { get; set; } = 648;
	public int WindowedX { get; set; } = int.MinValue;
	public int WindowedY { get; set; } = int.MinValue;
	public int WindowScreen { get; set; }
	public bool WindowMaximized { get; set; }
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
				var defaults = new AppSettings
				{
					SteeringSensitivityPreferenceVersion = SteeringSensitivityPreferencePolicy.CurrentVersion
				};
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
			// Older development builds briefly exposed a global Strava settings object.
			// Remove those unsupported fields from settings.json without logging their values.
			var removeLegacyStravaSettings = false;
			using (var document = JsonDocument.Parse(json))
			{
				if (document.RootElement.ValueKind == JsonValueKind.Object)
				{
					removeLegacyStravaSettings = document.RootElement.EnumerateObject()
						.Any(property => string.Equals(property.Name, "Strava", StringComparison.OrdinalIgnoreCase));
				}
			}
			settings.Display ??= new RideDisplayPreferences();
			settings.GameFeaturesByProfile ??= new System.Collections.Generic.Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
			settings.Steering ??= new SteeringControlSettings();
			var migratedSteeringSensitivity = SteeringSensitivityPreferencePolicy.Migrate(
				json, settings.Steering, settings.SteeringSensitivityPreferenceVersion);
			settings.SteeringSensitivityPreferenceVersion = SteeringSensitivityPreferencePolicy.CurrentVersion;
			settings.Steering.Normalize();
			settings.StravaClient ??= new StravaClientSettings();
			settings.Trainer ??= new TrainerSettings();
			settings.OptionalDeviceProtocols ??= new OptionalDeviceProtocolPreferences();
			if (!Enum.IsDefined(settings.ControllerDeviceType)) settings.ControllerDeviceType = ControllerDeviceTypePreference.AutoDetect;
			var migratedTrainerPreference = TrainerProtocolPreferencePolicy.MigrateLegacyPreference(
				json, settings.Trainer);
			TrainerProtocolPreferencePolicy.Normalize(settings.Trainer);
			settings.OptionalDeviceProtocols.Set(DeviceProtocolCategory.HeartRate, settings.OptionalDeviceProtocols.HeartRate);
			settings.OptionalDeviceProtocols.Set(DeviceProtocolCategory.Steering, settings.OptionalDeviceProtocols.Steering);

			settings.Recording.OutputFolder = ProductFilePaths.UpgradeLegacyFitOutputFolder(
				settings.Recording.OutputFolder,
				System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile),
				System.Environment.GetFolderPath(System.Environment.SpecialFolder.MyDocuments));
			if (removeLegacyStravaSettings || migratedTrainerPreference || migratedSteeringSensitivity)
				settings.Save();

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

public static class GameFeaturesPreference
{
	public static bool Enabled
	{
		get
		{
			return IndoorCycling.Core.Simulation.GameFeaturePreferencePolicy.IsEnabled(
				GameBootstrap.Settings.GameFeaturesByProfile, GameBootstrap.Settings.ActiveProfileId);
		}
	}

	public static void Set(bool enabled)
	{
		var id = GameBootstrap.Settings.ActiveProfileId;
		if (id is null) return;
		IndoorCycling.Core.Simulation.GameFeaturePreferencePolicy.Set(GameBootstrap.Settings.GameFeaturesByProfile, id.Value, enabled);
		GameBootstrap.Settings.Save();
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

/// <summary>Public Strava client configuration only. The confidential client secret is never stored here.</summary>
public sealed class StravaClientSettings
{
	public string BrokerBaseUrl { get; set; } = "https://api.g5.co.nz/wodra/strava/";
	public string ClientId { get; set; } = "234943";
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
