using Godot;
using IndoorCycling.Core.Models;
using IndoorCycling.Core.Simulation;
using IndoorCycling.Trainer.Abstractions;
using IndoorCycling.Trainer.Implementations;
using IndoorCycling.Trainer.Models;
using IndoorCycling.Core.Persistence;
using IndoorCycling.Persistence.Sqlite;
using IndoorCycling.Trainer.Windows;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Velopack.Windows;

public partial class GameBootstrap : Node
{
	public enum SessionMode
	{
		None,
		Trainer,
		FitReplay
	}

	public static ITrainerClient? TrainerClient { get; private set; }
	public static WindowsBleHeartRateClient? HeartRateClient { get; set; }
	public static ISimulationEngine SimulationEngine { get; private set; } = default!;
	public static RiderProfile RiderProfile { get; private set; } = default!;
	public static BikeProfile BikeProfile { get; private set; } = default!;

	public static AppSettings Settings { get; private set; } = default!;
	public static bool DebugModeEnabled => Settings?.DebugModeEnabled == true;
	public static IRideHistoryRepository? RideHistory { get; private set; }
	public static int TrainerDifficultyPercent => TrainerDifficultyPolicy.Normalize(
		_persistentRider?.TrainerDifficultyPercent ?? TrainerDifficultyPolicy.DefaultPercent);
	private static RiderProfileRecord? _persistentRider;
	private static Window? _mainWindow;
	private static Task<RiderProfileRecord>? _persistentRiderTask;
	private static Task? _pendingProfileSelectionTask;
	private double _windowSaveCountdown = -1;
	private static bool IsLayoutSmokeTest => OS.GetCmdlineUserArgs().Contains("--layout-smoke-test");
	private static bool IsWindowPersistenceSuppressed => OS.GetCmdlineArgs().Contains("--headless") || IsLayoutSmokeTest;

	public static SessionMode Mode { get; private set; } = SessionMode.None;
	public static string? FitReplayPath { get; private set; }

