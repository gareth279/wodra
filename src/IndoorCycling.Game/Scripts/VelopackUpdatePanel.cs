using Godot;
using System;
using System.Threading.Tasks;
using Velopack;
using Velopack.Sources;

/// <summary>Startup-only update UI. Downloading is safe during play, applying is not.</summary>
public partial class VelopackUpdatePanel : Control
{
	private const string RepositoryUrl = "https://github.com/gareth279/wodra";
	private UpdateManager? _manager;
	private UpdateInfo? _availableUpdate;
	private Label? _status;
	private Button? _checkButton;
	private Window? _detailsWindow;
	private Label? _detailsTitle;
	private RichTextLabel? _notes;
	private ProgressBar? _progress;
	private Button? _installButton;
	private bool _checking;
	private bool _downloading;
	private bool _staged;

	public void Attach()
	{
		SizeFlagsHorizontal = SizeFlags.ExpandFill;
		var row = new HBoxContainer { Name = "UpdateStatusRow", SizeFlagsHorizontal = SizeFlags.ExpandFill };
		_status = new Label
		{
			Text = "Updates are checked when online.",
			SizeFlagsHorizontal = SizeFlags.ExpandFill,
			AutowrapMode = TextServer.AutowrapMode.WordSmart,
			ClipText = true
		};
		_status.AddThemeFontSizeOverride("font_size", 13);
		row.AddChild(_status);
		_checkButton = new Button { Text = "Check for Updates" };
		_checkButton.Pressed += CheckForUpdates;
		row.AddChild(_checkButton);
		AddChild(row);
		BuildDetailsWindow();
		TryCreateManager();
		if (_manager is not null)
			_ = CheckForUpdatesAsync();
	}

	public void OnStartupShown()
	{
		if (_staged && GameBootstrap.Mode == GameBootstrap.SessionMode.None)
		{
			SetStatus("Update downloaded. Restart Wodra to apply it.", showRetry: true);
			if (_installButton is not null) _installButton.Disabled = false;
		}
		if (_manager is not null && !_checking && _availableUpdate is null && _status?.Text == "Updates are checked when online.")
			_ = CheckForUpdatesAsync();
	}

	private void TryCreateManager()
	{
		try
		{
			var source = new GithubSource(RepositoryUrl, accessToken: null, prerelease: true);
			var manager = new UpdateManager(source, new UpdateOptions { ExplicitChannel = "alpha" });
			if (!manager.IsInstalled)
			{
				SetStatus("Updates are available for installed builds.", showRetry: false);
				return;
			}
			_manager = manager;
			GameLogger.Log("update_manager_ready", new { currentVersion = manager.CurrentVersion?.ToString() });
		}
		catch (Exception ex)
		{
			GameLogger.Log("update_manager_initialization_failed", new { exceptionType = ex.GetType().Name, message = ex.Message });
			SetStatus("Updates are unavailable. You can still ride.", showRetry: true);
		}
	}

	private void CheckForUpdates()
	{
		if (_availableUpdate is not null)
		{
			_detailsWindow?.PopupCentered();
			return;
		}
		if (_manager is null) TryCreateManager();
		_ = CheckForUpdatesAsync();
	}

	private async Task CheckForUpdatesAsync()
	{
		if (_manager is null || _checking) return;
		_checking = true;
		SetStatus("Checking for updates…", showRetry: false);
		if (_checkButton is not null) _checkButton.Disabled = true;
		try
		{
			_availableUpdate = await _manager.CheckForUpdatesAsync();
			if (_availableUpdate is null)
			{
				GameLogger.Log("update_check_complete", new { available = false });
				SetStatus("Wodra is up to date.", showRetry: true);
				return;
			}

			var release = _availableUpdate.TargetFullRelease;
			GameLogger.Log("update_available", new { version = release.Version.ToString() });
			if (_status is not null) _status.Text = $"Wodra {release.Version} is available.";
			if (_checkButton is not null) _checkButton.Text = "Review Update";
			PopulateDetails(release);
			_detailsWindow?.PopupCentered();
		}
		catch (Exception ex)
		{
			GameLogger.Log("update_check_failed", new { exceptionType = ex.GetType().Name, message = ex.Message });
			SetStatus("Could not check for updates. Check your connection and retry.", showRetry: true);
		}
		finally
		{
			_checking = false;
			if (_checkButton is not null) _checkButton.Disabled = false;
		}
	}

