using Godot;
using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using IndoorCycling.Core.Workouts;
using IndoorCycling.Trainer.Abstractions;

public partial class WorkoutSelectionPanelController : Control
{
    private RideSessionNode? _session;
    private RouteSelectionPanelController? _routes;
    private StartPanelController? _start;
    private PanelContainer? _panel;
    private VBoxContainer? _rows;
    private Label? _title, _description, _duration, _profile, _status, _importWarnings;
    private OptionButton? _selector;
    private SpinBox? _ftp;
    private CheckButton? _erg;
    private Button? _startButton;
    private FileDialog? _fileDialog;
    private readonly System.Collections.Generic.List<WorkoutDefinition> _workouts = WorkoutCatalog.BuiltIns.ToList();
    private int _selectedIndex;

    public override void _Ready()
    {
        _session = GetNodeOrNull<RideSessionNode>("../RideSessionNode");
        _routes = GetNodeOrNull<RouteSelectionPanelController>("../RouteSelectionPanel");
        _start = GetNodeOrNull<StartPanelController>("../StartPanel");
        BuildUi(); Hide();
    }

    private void BuildUi()
    {
        _panel = new PanelContainer { Name = "WorkoutSelectionPanel", CustomMinimumSize = Vector2.Zero,
            AnchorLeft = .04f, AnchorRight = .96f, AnchorTop = .05f, AnchorBottom = .95f };
        AddChild(_panel);
        var margin = new MarginContainer { Name = "MarginContainer" }; margin.AddThemeConstantOverride("margin_left", 22); margin.AddThemeConstantOverride("margin_right", 22); margin.AddThemeConstantOverride("margin_top", 16); margin.AddThemeConstantOverride("margin_bottom", 16); _panel.AddChild(margin);
        var root = new VBoxContainer { Name = "VBoxContainer" }; root.AddThemeConstantOverride("separation", 10); margin.AddChild(root);
        var header = new HBoxContainer(); root.AddChild(header);
        var heading = new Label { Text = "Workouts", SizeFlagsHorizontal = SizeFlags.ExpandFill }; heading.AddThemeFontSizeOverride("font_size", 24); header.AddChild(heading);
        var routes = new Button { Text = "Routes" }; routes.Pressed += () => { Hide(); _routes?.ShowRouteSelection(); }; header.AddChild(routes);
        var options = new Button { Text = "Options" }; options.Pressed += () => { Hide(); _start?.ShowOptions(false, true); }; header.AddChild(options);
        var content = new HBoxContainer { Name = "Content", SizeFlagsVertical = SizeFlags.ExpandFill }; content.AddThemeConstantOverride("separation", 20); root.AddChild(content);
        var listColumn = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
        content.AddChild(listColumn); listColumn.AddChild(new Label { Text = "Choose a workout" });
        var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled }; listColumn.AddChild(scroll);
        _rows = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill }; _rows.AddThemeConstantOverride("separation", 4); scroll.AddChild(_rows);
        var previewScroll = new ScrollContainer { Name = "PreviewScroll", SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        content.AddChild(previewScroll);
        var preview = new VBoxContainer { Name = "Preview", SizeFlagsHorizontal = SizeFlags.ExpandFill }; preview.AddThemeConstantOverride("separation", 7); previewScroll.AddChild(preview);
        _title = new Label { Name = "WorkoutTitle", Text = "Workout", AutowrapMode = TextServer.AutowrapMode.WordSmart }; _title.AddThemeFontSizeOverride("font_size", 21); preview.AddChild(_title);
        _duration = new Label(); preview.AddChild(_duration);
        _description = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, SizeFlagsVertical = SizeFlags.ExpandFill }; preview.AddChild(_description);
        _profile = new Label { Text = "Profile FTP" }; preview.AddChild(_profile);
        _ftp = new SpinBox { Name = "WorkoutFtp", MinValue = 0, MaxValue = 1000, Step = 1, Rounded = true, CustomMinimumSize = new Vector2(150, 40), TooltipText = "Enter 50–1000 W, then press Save FTP." };
        _ftp.ValueChanged += _ => UpdatePreview(); preview.AddChild(_ftp);
        var saveFtp = new Button { Text = "Save FTP", CustomMinimumSize = new Vector2(105, 38) }; saveFtp.Pressed += () => SaveFtp(_ftp.Value); preview.AddChild(saveFtp);
        _erg = new CheckButton { Name = "WorkoutErgToggle", Text = "Use ERG target control" }; _erg.Toggled += _ => UpdatePreview(); preview.AddChild(_erg);
        _status = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart }; _status.AddThemeFontSizeOverride("font_size", 12); preview.AddChild(_status);
        _importWarnings = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart }; _importWarnings.AddThemeFontSizeOverride("font_size", 12); preview.AddChild(_importWarnings);
        var footer = new HBoxContainer { Name = "Footer" }; footer.AddThemeConstantOverride("separation", 8); root.AddChild(footer);
        var back = new Button { Text = "Back", CustomMinimumSize = new Vector2(90, 42) }; back.Pressed += () => { Hide(); _start?.Show(); }; footer.AddChild(back);
        var import = new Button { Text = "Import .zwo" }; import.Pressed += () => _fileDialog?.PopupCenteredRatio(.7f); footer.AddChild(import);
        _startButton = new Button { Name = "StartWorkout", Text = "Start Workout", CustomMinimumSize = new Vector2(170, 44), SizeFlagsHorizontal = SizeFlags.ShrinkEnd };
        _startButton.Pressed += StartSelectedWorkout; footer.AddChild(_startButton);
        _selector = new OptionButton { Visible = false }; // selected ID remains held by this screen across Options navigation.
        _fileDialog = new FileDialog { Name = "WorkoutFileDialog", Title = "Import Zwift workout", FileMode = FileDialog.FileModeEnum.OpenFile,
            Access = FileDialog.AccessEnum.Filesystem, Filters = ["*.zwo ; Zwift workout"] };
        _fileDialog.FileSelected += ImportZwo; AddChild(_fileDialog);
        LoadImportedWorkouts(); RefreshRows(); UpdatePreview();
    }

    public void ShowSelection()
    {
        if (_ftp is not null)
        {
            var id = GameBootstrap.Settings.ActiveProfileId;
            _ftp.Value = id is { } profile && GameBootstrap.Settings.WorkoutFtpWattsByProfile.TryGetValue(profile, out var value) ? value : 0;
            var hasConfigured = id is { } current && GameBootstrap.Settings.WorkoutFtpWattsByProfile.ContainsKey(current);
            if (!hasConfigured) _ftp.Value = 0;
            _profile!.Text = $"FTP for {GameBootstrap.Settings.Rider.Name} · {(hasConfigured ? "saved" : "set before FTP-based workouts")}";
        }
        RefreshRows(); UpdatePreview(); Show(); MoveToFront();
    }

    private void RefreshRows()
    {
        if (_rows is null) return; _rows.QueueFreeChildren();
        for (var i = 0; i < _workouts.Count; i++)
        {
            var index = i; var item = _workouts[index];
            var button = new Button { Text = $"{item.Name}\n{FormatDuration(item.DurationSeconds)} · {item.Steps.Count} intervals",
                CustomMinimumSize = new Vector2(0, 54), SizeFlagsHorizontal = SizeFlags.ExpandFill, Alignment = HorizontalAlignment.Left,
                FocusMode = Control.FocusModeEnum.All };
            button.Pressed += () => { _selectedIndex = index; UpdatePreview(); };
            _rows.AddChild(button);
        }
    }

    private WorkoutDefinition Selected => _workouts[Math.Clamp(_selectedIndex, 0, _workouts.Count - 1)];

    private void UpdatePreview()
    {
        if (_title is null || _workouts.Count == 0) return;
        var definition = Selected;
        var ftp = (int)(_ftp?.Value ?? 200);
        var percentage = definition.Steps.Any(step => step.StartTarget.Unit == WorkoutTargetUnit.FtpPercent || step.EndTarget?.Unit == WorkoutTargetUnit.FtpPercent);
        string? validationError = null;
        try { definition.Validate(percentage ? (HasFtpConfigured() ? ftp : null) : null); }
        catch (Exception ex) { validationError = ex.Message; }
        var capabilities = (GameBootstrap.TrainerClient as IWorkoutTrainerControl)?.WorkoutControlStatus ??
            new WorkoutTrainerControlStatus(WorkoutTrainerControlKind.TargetsOnly, "Trainer capability unavailable; target guidance only.");
        _title.Text = definition.Name; _description!.Text = definition.Description;
        _duration!.Text = $"{FormatDuration(definition.DurationSeconds)} · {definition.Steps.Count} intervals";
        var rows = new System.Collections.Generic.List<string>();
        foreach (var step in definition.Steps)
        {
            var start = step.StartTarget.Unit == WorkoutTargetUnit.FtpPercent ? $"{step.StartTarget.Value * 100:0}% FTP" : $"{step.StartTarget.Value:0} W";
            var target = step.EndTarget is { } end
                ? $"{start} → {(end.Unit == WorkoutTargetUnit.FtpPercent ? $"{end.Value * 100:0}% FTP" : $"{end.Value:0} W")}" : start;
            rows.Add($"{FormatDuration(step.DurationSeconds)}  {step.Name} · {target}{(step.CadenceCueRpm is { } cadence ? $" · {cadence} rpm" : "")}");
        }
        _description.Text += "\n\n" + string.Join("\n", rows);
        _erg!.Disabled = !capabilities.CanControlPower; _erg.TooltipText = capabilities.Message;
        _status!.Text = validationError ?? (_erg.ButtonPressed && capabilities.CanControlPower
            ? $"ERG available · {capabilities.Message}" : capabilities.CanControlPower
                ? "Targets only · trainer resistance remains at its current setting."
                : $"Targets only · trainer resistance remains at its current setting. {capabilities.Message}");
        _startButton!.Disabled = validationError is not null;
    }

    private bool HasFtpConfigured() => GameBootstrap.Settings.ActiveProfileId is { } id && GameBootstrap.Settings.WorkoutFtpWattsByProfile.ContainsKey(id);
    private void SaveFtp(double value)
    {
        if (GameBootstrap.Settings.ActiveProfileId is not { } id) return;
        if (value is < 50 or > 1000) { if (_profile is not null) _profile.Text = "Enter an FTP from 50 to 1000 W, then save it."; UpdatePreview(); return; }
        GameBootstrap.Settings.WorkoutFtpWattsByProfile[id] = (int)Math.Clamp(Math.Round(value), 50, 1000);
        GameBootstrap.Settings.Save();
        if (_profile is not null) _profile.Text = $"FTP for {GameBootstrap.Settings.Rider.Name} · saved";
        UpdatePreview();
    }

    private async void StartSelectedWorkout()
    {
        if (_session is null) return;
        var needsFtp = Selected.Steps.Any(step => step.StartTarget.Unit == WorkoutTargetUnit.FtpPercent || step.EndTarget?.Unit == WorkoutTargetUnit.FtpPercent);
        if (needsFtp && _ftp?.Value is >= 50 and <= 1000) SaveFtp(_ftp.Value);
        var ftp = GameBootstrap.Settings.ActiveProfileId is { } id && GameBootstrap.Settings.WorkoutFtpWattsByProfile.TryGetValue(id, out var saved) ? saved : (int?)null;
        if (needsFtp && ftp is null) { _status!.Text = "Enter an FTP to start this workout."; return; }
        Hide(); GetNodeOrNull<HudController>("../HudController")?.Show();
        var success = await _session.StartWorkoutSessionAsync(Selected, ftp, _erg?.ButtonPressed == true);
        if (!success) { GetNodeOrNull<HudController>("../HudController")?.Hide(); Show(); _status!.Text = "Trainer is not ready. Connect a trainer, or choose the dummy trainer from the start screen."; }
    }

    private void ImportZwo(string path)
    {
        try
        {
            if (new FileInfo(path).Length > ZwoWorkoutImporter.MaximumFileBytes)
                throw new InvalidDataException("Workout file exceeds 512 KiB.");
            var content = File.ReadAllBytes(path);
            var id = "zwo-" + Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant()[..16];
            using var stream = new MemoryStream(content, writable: false);
            var result = ZwoWorkoutImporter.Import(stream, id);
            if (!result.CanImport)
            {
                _importWarnings!.Text = string.Join("\n", result.Warnings.Append(result.Error ?? "Import failed.")); return;
            }
            var localFolder = ProjectSettings.GlobalizePath("user://Workouts"); Directory.CreateDirectory(localFolder);
            File.WriteAllBytes(Path.Combine(localFolder, id + ".zwo"), content);
            var priorIndex = _workouts.FindIndex(item => item.Id == id);
            if (priorIndex >= 0) _workouts[priorIndex] = result.Workout!; else _workouts.Add(result.Workout!);
            _selectedIndex = _workouts.FindIndex(item => item.Id == id);
            _importWarnings!.Text = result.Warnings.Count == 0 ? "Imported locally. Unsupported timed steps are rejected." : string.Join("\n", result.Warnings);
            RefreshRows(); UpdatePreview();
        }
        catch (Exception ex) { _importWarnings!.Text = $"Could not read workout file: {ex.Message}"; }
    }

    private void LoadImportedWorkouts()
    {
        try
        {
            var localFolder = ProjectSettings.GlobalizePath("user://Workouts");
            if (!Directory.Exists(localFolder)) return;
            foreach (var path in Directory.EnumerateFiles(localFolder, "*.zwo").Take(100))
            {
                var info = new FileInfo(path); if (info.Length > ZwoWorkoutImporter.MaximumFileBytes) continue;
                var content = File.ReadAllBytes(path); var id = "zwo-" + Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant()[..16];
                var imported = ZwoWorkoutImporter.Import(new MemoryStream(content, writable: false), id);
                if (imported.Workout is { } workout && _workouts.All(item => item.Id != id)) _workouts.Add(workout);
            }
        }
        catch (Exception ex) { GameLogger.Log("workout_imports_load_failed", new { message = ex.Message }); }
    }

    private static string FormatDuration(double seconds) => TimeSpan.FromSeconds(seconds).ToString(seconds >= 3600 ? @"h\:mm" : @"m\:ss");
}

internal static class WorkoutUiChildrenExtensions
{
    public static void QueueFreeChildren(this Node parent)
    { foreach (var child in parent.GetChildren()) { parent.RemoveChild(child); child.QueueFree(); } }
}
