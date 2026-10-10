using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using IndoorCycling.Core.Models;
using IndoorCycling.Core.Simulation;

public partial class RideHistoryPanelController : Control
{
    private Control? _startPanel;
    private RouteSelectionPanelController? _routeSelection;
    private HudController? _hud;
    private PanelContainer _panel = null!;
    private Label _title = null!;
    private Label _detail = null!;
    private ItemList _list = null!;
    private Button _viewButton = null!;
    private Button _ghostButton = null!;
    private Button _doneButton = null!;
    private List<RideActivity> _activities = [];
    private RideActivity? _selected;
    private bool _summaryMode;
    private bool _returnToRouteSelection;

    public override void _Ready()
    {
        _startPanel = GetNodeOrNull<Control>("../StartPanel");
        _routeSelection = GetNodeOrNull<RouteSelectionPanelController>("../RouteSelectionPanel");
        _hud = GetNodeOrNull<HudController>("../HudController");
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _panel = new PanelContainer { CustomMinimumSize = new Vector2(820, 580), AnchorLeft = .5f, AnchorTop = .5f, AnchorRight = .5f, AnchorBottom = .5f, OffsetLeft = -410, OffsetTop = -290, OffsetRight = 410, OffsetBottom = 290 };
        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", 18); margin.AddThemeConstantOverride("margin_right", 18);
        margin.AddThemeConstantOverride("margin_top", 18); margin.AddThemeConstantOverride("margin_bottom", 18);
        var column = new VBoxContainer();
        _title = new Label { Text = "Ride History" }; column.AddChild(_title);
        var body = new HBoxContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        _list = new ItemList { CustomMinimumSize = new Vector2(390, 430), SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        _list.ItemSelected += OnItemSelected; body.AddChild(_list);
        _detail = new Label { Text = "Select a ride to see details.", AutowrapMode = TextServer.AutowrapMode.WordSmart, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        body.AddChild(_detail); column.AddChild(body);
        var buttons = new HBoxContainer();
        _viewButton = new Button { Text = "View selected ride" }; _viewButton.Pressed += ShowSelectedSummary; buttons.AddChild(_viewButton);
        _ghostButton = new Button { Text = "Ride against ghost", Visible = false }; _ghostButton.Pressed += StartAgainstGhost; buttons.AddChild(_ghostButton);
        _doneButton = new Button { Text = "Done / Return to start" }; _doneButton.Pressed += ClosePanel; buttons.AddChild(_doneButton);
        column.AddChild(buttons); margin.AddChild(column); _panel.AddChild(margin); AddChild(_panel);
        Hide();
    }

    public async void ShowHistory()
    {
        _summaryMode = false; _returnToRouteSelection = false; _selected = null; _list.Visible = true; _list.Clear(); _title.Text = "Ride History";
        _detail.Text = "Loading ride history…"; _viewButton.Visible = false; _ghostButton.Visible = false;
        _startPanel?.Hide(); Show(); MoveToFront();
        try
        {
            await GameBootstrap.GetPersistentRiderAsync();
            var profile = await GameBootstrap.GetPersistentRiderAsync();
            _title.Text = $"Ride History — {profile.Name}";
            _activities = (await GameBootstrap.RideHistory!.GetRecentActivitiesForProfileAsync(profile.Id, 100)).ToList();
            if (_activities.Count == 0) _detail.Text = "No completed rides yet.";
            foreach (var activity in _activities)
            {
                var local = activity.StartedAtUtc.ToLocalTime();
                _list.AddItem($"{local:yyyy-MM-dd HH:mm}  {activity.RouteName}\n{activity.DistanceMeters / 1000:F2} km · {FormatDuration(activity.DurationSeconds)} · {activity.AveragePowerWatts?.ToString("F0") ?? "—"} W avg");
            }
            if (_activities.Count > 0) _list.Select(0);
        }
        catch (Exception ex) { _detail.Text = $"Ride history could not be loaded.\n{ex.Message}"; GD.PushWarning($"Ride history load failed: {ex.Message}"); }
    }

    public async void ShowCompletedSummary(RideActivityDraft draft, Guid? activityId, string? fitRecordingError = null)
    {
        _summaryMode = true; _selected = null; _list.Visible = false; _list.Clear(); _viewButton.Visible = false; _ghostButton.Visible = false;
        _returnToRouteSelection = GameBootstrap.TrainerClient is not null;
        _startPanel?.Hide(); Show(); MoveToFront();
        _title.Text = "Ride Summary";
        var fit = fitRecordingError is not null
            ? $"FIT RECORDING ISSUE: {fitRecordingError}\nFIT: {draft.FitFilePath ?? "not saved"}"
            : string.IsNullOrWhiteSpace(draft.FitFilePath) ? "FIT: not saved" : $"FIT: {draft.FitFilePath}";
        _detail.Text = FormatSummary(draft, fit, "Screenshots: loading…");
        IReadOnlyList<RideActivityMedia> media = Array.Empty<RideActivityMedia>();
        if (activityId is { } id && GameBootstrap.RideHistory is { } repository)
        {
            try { media = await repository.GetActivityMediaAsync(id); }
            catch (Exception ex) { GD.PushWarning($"Ride media list could not be loaded: {ex.Message}"); }
        }
        if (media.Count == 0 && activityId is { } currentId && _hud is not null)
            media = _hud.CapturedScreenshots.Where(s => s.ActivityId == currentId).Select(s => new RideActivityMedia(
                Guid.NewGuid(), currentId, s.ImagePath, s.SidecarPath, s.CapturedAt.ToUniversalTime(), s.Mode.ToString(), s.MarkerNumber)).ToArray();
        var names = media.Count == 0 ? "Screenshots: 0" : $"Screenshots: {media.Count}\n" + string.Join("\n", media.Select(m => Path.GetFileName(m.ImagePath)));
        _detail.Text = FormatSummary(draft, fit, names);
    }

    private void OnItemSelected(long index)
    {
        if (index < 0 || index >= _activities.Count) return;
        _selected = _activities[(int)index]; _viewButton.Visible = true;
        _ghostButton.Visible = CanOfferGhost(_selected);
        _detail.Text = FormatHistoryDetail(_selected);
    }

    private static bool CanOfferGhost(RideActivity activity)
    {
        if (activity.Status != RideActivityStatus.Completed || RouteCatalog.GetById(activity.RouteId) is null ||
            string.IsNullOrWhiteSpace(activity.FitFilePath) || !File.Exists(activity.FitFilePath)) return false;
        try
        {
            var trace = new GhostRideTraceLoader().Load(activity.FitFilePath);
            var route = RouteCatalog.GetById(activity.RouteId)!;
            if (activity.RouteId == "dunedin-one-way-oval-gardens")
            {
                var inferredLaps = Math.Round(trace.TotalDistanceMeters / route.DistanceMeters);
                if (inferredLaps < 1 || Math.Abs(trace.TotalDistanceMeters - inferredLaps * route.DistanceMeters) > 20) return false;
            }
            return trace.TotalDistanceMeters > 0;
        }
        catch (Exception ex) { GD.PushWarning($"Ghost trace unavailable for activity {activity.Id}: {ex.Message}"); return false; }
    }

    private void StartAgainstGhost()
    {
        if (_selected is not { } activity || string.IsNullOrWhiteSpace(activity.FitFilePath)) return;
        var route = RouteCatalog.GetById(activity.RouteId);
        if (route is null) return;
        try
        {
            var trace = new GhostRideTraceLoader().Load(activity.FitFilePath);
            var session = GetNodeOrNull<RideSessionNode>("../RideSessionNode");
            if (session is null) return;
            session.SetPendingGhost(new GhostRiderStateSource(trace, route.Id), activity.Id);
            Hide();
            _routeSelection?.ShowRouteSelection();
        }
        catch (Exception ex)
        {
            GD.PushWarning($"Could not start saved-ride ghost: {ex.Message}");
            _detail.Text += "\n\nThis ride cannot be loaded as a ghost.";
            _ghostButton.Visible = false;
        }
    }

    private async void ShowSelectedSummary()
    {
        if (_selected is null) return;
        _summaryMode = true; _list.Visible = false; _title.Text = "Ride Summary"; _viewButton.Visible = false; _ghostButton.Visible = false;
        var a = _selected;
        var draft = new RideActivityDraft(a.Id, a.RiderProfileId, a.RouteId, a.RouteName, a.StartedAtUtc, a.Status,
            a.DurationSeconds, a.DistanceMeters, a.CompletedLaps, a.AverageSpeedKph, a.MaxSpeedKph,
            a.AveragePowerWatts, a.MaxPowerWatts, a.AverageCadenceRpm, a.MaxCadenceRpm, a.ElevationGainMeters, a.FitFilePath);
        IReadOnlyList<RideActivityMedia> media = Array.Empty<RideActivityMedia>();
        try { media = await GameBootstrap.RideHistory!.GetActivityMediaAsync(a.Id); } catch { }
        _detail.Text = FormatSummary(draft, a.FitFilePath is null ? "FIT: not saved" : $"FIT: {a.FitFilePath}",
            media.Count == 0 ? "Screenshots: 0" : $"Screenshots: {media.Count}\n" + string.Join("\n", media.Select(m => Path.GetFileName(m.ImagePath))));
    }

    private void ClosePanel()
    {
        Hide();
        if (_summaryMode && _returnToRouteSelection) _routeSelection?.ShowRouteSelection();
        else _startPanel?.Show();
    }

    private static string FormatSummary(RideActivityDraft a, string fit, string media) =>
        $"{a.RouteName}\n{a.StartedAtUtc.ToLocalTime():f}\n\n" +
        $"Duration: {FormatDuration(a.DurationSeconds)}\nDistance: {a.DistanceMeters / 1000:F2} km\nLaps: {a.CompletedLaps}\n" +
        $"Average speed: {a.AverageSpeedKph?.ToString("F1") ?? "—"} km/h  ·  Max: {a.MaxSpeedKph?.ToString("F1") ?? "—"} km/h\n" +
        $"Average power: {a.AveragePowerWatts?.ToString("F0") ?? "—"} W  ·  Max: {a.MaxPowerWatts?.ToString("F0") ?? "—"} W\n" +
        $"Average cadence: {a.AverageCadenceRpm?.ToString("F0") ?? "—"} rpm  ·  Max: {a.MaxCadenceRpm?.ToString("F0") ?? "—"} rpm\n" +
        $"Elevation gain: {a.ElevationGainMeters:F0} m\n{fit}\n\n{media}";

    private static string FormatHistoryDetail(RideActivity a) =>
        $"{a.RouteName}\n{a.StartedAtUtc.ToLocalTime():f}\n\nDistance: {a.DistanceMeters / 1000:F2} km\n" +
        $"Duration: {FormatDuration(a.DurationSeconds)}\nLaps: {a.CompletedLaps}\nAverage power: {a.AveragePowerWatts?.ToString("F0") ?? "—"} W\n" +
        $"FIT: {(a.FitFilePath is null ? "not saved" : a.FitFilePath)}";

    private static string FormatDuration(double seconds)
    {
        var span = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return span.TotalHours >= 1 ? span.ToString(@"h\:mm\:ss") : span.ToString(@"m\:ss");
    }
}