	private void BuildDetailsWindow()
	{
		_detailsWindow = new Window
		{
			Title = "Wodra Update",
			Size = new Vector2I(520, 440),
			MinSize = new Vector2I(380, 320),
			Exclusive = true
		};
		var margin = new MarginContainer();
		margin.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
		foreach (var edge in new[] { "left", "right", "top", "bottom" }) margin.AddThemeConstantOverride($"margin_{edge}", 18);
		var layout = new VBoxContainer();
		_detailsTitle = new Label { Text = "Wodra update available", AutowrapMode = TextServer.AutowrapMode.WordSmart };
		_detailsTitle.AddThemeFontSizeOverride("font_size", 20);
		_notes = new RichTextLabel { FitContent = false, ScrollActive = true, SizeFlagsVertical = SizeFlags.ExpandFill, BbcodeEnabled = false };
		_notes.AutowrapMode = TextServer.AutowrapMode.WordSmart;
		_progress = new ProgressBar { MinValue = 0, MaxValue = 100, Value = 0, Visible = false };
		var actions = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End };
		var later = new Button { Text = "Later" };
		later.Pressed += () => _detailsWindow?.Hide();
		_installButton = new Button { Text = "Download and Install" };
		_installButton.Pressed += DownloadAndApplyUpdate;
		actions.AddChild(later);
		actions.AddChild(_installButton);
		layout.AddChild(_detailsTitle);
		layout.AddChild(_notes);
		layout.AddChild(_progress);
		layout.AddChild(actions);
		margin.AddChild(layout);
		_detailsWindow.AddChild(margin);
		AddChild(_detailsWindow);
	}

	private void PopulateDetails(VelopackAsset release)
	{
		if (_detailsTitle is not null) _detailsTitle.Text = $"Wodra {release.Version} is ready to download";
		if (_notes is not null)
			_notes.Text = string.IsNullOrWhiteSpace(release.NotesMarkdown) ? "No release notes were provided." : release.NotesMarkdown;
	}

	private async void DownloadAndApplyUpdate()
	{
		if (_manager is null || _availableUpdate is null || _downloading) return;
		if (GameBootstrap.Mode != GameBootstrap.SessionMode.None || !Visible)
		{
			SetStatus("Finish the current ride before applying updates.", showRetry: true);
			_detailsWindow?.Hide();
			return;
		}

		_downloading = true;
		if (_installButton is not null) _installButton.Disabled = true;
		if (_progress is not null) { _progress.Value = 0; _progress.Visible = true; }
		if (_detailsTitle is not null) _detailsTitle.Text = "Downloading update…";
		SetStatus("Downloading update…", showRetry: false);
		GameLogger.Log("update_download_started", new { version = _availableUpdate.TargetFullRelease.Version.ToString() });
		try
		{
			var info = _availableUpdate;
			if (_staged)
			{
				_manager.ApplyUpdatesAndRestart(info.TargetFullRelease, Array.Empty<string>());
				return;
			}
			await _manager.DownloadUpdatesAsync(info, value => CallDeferred(MethodName.SetDownloadProgress, value));
			if (GameBootstrap.Mode != GameBootstrap.SessionMode.None)
			{
				_staged = true;
				SetStatus("Update downloaded. It will wait until you finish riding.", showRetry: true);
				if (_detailsTitle is not null) _detailsTitle.Text = "Update downloaded — apply after your ride";
				if (_installButton is not null) { _installButton.Text = "Apply and Restart"; _installButton.Disabled = true; }
				GameLogger.Log("update_download_staged_during_ride", new { version = info.TargetFullRelease.Version.ToString() });
				return;
			}

			GameLogger.Log("update_download_complete", new { version = info.TargetFullRelease.Version.ToString() });
			_detailsWindow?.Hide();
			SetStatus("Applying update and restarting Wodra…", showRetry: false);
			_manager.ApplyUpdatesAndRestart(info.TargetFullRelease, Array.Empty<string>());
		}
		catch (Exception ex)
		{
			GameLogger.Log("update_download_or_apply_failed", new { exceptionType = ex.GetType().Name, message = ex.Message });
			if (_detailsTitle is not null) _detailsTitle.Text = "Update failed. Your current Wodra install is unchanged.";
			SetStatus("Update failed. Retry when your connection is available.", showRetry: true);
		}
		finally
		{
			_downloading = false;
			if (_installButton is not null && _detailsWindow?.Visible == true)
				_installButton.Disabled = GameBootstrap.Mode != GameBootstrap.SessionMode.None;
		}
	}

	public void SetDownloadProgress(int percentage)
	{
		if (_progress is not null) _progress.Value = Math.Clamp(percentage, 0, 100);
	}

	private void SetStatus(string text, bool showRetry)
	{
		if (_status is not null) _status.Text = text;
		if (_checkButton is not null)
		{
			_checkButton.Text = showRetry ? "Retry" : _availableUpdate is null ? "Check for Updates" : "Review Update";
			_checkButton.Visible = showRetry || _availableUpdate is not null;
		}
	}
}
