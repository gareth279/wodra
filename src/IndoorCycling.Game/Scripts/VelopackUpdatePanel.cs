using Godot;
using System;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

/// <summary>Startup/Settings presenter for the application-owned updater.</summary>
public partial class VelopackUpdatePanel : Control
{
    private readonly bool _showAvailableUpdateAutomatically;
    private readonly bool _showChannelSelector;
    private readonly VelopackUpdateService? _providedService;
    private VelopackUpdateService? _service;
    private Label? _status;
    private Label? _identity;
    private Label? _detailsTitle;
    private Label? _detailsStatus;
    private RichTextLabel? _notes;
    private ProgressBar? _progress;
    private Button? _checkButton;
    private Button? _installButton;
    private Button? _cancelButton;
    private Window? _detailsWindow;
    private bool _attached;
    private bool _operationDetailsRequested;

    public VelopackUpdatePanel(bool showAvailableUpdateAutomatically = false, VelopackUpdateService? service = null)
    {
        _showAvailableUpdateAutomatically = showAvailableUpdateAutomatically;
        _providedService = service;
    }

    public VelopackUpdatePanel(bool showAvailableUpdateAutomatically, bool showChannelSelector, VelopackUpdateService? service = null)
    {
        _showAvailableUpdateAutomatically = showAvailableUpdateAutomatically;
        _showChannelSelector = showChannelSelector;
        _providedService = service;
    }

    public void Attach()
    {
        if (_attached) return;
        _attached = true;
        _service = _providedService ?? GameBootstrap.UpdateService;
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        // A plain Control does not derive its minimum size from its children.
        // Settings embeds this presenter in a VBoxContainer, so reserve space
        // explicitly or its status row can collapse to zero height and appear
        // to be missing even though the updater service is running.
        CustomMinimumSize = new Vector2(0, _showChannelSelector ? 108 : 56);

        var layout = new VBoxContainer { Name = "UpdatePanelLayout", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        layout.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(layout);
        _identity = new Label { Name = "InstalledUpdateIdentity", AutowrapMode = TextServer.AutowrapMode.WordSmart };
        _identity.AddThemeFontSizeOverride("font_size", 12);
        layout.AddChild(_identity);

        if (_showChannelSelector)
        {
            var channelRow = new HBoxContainer { Name = "UpdateChannelRow", SizeFlagsHorizontal = SizeFlags.ExpandFill };
            channelRow.AddChild(new Label { Text = "Update channel", SizeFlagsHorizontal = SizeFlags.ExpandFill, VerticalAlignment = VerticalAlignment.Center });
            var picker = new OptionButton { Name = "UpdateChannelSelector", CustomMinimumSize = new Vector2(150, 36) };
            picker.AddItem("Stable", 0);
            picker.AddItem("Alpha", 1);
            picker.SetItemMetadata(0, UpdateChannelPolicy.Stable);
            picker.SetItemMetadata(1, UpdateChannelPolicy.Alpha);
            picker.Select(GameBootstrap.Settings.UpdateChannel == UpdateChannelPolicy.Stable ? 0 : 1);
            picker.ItemSelected += index => _ = ChangeChannelAsync((string)picker.GetItemMetadata((int)index));
            channelRow.AddChild(picker);
            layout.AddChild(channelRow);
            layout.AddChild(new Label
            {
                Name = "UpdateChannelDescription",
                Text = "Stable: Tested releases recommended for normal use.\nAlpha: Development releases with newer features that may have bugs.",
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
                SizeFlagsHorizontal = SizeFlags.ExpandFill
            });
        }

        var row = new HBoxContainer
        {
            Name = "UpdateStatusRow",
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 36),
            Alignment = BoxContainer.AlignmentMode.Begin
        };
        _status = new Label
        {
            Name = "UpdateStatus",
            Text = "Checking for updates…",
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            AutowrapMode = TextServer.AutowrapMode.WordSmart
        };
        _status.AddThemeFontSizeOverride("font_size", 13);
        row.AddChild(_status);
        _checkButton = new Button { Name = "CheckForUpdatesButton", Text = "Check for Updates", CustomMinimumSize = new Vector2(0, 36) };
        _checkButton.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        _checkButton.Pressed += CheckOrReview;
        row.AddChild(_checkButton);
        layout.AddChild(row);

        BuildDetailsWindow();
        if (_service is not null)
        {
            _service.StateChanged += OnStateChanged;
            ApplyState(_service.Snapshot, openWhenAvailable: false);
        }
        else
        {
            _status.Text = "Update service is unavailable. You can still ride.";
            _checkButton.Disabled = true;
        }
    }