	public override void _Ready()
	{
		var userDataDirectory = OS.GetUserDataDir();
		var userDataParent = Directory.GetParent(userDataDirectory)?.FullName;
		if (userDataParent is not null && !IsLayoutSmokeTest)
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

		RefreshInstalledShortcutIcons();

		Settings = AppSettings.LoadOrCreate();
		GameLogger.Init();
		if (!OS.GetCmdlineUserArgs().Contains("--layout-smoke-test"))
			StravaIntegrationClient.Shared.ResumePendingAutomaticUploads();
		ConfigureMainWindow();
		Settings.DummyTrainer ??= new DummyTrainerSettings();
		Settings.DummyTrainer.Validate();
		var historyPath = Path.Combine(ProjectSettings.GlobalizePath("user://data"), "wodra.db");
		RideHistory = new SqliteRideHistoryRepository(historyPath);
		_persistentRiderTask = InitializePersistentRiderAsync();
		_ = ObservePersistenceStartupAsync(_persistentRiderTask);

		SimulationEngine = new SimulationEngine();

		RiderProfile = new RiderProfile(
			"Rider",
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

	private static void RefreshInstalledShortcutIcons()
	{
		if (!OperatingSystem.IsWindows() || IsWindowPersistenceSuppressed)
			return;

		try
		{
			var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Branding", "wodra.ico");
			if (!File.Exists(iconPath))
				return;

			// Refresh only shortcuts already owned by this Velopack installation.
			// The installer creates these on a clean install; this updates their icon
			// after an app upgrade without recreating deleted links or touching others.
			new Shortcuts().CreateShortcut("Wodra.exe",
				ShortcutLocation.StartMenuRoot | ShortcutLocation.Desktop,
				updateOnly: true, programArguments: null, icon: iconPath);
		}
		catch
		{
			GD.PushWarning("Wodra could not refresh its installed shortcut icons.");
		}
	}

	private void ConfigureMainWindow()
	{
		var window = GetWindow();
		_mainWindow = window;
		var screens = GetUsableScreens();
		var requested = new WindowRectangle(Settings.WindowedX, Settings.WindowedY,
			Math.Max(1, Settings.WindowedWidth), Math.Max(1, Settings.WindowedHeight));
		var hasSavedPosition = Settings.WindowedX != int.MinValue && Settings.WindowedY != int.MinValue;
		var screenIndex = hasSavedPosition
			? WindowPlacementPolicy.SelectScreen(Settings.WindowScreen, requested, screens)
			: Math.Clamp(Settings.WindowScreen, 0, screens.Count - 1);
		var usable = screens[screenIndex];
		var placement = WindowPlacementPolicy.Clamp(requested, usable);
		if (!hasSavedPosition)
			placement = placement with { X = usable.X + (usable.Width - placement.Width) / 2,
				Y = usable.Y + (usable.Height - placement.Height) / 2 };
		window.Mode = Window.ModeEnum.Windowed;
		window.MinSize = new Vector2I(Math.Min(960, usable.Width), Math.Min(540, usable.Height));
		window.CurrentScreen = screenIndex;
		window.Size = new Vector2I(placement.Width, placement.Height);
		window.Position = new Vector2I(placement.X, placement.Y);
		window.SizeChanged += OnMainWindowSizeChanged;
		window.CloseRequested += OnMainWindowCloseRequested;
		window.Mode = Settings.Fullscreen ? Window.ModeEnum.Fullscreen
			: Settings.WindowMaximized ? Window.ModeEnum.Maximized : Window.ModeEnum.Windowed;
	}

	private static List<WindowRectangle> GetUsableScreens()
	{
		var count = Math.Max(1, DisplayServer.GetScreenCount());
		var screens = new List<WindowRectangle>(count);
		for (var i = 0; i < count; i++)
		{
			var rect = DisplayServer.ScreenGetUsableRect(i);
			screens.Add(new WindowRectangle(rect.Position.X, rect.Position.Y,
				Math.Max(1, rect.Size.X), Math.Max(1, rect.Size.Y)));
		}
		return screens;
	}

	private void OnMainWindowSizeChanged() => _windowSaveCountdown = Math.Max(_windowSaveCountdown, 0.75);

	private void OnMainWindowCloseRequested()
	{
		SaveWindowPlacement();
		GetTree().Quit();
	}

	public override void _Input(InputEvent @event)
	{
		if (@event is not InputEventKey key || !key.Pressed || key.Echo ||
			!(key.Keycode == Key.F11 || (key.Keycode == Key.Enter && key.AltPressed))) return;
		ToggleFullscreen();
		GetViewport().SetInputAsHandled();
	}

	public static void ToggleFullscreen()
	{
		if (Settings is null) return;
		var window = _mainWindow;
		if (window is null) return;
		if (window.Mode != Window.ModeEnum.Fullscreen)
		{
			if (window.Mode == Window.ModeEnum.Windowed) CaptureWindowedRectangle(window);
			Settings.WindowMaximized = window.Mode == Window.ModeEnum.Maximized || Settings.WindowMaximized;
			Settings.Fullscreen = true;
			window.Mode = Window.ModeEnum.Fullscreen;
		}
		else
		{
			Settings.Fullscreen = false;
			RestoreWindowedRectangle(window);
			window.Mode = Settings.WindowMaximized ? Window.ModeEnum.Maximized : Window.ModeEnum.Windowed;
		}
		Settings.Save();
	}

	public override void _Process(double delta)
	{
		EpicOnlineServicesMultiplayerProvider.TickPlatform(delta);
		TrackWindowPlacement(delta);
	}

	private void TrackWindowPlacement(double delta)
	{
		if (IsWindowPersistenceSuppressed) return;
		var window = _mainWindow;
		if (window is null || Settings is null) return;
		if (window.Mode == Window.ModeEnum.Windowed)
		{
			if (Settings.WindowMaximized) { Settings.WindowMaximized = false; Settings.Save(); }
			var before = (Settings.WindowedX, Settings.WindowedY, Settings.WindowedWidth,
				Settings.WindowedHeight, Settings.WindowScreen);
			CaptureWindowedRectangle(window);
			if (before != (Settings.WindowedX, Settings.WindowedY, Settings.WindowedWidth,
				Settings.WindowedHeight, Settings.WindowScreen)) _windowSaveCountdown = 0.75;
		}
		else if (window.Mode == Window.ModeEnum.Maximized && !Settings.WindowMaximized)
		{
			Settings.WindowMaximized = true;
			Settings.Save();
		}
		if (_windowSaveCountdown >= 0)
		{
			_windowSaveCountdown -= delta;
			if (_windowSaveCountdown < 0) { Settings.Save(); _windowSaveCountdown = -1; }
		}
	}

	private static void CaptureWindowedRectangle(Window window)
	{
		var screens = GetUsableScreens();
		var screen = Math.Clamp(window.CurrentScreen, 0, screens.Count - 1);
		var rect = WindowPlacementPolicy.Clamp(new WindowRectangle(window.Position.X, window.Position.Y,
			window.Size.X, window.Size.Y), screens[screen]);
		Settings.WindowedX = rect.X;
		Settings.WindowedY = rect.Y;
		Settings.WindowedWidth = rect.Width;
		Settings.WindowedHeight = rect.Height;
		Settings.WindowScreen = screen;
	}

	private static void RestoreWindowedRectangle(Window window)
	{
		var screens = GetUsableScreens();
		var requested = new WindowRectangle(Settings.WindowedX, Settings.WindowedY,
			Math.Max(1, Settings.WindowedWidth), Math.Max(1, Settings.WindowedHeight));
		var screen = WindowPlacementPolicy.SelectScreen(Settings.WindowScreen, requested, screens);
		var rect = WindowPlacementPolicy.Clamp(requested, screens[screen]);
		window.Mode = Window.ModeEnum.Windowed;
		window.CurrentScreen = screen;
		window.Size = new Vector2I(rect.Width, rect.Height);
		window.Position = new Vector2I(rect.X, rect.Y);
	}

	private void SaveWindowPlacement()
	{
		if (IsWindowPersistenceSuppressed) return;
		var window = _mainWindow;
		if (window is null || Settings is null) return;
		if (window.Mode == Window.ModeEnum.Windowed)
		{
			Settings.WindowMaximized = false;
			CaptureWindowedRectangle(window);
		}
		else if (window.Mode == Window.ModeEnum.Maximized) Settings.WindowMaximized = true;
		Settings.Save();
	}

	public override void _ExitTree()
	{
		SaveWindowPlacement();
		if (_mainWindow is not null)
		{
			_mainWindow.SizeChanged -= OnMainWindowSizeChanged;
			_mainWindow.CloseRequested -= OnMainWindowCloseRequested;
		}
		EpicOnlineServicesMultiplayerProvider.ShutdownPlatform();
		GameLogger.EndSession();
	}

	public static async Task<RiderProfileRecord> GetPersistentRiderAsync()
	{
		// A profile picker change is asynchronous. If a ride or online join starts
		// immediately after selection, wait for that selection to become authoritative.
		if (_pendingProfileSelectionTask is { } pendingSelection)
			await pendingSelection;
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
				"Rider",
				DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, Settings.Rider.WeightKg, Settings.Rider.BikeWeightKg);
			ApplyActiveProfile(_persistentRider);
			return _persistentRider;
		}
		_persistentRider = selection.ActiveProfile!;
		if (Settings.ActiveProfileId != _persistentRider.Id)
		{
			Settings.ActiveProfileId = _persistentRider.Id;
			Settings.Save();
		}
		ApplyActiveProfile(_persistentRider);
		return _persistentRider!;
	}

