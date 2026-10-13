using Godot;
using System;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Velopack;
using Velopack.Sources;

/// <summary>Startup-only update UI. Updates can be downloaded during play, but are applied only from a safe startup state.</summary>
public partial class VelopackUpdatePanel : Control
{
	private const string RepositoryUrl = "https://github.com/gareth279/wodra";
	private const string UpdateChannel = "alpha";
	private UpdateManager? _manager;
	private UpdateInfo? _availableUpdate;
	private VelopackAsset? _pendingUpdate;
	private Label? _status;
	private Button? _checkButton;
	private Window? _detailsWindow;
	private Label? _detailsTitle;
	private Label? _detailsStatus;
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
			Text = "Checking for updates…",
			SizeFlagsHorizontal = SizeFlags.ExpandFill,
			AutowrapMode = TextServer.AutowrapMode.WordSmart,
			ClipText = true
		};
		_status.AddThemeFontSizeOverride("font_size", 13);
		row.AddChild(_status);
		// The action only appears when there is something to review or a check failed.
		// In particular, never expose a Retry action while the initial check is pending.
		_checkButton = new Button { Text = "Check for Updates", Visible = false };
		_checkButton.Pressed += CheckForUpdates;
		row.AddChild(_checkButton);
		AddChild(row);
		BuildDetailsWindow();
		TryCreateManager();
		if (_manager?.UpdatePendingRestart is { } pending)
			ShowPendingUpdate(pending);
		else if (_manager is not null)
			_ = CheckForUpdatesAsync();
	}

	public void OnStartupShown()
	{
		if (_manager?.UpdatePendingRestart is { } pending)
		{
			ShowPendingUpdate(pending);
			return;
		}

		if (_staged && GameBootstrap.Mode == GameBootstrap.SessionMode.None)
		{
			SetStatus("Update downloaded. Restart Wodra to apply it.", showRetry: false);
			if (_installButton is not null) _installButton.Disabled = false;
		}
		if (_manager is not null && !_checking && _availableUpdate is null && (_status?.Text is "Checking for updates…" or "Updates are checked when online."))
			_ = CheckForUpdatesAsync();
	}

	private void ShowPendingUpdate(VelopackAsset pending)
	{
		_pendingUpdate = pending;
		_availableUpdate = null;
		_staged = true;
		ShowUpdateDetails(pending.Version.ToString(), "A downloaded update is ready to apply.", "Select Apply and Restart to finish installing this update.");
		SetStatus($"Wodra {pending.Version} is downloaded and ready to apply.", showRetry: false);
		if (_installButton is not null) { _installButton.Text = "Apply and Restart"; _installButton.Disabled = false; }
		_detailsWindow?.PopupCentered();
	}

	private void TryCreateManager()
	{
		try
		{
			var source = new GithubSource(RepositoryUrl, accessToken: null, prerelease: true);
			var manager = new UpdateManager(source, new UpdateOptions { ExplicitChannel = UpdateChannel });
			if (!manager.IsInstalled)
			{
				SetStatus("Automatic updates are available in the installed version.", showRetry: false);
				return;
			}
			_manager = manager;
			GameLogger.Log("update_manager_ready", new
			{
				installedPackageVersion = manager.CurrentVersion?.ToString(),
				applicationVersion = GetApplicationVersion(),
				channel = UpdateChannel,
				feedSource = RepositoryUrl,
				isInstalled = manager.IsInstalled,
				pendingVersion = manager.UpdatePendingRestart?.Version.ToString()
			});
		}
		catch (Exception ex)
		{
			GameLogger.Log("update_manager_initialization_failed", new { exceptionType = ex.GetType().Name, message = ex.Message });
			SetStatus("Updates are unavailable. You can still ride.", showRetry: true);
		}
	}

	private void CheckForUpdates()
	{
		if (_pendingUpdate is not null || _availableUpdate is not null)
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
		var installed = _manager.CurrentVersion;
		var appVersion = GetApplicationVersion();
		GameLogger.Log("update_check_started", new
		{
			installedPackageVersion = installed?.ToString(),
			applicationVersion = appVersion,
			channel = UpdateChannel,
			feedSource = RepositoryUrl
		});
		SetStatus("Checking for updates…", showRetry: false);
		if (_checkButton is not null) _checkButton.Disabled = true;
		try
		{
			var result = await _manager.CheckForUpdatesAsync();
			if (result is null)
			{
				_availableUpdate = null;
				GameLogger.Log("update_check_complete", new
				{
					installedPackageVersion = installed?.ToString(),
					applicationVersion = appVersion,
					candidateVersion = (string?)null,
					channel = UpdateChannel,
					feedSource = RepositoryUrl,
					isGenuinelyNewer = false,
					comparison = "no-candidate"
				});
				SetStatus($"Wodra is up to date ({installed?.ToString() ?? appVersion}).", showRetry: false);
				return;
			}

			var release = result.TargetFullRelease;
			var newer = installed is not null && UpdateVersionPolicy.IsNewer(installed, release.Version);
			GameLogger.Log("update_check_complete", new
			{
				installedPackageVersion = installed?.ToString(),
				applicationVersion = appVersion,
				candidateVersion = release.Version.ToString(),
				channel = UpdateChannel,
				feedSource = RepositoryUrl,
				isGenuinelyNewer = newer,
				comparison = installed is null ? "installed-version-unavailable" : release.Version.CompareTo(installed).ToString()
			});
			if (!newer)
			{
				_availableUpdate = null;
				SetStatus($"Wodra is up to date ({installed?.ToString() ?? appVersion}).", showRetry: false);
				return;
			}

			_availableUpdate = result;
			var notes = FormatReleaseNotes(release.NotesMarkdown);
			ShowUpdateDetails(release.Version.ToString(), $"Current version: {installed}\nNew version: {release.Version}", notes);
			GameLogger.Log("update_available", new { installedVersion = installed?.ToString(), applicationVersion = appVersion, candidateVersion = release.Version.ToString(), channel = UpdateChannel, feedSource = RepositoryUrl });
			SetStatus($"Wodra {release.Version} is available.", showRetry: false);
			_detailsWindow?.PopupCentered();
		}
		catch (Exception ex)
		{
			_availableUpdate = null;
			GameLogger.Log("update_check_failed", new
			{
				exceptionType = ex.GetType().Name,
				message = ex.Message,
				installedPackageVersion = installed?.ToString(),
				applicationVersion = appVersion,
				channel = UpdateChannel,
				feedSource = RepositoryUrl
			});
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
			Size = new Vector2I(500, 360),
			MinSize = new Vector2I(340, 270),
			Exclusive = true,
			Visible = false
		};
		var margin = new MarginContainer();
		margin.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
		foreach (var edge in new[] { "left", "right", "top", "bottom" }) margin.AddThemeConstantOverride($"margin_{edge}", 16);
		var layout = new VBoxContainer();
		_detailsTitle = new Label { Text = "Wodra Update", AutowrapMode = TextServer.AutowrapMode.WordSmart, SizeFlagsHorizontal = SizeFlags.ExpandFill };
		_detailsTitle.AddThemeFontSizeOverride("font_size", 20);
		_detailsStatus = new Label { Text = "", AutowrapMode = TextServer.AutowrapMode.WordSmart, SizeFlagsHorizontal = SizeFlags.ExpandFill };
		_notes = new RichTextLabel { FitContent = false, ScrollActive = true, SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill, BbcodeEnabled = false, AutowrapMode = TextServer.AutowrapMode.WordSmart };
		_progress = new ProgressBar { MinValue = 0, MaxValue = 100, Value = 0, Visible = false, SizeFlagsHorizontal = SizeFlags.ExpandFill };
		var actions = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End };
		var later = new Button { Text = "Later" };
		later.Pressed += () => _detailsWindow?.Hide();
		_installButton = new Button { Text = "Download and Install" };
		_installButton.Pressed += DownloadAndApplyUpdate;
		actions.AddChild(later);
		actions.AddChild(_installButton);
		layout.AddChild(_detailsTitle);
		layout.AddChild(_detailsStatus);
		layout.AddChild(_notes);
		layout.AddChild(_progress);
		layout.AddChild(actions);
		margin.AddChild(layout);
		_detailsWindow.AddChild(margin);
		AddChild(_detailsWindow);
	}

	private void ShowUpdateDetails(string version, string status, string notes)
	{
		if (_detailsTitle is not null) _detailsTitle.Text = $"Wodra update {version}";
		if (_detailsStatus is not null) _detailsStatus.Text = status;
		if (_notes is not null) _notes.Text = notes;
	}

	private async void DownloadAndApplyUpdate()
	{
		if (_manager is null || (_availableUpdate is null && _pendingUpdate is null) || _downloading) return;
		if (GameBootstrap.Mode != GameBootstrap.SessionMode.None || !IsVisibleInTree())
		{
			SetStatus("Finish the current ride or replay before applying updates.", showRetry: false);
			_detailsWindow?.Hide();
			return;
		}

		_downloading = true;
		if (_installButton is not null) { _installButton.Disabled = true; _installButton.Text = _pendingUpdate is not null ? "Applying…" : "Starting download…"; }
		if (_checkButton is not null) _checkButton.Disabled = true;
		if (_progress is not null) { _progress.Value = 0; _progress.Visible = _availableUpdate is not null; }
		if (_detailsStatus is not null) _detailsStatus.Text = _pendingUpdate is not null ? "Applying the downloaded update…" : "Connecting to the update feed…";
		if (_detailsTitle is not null) _detailsTitle.Text = _pendingUpdate is not null ? "Applying update…" : "Preparing download…";
		SetStatus(_pendingUpdate is not null ? "Applying update and restarting Wodra…" : "Connecting to download…", showRetry: false);
		var release = _availableUpdate?.TargetFullRelease;
		GameLogger.Log("update_download_started", new { version = _pendingUpdate?.Version.ToString() ?? release?.Version.ToString(), channel = UpdateChannel, feedSource = RepositoryUrl });

		try
		{
			await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
			if (_pendingUpdate is not null)
			{
				GameLogger.Log("update_apply_started", new { version = _pendingUpdate.Version.ToString() });
				_manager.ApplyUpdatesAndRestart(_pendingUpdate, Array.Empty<string>());
				GameLogger.Log("update_apply_returned_unexpectedly", new { version = _pendingUpdate.Version.ToString() });
				throw new InvalidOperationException("Velopack returned without restarting after applying the pending update.");
			}

			var info = _availableUpdate!;
			await _manager.DownloadUpdatesAsync(info, value => CallDeferred(MethodName.SetDownloadProgress, value));
			GameLogger.Log("update_download_complete", new { version = info.TargetFullRelease.Version.ToString() });
			if (GameBootstrap.Mode != GameBootstrap.SessionMode.None)
			{
				_staged = true;
				SetStatus("Update downloaded. It will wait until you finish riding.", showRetry: false);
				if (_detailsStatus is not null) _detailsStatus.Text = "Download complete. Applying is paused until your ride or replay has ended.";
				if (_detailsTitle is not null) _detailsTitle.Text = "Update downloaded";
				if (_installButton is not null) { _installButton.Text = "Apply and Restart"; _installButton.Disabled = true; }
				GameLogger.Log("update_download_staged_during_ride", new { version = info.TargetFullRelease.Version.ToString() });
				return;
			}

			if (GameBootstrap.Mode != GameBootstrap.SessionMode.None)
				throw new InvalidOperationException("An active ride or replay prevents applying the update.");
			_detailsWindow?.Hide();
			SetStatus("Applying update and restarting Wodra…", showRetry: false);
			GameLogger.Log("update_apply_started", new { version = info.TargetFullRelease.Version.ToString() });
			_manager.ApplyUpdatesAndRestart(info.TargetFullRelease, Array.Empty<string>());
			GameLogger.Log("update_apply_returned_unexpectedly", new { version = info.TargetFullRelease.Version.ToString() });
			throw new InvalidOperationException("Velopack returned without restarting after applying the update.");
		}
		catch (Exception ex)
		{
			GameLogger.Log("update_download_or_apply_failed", new { exceptionType = ex.GetType().Name, message = ex.Message, version = _pendingUpdate?.Version.ToString() ?? release?.Version.ToString() });
			if (_detailsTitle is not null) _detailsTitle.Text = "Update could not be installed";
			if (_detailsStatus is not null) _detailsStatus.Text = $"Could not install the update. Your current Wodra install and rider data are unchanged.\n{SummarizeError(ex.Message)} See the Velopack log for details.";
			if (_installButton is not null) { _installButton.Text = _pendingUpdate is not null ? "Retry Apply and Restart" : "Retry Download and Install"; _installButton.Disabled = false; }
			// The failed operation and its Retry Apply/Download action live in the details
			// window. Do not put a misleading update-check Retry beside the startup menu.
			SetStatus("Update failed. Retry from the update window, or continue riding.", showRetry: false);
			if (_detailsWindow is not null && !_detailsWindow.Visible) _detailsWindow.PopupCentered();
		}
		finally
		{
			_downloading = false;
			if (_checkButton is not null) _checkButton.Disabled = false;
			if (_installButton is not null && _detailsWindow?.Visible == true && GameBootstrap.Mode == GameBootstrap.SessionMode.None)
				_installButton.Disabled = false;
		}
	}

	public void SetDownloadProgress(int percentage)
	{
		if (_progress is not null) _progress.Value = Math.Clamp(percentage, 0, 100);
		if (_detailsStatus is not null) _detailsStatus.Text = $"Downloading update… {Math.Clamp(percentage, 0, 100)}%";
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

	private static string GetApplicationVersion()
	{
		var assembly = Assembly.GetExecutingAssembly();
		return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
			?? assembly.GetName().Version?.ToString()
			?? "unknown";
	}

	private static string FormatReleaseNotes(string? markdown)
	{
		if (string.IsNullOrWhiteSpace(markdown)) return "No release notes were provided for this update.";
		var lines = markdown.Replace("\r", string.Empty).Split('\n');
		for (var i = 0; i < lines.Length; i++)
		{
			var line = lines[i].Trim();
			line = Regex.Replace(line, "^#{1,6}\\s*", string.Empty);
			line = Regex.Replace(line, "^[-*+]\\s+", "• ");
			line = Regex.Replace(line, "\\*\\*(.*?)\\*\\*", "$1");
			line = Regex.Replace(line, "`([^`]*)`", "$1");
			lines[i] = line;
		}
		var formatted = string.Join("\n", lines).Trim();
		return formatted.Length == 0 ? "No release notes were provided for this update." : formatted;
	}

	private static string SummarizeError(string message)
	{
		var compact = Regex.Replace(message, "\\s+", " ").Trim();
		return compact.Length <= 180 ? compact : compact[..177] + "…";
	}
}
