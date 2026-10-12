using Godot;
using IndoorCycling.Core.Models;
using IndoorCycling.Core.Simulation;
using IndoorCycling.Trainer.Abstractions;
using IndoorCycling.Trainer.Implementations;
using IndoorCycling.Core.Persistence;
using IndoorCycling.Persistence.Sqlite;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

public partial class GameBootstrap : Node
{
	public enum SessionMode
	{
		None,
		Trainer,
		FitReplay
	}

	public static ITrainerClient? TrainerClient { get; private set; }
	public static ISimulationEngine SimulationEngine { get; private set; } = default!;
	public static RiderProfile RiderProfile { get; private set; } = default!;
	public static BikeProfile BikeProfile { get; private set; } = default!;

	public static AppSettings Settings { get; private set; } = default!;
	public static bool DebugModeEnabled => Settings?.DebugModeEnabled == true;
	public static IRideHistoryRepository? RideHistory { get; private set; }
	private static RiderProfileRecord? _persistentRider;
	private static Task<RiderProfileRecord>? _persistentRiderTask;

	public static SessionMode Mode { get; private set; } = SessionMode.None;
	public static string? FitReplayPath { get; private set; }

	public override void _Ready()
	{
		var userDataDirectory = OS.GetUserDataDir();
		var userDataParent = Directory.GetParent(userDataDirectory)?.FullName;
		if (userDataParent is not null)
		{
			try
			{
				var migration = LegacyUserDataMigrator.Migrate(userDataDirectory,
				[
					Path.Combine(userDataParent, "GWift"),
					Path.Combine(userDataParent, "IndoorCycling.Game")
				]);
				if (migration.CopiedFiles.Count > 0)
					GD.Print($"Migrated {migration.CopiedFiles.Count} legacy user-data file(s) into Wodra.");
			}
			catch (Exception ex)
			{
				GD.PushWarning($"Wodra could not finish importing legacy user data: {ex.Message}");
			}
		}

		GameLogger.Init();

		Settings = AppSettings.LoadOrCreate();
		Settings.DummyTrainer ??= new DummyTrainerSettings();
		Settings.DummyTrainer.Validate();
		var historyPath = Path.Combine(ProjectSettings.GlobalizePath("user://data"), "wodra.db");
		RideHistory = new SqliteRideHistoryRepository(historyPath);
		_persistentRiderTask = InitializePersistentRiderAsync();
		_ = ObservePersistenceStartupAsync(_persistentRiderTask);

		SimulationEngine = new SimulationEngine();

		RiderProfile = new RiderProfile(
			Settings.Rider.Name,
			Settings.Rider.WeightKg
		);

		BikeProfile = new BikeProfile(
			Settings.Rider.BikeName,
			Settings.Rider.BikeWeightKg
		);

		GameLogger.Log("game_bootstrap_initialised", new
		{
			rider = RiderProfile.Name,
			riderWeightKg = RiderProfile.WeightKg,
			bike = BikeProfile.Name,
			bikeWeightKg = BikeProfile.WeightKg,
			settingsPath = AppSettings.SettingsPath
		});

		GD.Print("GameBootstrap initialised.");
	}

	public override void _Process(double delta)
	{
		EpicOnlineServicesMultiplayerProvider.TickPlatform(delta);
	}

	public override void _ExitTree()
	{
		EpicOnlineServicesMultiplayerProvider.ShutdownPlatform();
	}

	public static async Task<RiderProfileRecord> GetPersistentRiderAsync()
	{
		if (_persistentRider is not null) return _persistentRider;
		_persistentRiderTask ??= InitializePersistentRiderAsync();
		return await _persistentRiderTask;
	}