    public void OnStartupShown()
    {
        if (_service is null) return;
        var snapshot = _service.Snapshot;
        ApplyState(snapshot, openWhenAvailable: true);
    }

    private void OnStateChanged(UpdaterSnapshot snapshot)
    {
        if (!IsInsideTree()) return;
        ApplyState(snapshot, openWhenAvailable: _showAvailableUpdateAutomatically && IsVisibleInTree());
    }

    private async Task ChangeChannelAsync(string channel)
    {
        if (_service is null || channel == GameBootstrap.Settings.UpdateChannel) return;
        GameBootstrap.Settings.UpdateChannel = UpdateChannelPolicy.Normalize(channel);
        try
        {
            GameBootstrap.Settings.Save();
            _service.LogUiEvent("update_channel_changed", new { channel = GameBootstrap.Settings.UpdateChannel });
            var result = await _service.ChangeChannelAsync(GameBootstrap.Settings.UpdateChannel);
            if (_status is not null) _status.Text = result.Message;
        }
        catch (Exception ex)
        {
            if (_status is not null) _status.Text = "Could not reconfigure update checking right now. Wodra remains available for riding.";
            GD.PushWarning($"Could not reconfigure the update feed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void ApplyState(UpdaterSnapshot snapshot, bool openWhenAvailable)
    {
        if (_status is not null) _status.Text = snapshot.Message;
        if (_identity is not null) _identity.Text = $"Installed Wodra {snapshot.CurrentVersion} · Selected channel: {snapshot.Channel}";
        if (_checkButton is not null)
        {
            _checkButton.Text = snapshot.Status switch
            {
                UpdaterStatus.Available or UpdaterStatus.Staged => "Review Update",
                UpdaterStatus.Failed => "Retry",
                _ => "Check for Updates"
            };
            _checkButton.Disabled = snapshot.Status is UpdaterStatus.Checking or UpdaterStatus.Downloading or UpdaterStatus.Applying;
        }

        if (_detailsStatus is not null)
            _detailsStatus.Text = $"Installed: {snapshot.CurrentVersion} · Channel: {snapshot.Channel}\n{snapshot.Message}";
        if (_detailsTitle is not null)
            _detailsTitle.Text = snapshot.Status switch
            {
                UpdaterStatus.Available => $"Wodra update {snapshot.Offer?.Version}",
                UpdaterStatus.Staged => "Update downloaded",
                UpdaterStatus.Downloading => "Downloading update…",
                UpdaterStatus.Applying => "Applying update…",
                UpdaterStatus.Failed => "Update needs attention",
                _ => "Wodra Updates"
            };
        if (_notes is not null)
        {
            _notes.Text = snapshot.Offer is null
                ? "Update checks are optional. Wodra remains available if the network cannot be reached."
                : FormatReleaseNotes(snapshot.Offer.ReleaseNotes);
        }

        var downloading = snapshot.Status == UpdaterStatus.Downloading;
        if (_progress is not null)
        {
            _progress.Visible = downloading || snapshot.Status == UpdaterStatus.Staged && snapshot.ProgressPercent > 0;
            _progress.Value = Math.Clamp(snapshot.ProgressPercent, 0, 100);
        }
        if (_installButton is not null)
        {
            _installButton.Visible = snapshot.Offer is not null &&
                snapshot.Status is UpdaterStatus.Available or UpdaterStatus.Staged or UpdaterStatus.Failed;
            _installButton.Text = snapshot.Status switch
            {
                UpdaterStatus.Staged => "Apply and Restart",
                UpdaterStatus.Failed when snapshot.HasStagedPackage => "Retry Apply and Restart",
                UpdaterStatus.Failed => "Retry Download and Install",
                _ => "Download and Install"
            };
            _installButton.Disabled = snapshot.Status is UpdaterStatus.Checking or UpdaterStatus.Downloading or UpdaterStatus.Applying;
        }
        if (_cancelButton is not null) _cancelButton.Visible = downloading;

        if (_operationDetailsRequested && snapshot.Status is UpdaterStatus.Downloading or UpdaterStatus.Staged or UpdaterStatus.Failed)
            _detailsWindow?.PopupCentered();
        else if (openWhenAvailable && snapshot.Status is UpdaterStatus.Available or UpdaterStatus.Staged)
            _detailsWindow?.PopupCentered();
    }

    private void CheckOrReview()
    {
        if (_service is null)
        {
            GameLogger.Log("update_ui_action_ignored", new { action = "check", reason = "service-unavailable" });
            return;
        }
        _service.LogUiEvent("update_ui_action", new { action = "check-or-review", status = _service.Snapshot.Status.ToString() });
        var snapshot = _service.Snapshot;
        if (snapshot.Status is UpdaterStatus.Available or UpdaterStatus.Staged)
        {
            _operationDetailsRequested = true;
            _detailsWindow?.PopupCentered();
            return;
        }
        _ = Observe(_service.CheckForUpdatesAsync());
    }

    private async Task Observe(Task<UpdaterSnapshot> operation)
    {
        try { await operation; }
        catch (Exception ex) { GD.PushWarning($"Update operation was observed by the UI: {ex.GetType().Name}: {ex.Message}"); }
    }

    private void BuildDetailsWindow()
    {
        _detailsWindow = new Window
        {
            Name = "UpdateDetailsWindow",
            Title = "Wodra Updates",
            Size = new Vector2I(520, 380),
            MinSize = new Vector2I(360, 280),
            Exclusive = true,
            Visible = false
        };
        var margin = new MarginContainer();
        margin.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        foreach (var edge in new[] { "left", "right", "top", "bottom" })
            margin.AddThemeConstantOverride($"margin_{edge}", 16);
        var layout = new VBoxContainer();
        _detailsTitle = new Label { Text = "Wodra Updates", AutowrapMode = TextServer.AutowrapMode.WordSmart };
        _detailsTitle.AddThemeFontSizeOverride("font_size", 20);
        _detailsStatus = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        _notes = new RichTextLabel { FitContent = false, ScrollActive = true, SizeFlagsVertical = SizeFlags.ExpandFill, BbcodeEnabled = false, AutowrapMode = TextServer.AutowrapMode.WordSmart };
        _progress = new ProgressBar { MinValue = 0, MaxValue = 100, Value = 0, Visible = false, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        var actions = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End };
        _cancelButton = new Button { Text = "Cancel Download", Visible = false };
        _cancelButton.Pressed += () => _service?.CancelDownload();
        var later = new Button { Text = "Later" };
        later.Pressed += () => { _operationDetailsRequested = false; _detailsWindow?.Hide(); };
        _installButton = new Button { Name = "InstallUpdateButton", Text = "Download and Install", Visible = false };
        _installButton.Pressed += InstallOrRetry;
        actions.AddChild(_cancelButton);
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

    private void InstallOrRetry()
    {
        if (_service is null)
        {
            GameLogger.Log("update_ui_action_ignored", new { action = "download-or-apply", reason = "service-unavailable" });
            return;
        }
        _service.LogUiEvent("update_ui_action", new { action = "download-or-apply", status = _service.Snapshot.Status.ToString() });
        _operationDetailsRequested = true;
        var snapshot = _service.Snapshot;
        if (snapshot.HasStagedPackage)
        {
            _service.ApplyStagedUpdate();
            return;
        }

        _ = Observe(_service.DownloadAndInstallAsync());
    }

    public override void _ExitTree()
    {
        if (_service is not null) _service.StateChanged -= OnStateChanged;
        base._ExitTree();
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
}
