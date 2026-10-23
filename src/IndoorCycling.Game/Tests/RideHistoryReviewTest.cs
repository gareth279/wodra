using Godot;
using IndoorCycling.Core.Models;
using IndoorCycling.Persistence.Sqlite;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

public partial class RideHistoryReviewTest : Node
{
    private readonly string _output = Path.Combine(Path.GetTempPath(), "WodraRideHistoryReview");
    private int _assertions;
    private static bool IsHeadless => DisplayServer.GetName().Contains("headless", StringComparison.OrdinalIgnoreCase);

    public override async void _Ready()
    {
        try
        {
            Directory.CreateDirectory(_output);
            var dbPath = Path.Combine(Path.GetTempPath(), "wodra-history-ui-" + Guid.NewGuid().ToString("N"), "history.sqlite3");
            var repository = new SqliteRideHistoryRepository(dbPath); await repository.InitializeAsync();
            var now = DateTimeOffset.UtcNow; var profile = new RiderProfileRecord(Guid.NewGuid(), "History Review", now, now);
            await repository.SaveProfileAsync(profile);
            for (var i = 0; i < 55; i++)
            {
                var route = i % 2 == 0 ? "oval" : "gardens";
                await repository.SaveCompletedActivityAsync(new RideActivityDraft(Guid.NewGuid(), profile.Id, route, route == "oval" ? "Oval Loop" : "Gardens Return",
                    now.AddDays(-i), RideActivityStatus.Completed, 1200 + i, 8000 + i * 100, i % 4, null, null,
                    i % 3 == 0 ? null : 150 + i, null, 80, null, i, null), []);
            }

            var ride = GD.Load<PackedScene>("res://Scenes/RideScreen.tscn").Instantiate<Control>();
            var controller = ride.GetNode<RideHistoryPanelController>("RideHistoryPanel");
            controller.ConfigureForIsolatedReview(repository, profile);
            AddChild(ride); await Frames(8); controller.ShowHistory(); await Frames(20);
            var list = Find<ItemList>(controller, "HistoryActivityList");
            var search = Find<LineEdit>(controller, "HistorySearch");
            var panel = controller.GetNode<PanelContainer>("HistoryPanel");
            Check(list.ItemCount == 25, "production history panel pages the first 25 recent rides");
            Check(Find<Button>(controller, "OpenFitFolder").Disabled && Find<Label>(controller, "HistoryDetail").Text.Contains("Speed: avg") && Find<Label>(controller, "HistoryDetail").Text.Contains("—"), "selected ride shows available metrics and missing FIT/measurements without converting them to zero");
            Check(Find<Label>(controller, "HistoryDashboard").Text.Contains("This week") && Find<Control>(controller, "HistoryTrendChart").IsVisibleInTree(), "profile dashboard shows weekly totals and a trend chart");
            Find<Button>(controller, "HistoryNextPage").EmitSignal(Button.SignalName.Pressed); await Frames(10);
            Check(list.ItemCount == 25 && Find<Label>(controller, "HistoryPageStatus").Text.StartsWith("26–50"), "page navigation loads rides beyond the first page");
            Find<Button>(controller, "HistoryPreviousPage").EmitSignal(Button.SignalName.Pressed); await Frames(10);
            Check(Find<Label>(controller, "HistoryDetail").Text.Contains("Oval Loop"), "page navigation returns with a selected ride detail");
            var routeFilter = Find<OptionButton>(controller, "HistoryRouteFilter"); routeFilter.Select(1); routeFilter.EmitSignal(OptionButton.SignalName.ItemSelected, 1); await Frames(10);
            Check(list.ItemCount > 0 && Enumerable.Range(0, list.ItemCount).All(i => list.GetItemText(i).Contains("Gardens", StringComparison.OrdinalIgnoreCase)), "route filter selects a route independently of search");
            routeFilter.Select(0); routeFilter.EmitSignal(OptionButton.SignalName.ItemSelected, 0); await Frames(10);
            var dateFilter = Find<OptionButton>(controller, "HistoryDateFilter"); dateFilter.Select(1); dateFilter.EmitSignal(OptionButton.SignalName.ItemSelected, 1); await Frames(10);
            Check(list.ItemCount == 7, "local last-seven-days filter includes the selected calendar range");
            dateFilter.Select(0); dateFilter.EmitSignal(OptionButton.SignalName.ItemSelected, 0); await Frames(10);
            var sortFilter = Find<OptionButton>(controller, "HistorySortFilter"); sortFilter.Select(2); sortFilter.EmitSignal(OptionButton.SignalName.ItemSelected, 2); await Frames(10);
            Check(list.GetItemText(0).Contains("13.4 km"), "distance sorting puts the longest matching ride first");
            sortFilter.Select(0); sortFilter.EmitSignal(OptionButton.SignalName.ItemSelected, 0); await Frames(10);
            search.Text = "Gardens"; search.EmitSignal(LineEdit.SignalName.TextChanged, search.Text); await Frames(12);
            Check(list.ItemCount > 0 && Enumerable.Range(0, list.ItemCount).All(i => list.GetItemText(i).Contains("Gardens", StringComparison.OrdinalIgnoreCase)), "route-name search filters the production list");
            search.Text = "does not exist"; search.EmitSignal(LineEdit.SignalName.TextChanged, search.Text); await Frames(12);
            Check(list.ItemCount == 0 && Find<Label>(controller, "HistoryPageStatus").Text.Contains("No rides"), "empty filters produce a clear state");
            search.Text = ""; search.EmitSignal(LineEdit.SignalName.TextChanged, search.Text); await Frames(12);

            if (!IsHeadless)
            {
                Capture("history-default.png");
                foreach (var size in new[] { new Vector2I(960, 540), new Vector2I(1280, 720) })
                {
                    DisplayServer.WindowSetMode(DisplayServer.WindowMode.Windowed); DisplayServer.WindowSetSize(size); await Frames(8);
                    CheckWithin(panel, size, $"{size.X}x{size.Y}"); Capture($"history-{size.X}x{size.Y}.png");
                }
                DisplayServer.WindowSetMode(DisplayServer.WindowMode.Fullscreen); await Frames(8);
                var full = DisplayServer.WindowGetSize(); CheckWithin(panel, full, "fullscreen"); Capture("history-fullscreen.png");
                DisplayServer.WindowSetMode(DisplayServer.WindowMode.Windowed);
            }
            GD.Print($"[RideHistoryReview] PASS {_assertions}; isolated database; screenshots={_output}"); GetTree().Quit(0);
        }
        catch (Exception ex) { GD.PushError("[RideHistoryReview] FAIL " + ex); GetTree().Quit(1); }
    }

    private static T Find<T>(Node root, string name) where T : Node => root.FindChildren(name, "", true, false).OfType<T>().Single();
    private void Check(bool result, string message) { _assertions++; if (!result) throw new InvalidOperationException(message); GD.Print("[RideHistoryReview] PASS " + message); }
    private void CheckWithin(Control panel, Vector2I window, string label)
    {
        var rect = panel.GetGlobalRect(); var viewport = GetViewport().GetVisibleRect().Size;
        var scale = new Vector2(window.X / viewport.X, window.Y / viewport.Y);
        var p = rect.Position * scale; var e = (rect.Position + rect.Size) * scale;
        Check(p.X >= -2 && p.Y >= -2 && e.X <= window.X + 2 && e.Y <= window.Y + 2, $"panel fits {label}: {p}..{e} of {window}");
    }
    private async Task Frames(int count) { for (var i = 0; i < count; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); }
    private void Capture(string name) { RenderingServer.ForceDraw(false); var image = GetViewport().GetTexture().GetImage(); if (image.IsEmpty()) throw new IOException("empty UI screenshot " + name); image.SavePng(Path.Combine(_output, name)); }
}