	private static async Task<RiderProfileRecord> InitializePersistentRiderAsync()
	{
		var repository = RideHistory ?? throw new InvalidOperationException("Ride history is unavailable.");
		await repository.InitializeAsync();
		var profiles = await repository.GetAllProfilesAsync();
		var selection = RiderProfileStartupSelection.Resolve(profiles, Settings.ActiveProfileId);
		if (selection.OpenCreateProfile)
		{
			// Keep a temporary in-memory rider for startup UI/preview; the first persistent
			// profile is created only after the user saves the profile form.
			_persistentRider = new RiderProfileRecord(Guid.Empty,
				string.IsNullOrWhiteSpace(Settings.Rider.Name) ? "Rider" : Settings.Rider.Name,
				DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, Settings.Rider.WeightKg, Settings.Rider.BikeWeightKg);
			ApplyActiveProfile(_persistentRider);
			return _persistentRider;
		}
		_persistentRider = selection.ActiveProfile!;
		if (Settings.ActiveProfileId != _persistentRider.Id)
		{
			if (Settings.ActiveProfileId is null && profiles.Count == 1)
			{
				// Preserve legacy single-profile weights from settings on its first profile-aware launch.
				_persistentRider = _persistentRider with { BodyWeightKg = Settings.Rider.WeightKg, BikeWeightKg = Settings.Rider.BikeWeightKg };
				await repository.SaveProfileAsync(_persistentRider);
			}
			Settings.ActiveProfileId = _persistentRider.Id;
			Settings.Save();
		}
		ApplyActiveProfile(_persistentRider);
		return _persistentRider!;
	}

	public static async Task SetActiveProfileAsync(Guid id)
	{
		var repository = RideHistory ?? throw new InvalidOperationException("Ride history is unavailable.");
		var profile = await repository.GetProfileAsync(id) ?? throw new InvalidOperationException("Rider profile does not exist.");
		_persistentRider = profile;
		Settings.ActiveProfileId = profile.Id;
		Settings.Save();
		ApplyActiveProfile(profile);
	}

	public static async Task SaveProfileAsync(RiderProfileRecord profile)
	{
		var repository = RideHistory ?? throw new InvalidOperationException("Ride history is unavailable.");
		await repository.SaveProfileAsync(profile);
		if (Settings.ActiveProfileId == profile.Id) { _persistentRider = profile; ApplyActiveProfile(profile); }
	}

	private static void ApplyActiveProfile(RiderProfileRecord profile)
	{
		RiderProfile = new RiderProfile(profile.Name, profile.BodyWeightKg, profile.Id, profile.BikeWeightKg,
			profile.IsTestProfile, profile.CreatedAtUtc, profile.UpdatedAtUtc);
		BikeProfile = new BikeProfile(Settings.Rider.BikeName, profile.BikeWeightKg);
	}

	private static async Task ObservePersistenceStartupAsync(Task<RiderProfileRecord> startup)
	{
		try { await startup; }
		catch (Exception ex)
		{
			GameLogger.Log("ride_history_startup_failed", new { message = ex.Message });
			GD.PushWarning($"Ride history could not initialize. Wodra will remain usable, but rides may not be saved: {ex.Message}");
		}
	}

	public static void ResetSession()
	{
		Mode = SessionMode.None;
		FitReplayPath = null;
		TrainerClient = null;
	}

	public static void ConfigureFakeTrainerMode()
	{
		Mode = SessionMode.Trainer;
		FitReplayPath = null;
		Settings.DummyTrainer.Validate();
		var dummy = new FakeTrainerClient();
		var bodyWeight = RiderProfile?.WeightKg ?? Settings.Rider.WeightKg;
		dummy.SetDummyOutput(Settings.DummyTrainer.CalculateWatts(bodyWeight), Settings.DummyTrainer.CadenceRpm);
		TrainerClient = dummy;

		GD.Print("Configured fake trainer mode.");
	}

	public static void ConfigureRealTrainerMode(ITrainerClient trainerClient)
	{
		Mode = SessionMode.Trainer;
		FitReplayPath = null;
		TrainerClient = trainerClient;

		GD.Print($"Configured real trainer mode: {trainerClient.GetType().Name}");
	}

	public static void ConfigureFitReplayMode(string fitPath)
	{
		Mode = SessionMode.FitReplay;
		FitReplayPath = fitPath;
		TrainerClient = null;

		GD.Print($"Configured FIT replay mode. File: {fitPath}");
	}
}