	public static Task SetActiveProfileAsync(Guid id)
	{
		var selection = SetActiveProfileCoreAsync(id);
		_pendingProfileSelectionTask = selection;
		_ = ClearCompletedProfileSelectionAsync(selection);
		return selection;
	}

	private static async Task SetActiveProfileCoreAsync(Guid id)
	{
		var repository = RideHistory ?? throw new InvalidOperationException("Ride history is unavailable.");
		var profile = await repository.GetProfileAsync(id) ?? throw new InvalidOperationException("Rider profile does not exist.");
		_persistentRider = profile;
		Settings.ActiveProfileId = profile.Id;
		Settings.Save();
		ApplyActiveProfile(profile);
	}

	private static async Task ClearCompletedProfileSelectionAsync(Task selection)
	{
		try { await selection; }
		catch { /* The picker reports selection errors; leave the prior profile active. */ }
		if (ReferenceEquals(_pendingProfileSelectionTask, selection))
			_pendingProfileSelectionTask = null;
	}

	public static async Task SaveProfileAsync(RiderProfileRecord profile)
	{
		var repository = RideHistory ?? throw new InvalidOperationException("Ride history is unavailable.");
		await repository.SaveProfileAsync(profile);
		if (Settings.ActiveProfileId == profile.Id) { _persistentRider = profile; ApplyActiveProfile(profile); }
	}

	public static async Task SetTrainerDifficultyAsync(int percent)
	{
		var profile = await GetPersistentRiderAsync();
		var updated = profile with { TrainerDifficultyPercent = TrainerDifficultyPolicy.Normalize(percent), UpdatedAtUtc = DateTimeOffset.UtcNow };
		if (updated.Id != Guid.Empty)
			await (RideHistory ?? throw new InvalidOperationException("Ride history is unavailable.")).SaveProfileAsync(updated);
		_persistentRider = updated;
		ApplyActiveProfile(updated);
	}

	private static void ApplyActiveProfile(RiderProfileRecord profile)
	{
		RiderProfile = new RiderProfile(profile.Name, profile.BodyWeightKg, profile.Id, profile.BikeWeightKg,
			profile.IsTestProfile, profile.CreatedAtUtc, profile.UpdatedAtUtc);
		BikeProfile = new BikeProfile(Settings.Rider.BikeName, profile.BikeWeightKg);
		GameLogger.Log("rider_profile_activated", new { profileId = profile.Id, displayName = profile.Name });
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
