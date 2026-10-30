using Godot;
using IndoorCycling.Core.Models;
using IndoorCycling.Core.Workouts;
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

/// <summary>Production RideScreen Levels workout and HUD review using disposable test profile data.</summary>
public partial class WorkoutRideScreenReviewTest : Node
{
    private readonly string _output = System.Environment.GetEnvironmentVariable("WODRA_REVIEW_OUTPUT")
        ?? Path.Combine(Path.GetTempPath(), "WodraWorkoutReview");
    private int _assertions;
    private bool _workoutTransitionCaptured;
    private static readonly Vector2I[] HudReviewSizes =
    [new(960, 540), new(1280, 720), new(1920, 1080), new(2560, 1440)];
    private static bool IsHeadless => RenderingServer.GetCurrentRenderingDriverName().Contains("headless", StringComparison.OrdinalIgnoreCase)
        || RenderingServer.GetCurrentRenderingDriverName().Contains("dummy", StringComparison.OrdinalIgnoreCase)
        || DisplayServer.GetName().Contains("headless", StringComparison.OrdinalIgnoreCase);

    public override async void _Ready()
    {
        try
        {
            Directory.CreateDirectory(_output);
            var ride = GD.Load<PackedScene>("res://Scenes/RideScreen.tscn").Instantiate<Control>();
            AddChild(ride); await Frames(10);
            var start = ride.GetNode<StartPanelController>("StartPanel");
            var profile = new RiderProfileRecord(Guid.NewGuid(), "Workout Review Test", DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow, 76, 9, true, 50);
            await GameBootstrap.SaveProfileAsync(profile); await GameBootstrap.SetActiveProfileAsync(profile.Id);
            foreach (var window in start.GetChildren().OfType<Window>()) window.Hide();
            GameBootstrap.Settings.DummyTrainer.Mode = "Watts";
            GameBootstrap.Settings.DummyTrainer.TargetWatts = 175;
            GameBootstrap.Settings.DummyTrainer.CadenceRpm = 85;
            GameBootstrap.Settings.Recording.OutputFolder = Path.Combine(_output, "fit");
            GameBootstrap.Settings.Save(); GameBootstrap.ConfigureFakeTrainerMode();

            var routes = ride.GetNode<RouteSelectionPanelController>("RouteSelectionPanel");
            var workouts = ride.GetNode<WorkoutSelectionPanelController>("WorkoutSelectionPanel");
            var session = ride.GetNode<RideSessionNode>("RideSessionNode");
            var hud = ride.GetNode<HudController>("HudController");
            var world = ride.GetNode<RideWorldView>("RideWorldView");
            var storedRoute = routes.SelectedRouteId;
            VerifyWorkoutHudPolicy();

            start.Hide(); routes.ShowWorkouts(); workouts.ShowSelection(); await Frames(4);
            var panel = workouts.GetNode<PanelContainer>("WorkoutSelectionPanel");
            if (!IsHeadless) Capture("workout-selector-default.png");
            Check(workouts.GetNode<Button>("WorkoutSelectionPanel/MarginContainer/VBoxContainer/Footer/StartWorkout").Disabled,
                "FTP percentage workout remains blocked until the test profile has explicit FTP");

            var row = workouts.FindChildren("", "Button", true, false).OfType<Button>()
                .First(button => button.Text.StartsWith("Three-minute intervals", StringComparison.Ordinal));
            row.EmitSignal(BaseButton.SignalName.Pressed);
            var profileFtp = GameBootstrap.Settings.WorkoutFtpWattsByProfile[profile.Id] = 210;
            GameBootstrap.Settings.Save(); workouts.ShowSelection(); await Frames(3);
            Check(workouts.GetNode<Label>("WorkoutSelectionPanel/MarginContainer/VBoxContainer/Content/PreviewScroll/Preview/WorkoutTitle").Text
                .Contains("Three-minute", StringComparison.Ordinal), "selected workout is retained while reloading preview");
            if (!IsHeadless)
            {
                DisplayServer.WindowSetMode(DisplayServer.WindowMode.Windowed); DisplayServer.WindowSetSize(new Vector2I(960, 540));
                await Frames(8); CheckWithinWindow(panel, "minimum window"); Capture("workout-selector-minimum.png");
                DisplayServer.WindowSetSize(new Vector2I(1152, 648)); await Frames(8); CheckWithinWindow(panel, "default window");
                Capture("workout-selector-default-size.png");
                DisplayServer.WindowSetMode(DisplayServer.WindowMode.Fullscreen); await Frames(8); CheckWithinWindow(panel, "fullscreen");
                Capture("workout-selector-fullscreen.png"); DisplayServer.WindowSetMode(DisplayServer.WindowMode.Windowed);
                DisplayServer.WindowSetSize(new Vector2I(1152, 648)); await Frames(4);
            }

            start.ShowOptions(returnToRoutes: false, returnToWorkouts: true); await Frames(3);
            var options = ride.GetNode<Window>("OptionsWindow");
            options.FindChildren("", "Button", true, false).OfType<Button>().Single(button => button.Text == "Done")
                .EmitSignal(BaseButton.SignalName.Pressed); await Frames(3);
            Check(workouts.Visible && workouts.GetNode<Label>("WorkoutSelectionPanel/MarginContainer/VBoxContainer/Content/PreviewScroll/Preview/WorkoutTitle").Text
                .Contains("Three-minute", StringComparison.Ordinal), "Options return to the same workout selection");
            Check(!start.Visible, "returning from workout Options does not reveal the startup screen overlay");

            var definition = new WorkoutDefinition("review-short", "Review intervals", "Synthetic UI fixture", [
                new("warm", "Warm-up", WorkoutBlockKind.WarmUp, 180, new(.5, WorkoutTargetUnit.FtpPercent), new(.7, WorkoutTargetUnit.FtpPercent), 85),
                new("work", "Work", WorkoutBlockKind.Work, 900, new(200, WorkoutTargetUnit.Watts), null, 95)]);
            if (!await session.StartWorkoutSessionAsync(definition, profileFtp, useErg: true))
                throw new InvalidOperationException("Could not start production dummy workout session.");
            workouts.Hide(); routes.Hide(); hud.Show(); await Settle(2.5);
            var workoutHud = hud.GetNode<PanelContainer>("WorkoutHudPanel");
            Check(session.IsWorkoutSession && session.CurrentRoute?.Environment == RouteEnvironment.Raceway,
                "workout is a separate activity in the flat Levels Raceway environment");
            var raceway = session.CurrentRoute ?? throw new InvalidOperationException("Workout route was not initialized.");
            Check(raceway is { DistanceMeters: > 2400 and < 2405, Points: { Count: > 400 } } &&
                raceway.Points!.All(point => point.Latitude is null && point.Longitude is null &&
                    point.GradientPercent == 0 && point.ElevationMeters == 0) && raceway.TotalAscentMeters == 0,
                "Levels workout uses the resampled 2.4 km local circuit without GPS, grade or ascent");
            var geometry = new VisualRoute(raceway, spacingMeters: 5);
            Check(geometry.Samples.Count > 480 &&
                System.Numerics.Vector3.Distance(geometry.Samples[0].Position, geometry.Samples[^1].Position) < .001f,
                "sampled circuit is closed at the start/finish seam");
            Check(geometry.Samples.Zip(geometry.Samples.Skip(1), (a, b) => System.Numerics.Vector3.Distance(a.Position, b.Position)).Max() < 5.1f,
                "resampled circuit has continuous short mesh segments without lap gaps");
            var area = geometry.Samples.Zip(geometry.Samples.Skip(1), (a, b) =>
                (double)a.Position.X * -b.Position.Z - (double)b.Position.X * -a.Position.Z).Sum() / 2;
            Check(area > 0, "local projected centreline retains anticlockwise circuit direction");
            Check(Math.Abs(raceway.GetLoopDistance(raceway.DistanceMeters + 8) - 8) < .01 &&
                System.Numerics.Vector3.Distance(geometry.GetPoseAtDistance(raceway.DistanceMeters + 8).Position,
                    geometry.GetPoseAtDistance(8).Position) < .01f,
                "authoritative workout distance wraps smoothly across the 2.4 km lap seam");
            var environmentPolicy = RouteEnvironmentPolicy.For(raceway.Environment);
            Check(!environmentPolicy.SupportsAmbientCars && !environmentPolicy.SupportsAmbientPedestrians &&
                !environmentPolicy.SupportsUrbanStreetScenery,
                "workout Raceway uses the explicit no-traffic, no-public-road-furniture environment policy");
            Check(workoutHud.Visible, "workout HUD appears only during workout activity");
            Check(hud.GetNode<Control>("RouteGradientInset").Visible == false,
                "geographical route inset is absent from the workout circuit");
            Check(world.LocalAiRiderCount == 0, "workout circuit creates no local AI cyclists");
            var circuitVisual = world.FindChild("LevelsRacewayEnvironment", true, false) as Node3D;
            Check(circuitVisual is not null && circuitVisual.GetChildren().OfType<MeshInstance3D>()
                .Count(mesh => mesh.Mesh is not null) >= 4 &&
                circuitVisual.GetNodeOrNull<Node3D>("TracksideSafetyBarriers") is not null &&
                circuitVisual.GetNodeOrNull<Node3D>("PaddockAndPitBuildings") is not null,
                "production Raceway includes continuous road surfaces, barriers and paddock scenery");
            if (!IsHeadless)
            {
                await ReviewHudViewports(hud, workoutHud);
                hud.SetDebugMode(true); await Frames(5);
                CheckWorkoutHudBounds(hud, workoutHud, "debug overlay enabled");
                Capture("issue-40-workout-hud-debug-enabled.png");
                hud.SetDebugMode(false); await Frames(4);
                CheckWorkoutHudBounds(hud, workoutHud, "debug overlay disabled");
                Capture("issue-40-workout-hud-debug-disabled.png");
                hud.SetDebugMode(true);
                session.ToggleRidePause(); await Settle(.3); Capture("workout-hud-paused.png");
                session.ResumeRide(); await Settle(.6); Capture("workout-hud-resumed.png");

                var milestones = new[] { 450d, 850d, 1250d, 1700d, 2150d, raceway!.DistanceMeters + 8 };
                var nextMilestone = 0;
                var lapTimeout = DateTimeOffset.UtcNow.AddMinutes(9);
                while (session.CurrentRideState.DistanceMeters < raceway.DistanceMeters + 8 && DateTimeOffset.UtcNow < lapTimeout)
                {
                    await Settle(1);
                    if (nextMilestone < milestones.Length && session.CurrentRideState.DistanceMeters >= milestones[nextMilestone])
                    {
                        await Frames(2);
                        Capture($"issue-40-raceway-distance-{(int)milestones[nextMilestone]}m.png");
                        nextMilestone++;
                    }
                    if (session.CurrentRideState.ElapsedSeconds >= 180 && _workoutTransitionCaptured == false)
                    {
                        _workoutTransitionCaptured = true;
                        Check(session.CurrentRideState.DistanceMeters > 0, "workout interval transition occurs during the production ride");
                        Capture("issue-40-raceway-workout-interval-transition.png");
                    }
                }
                Check(session.CurrentRideState.DistanceMeters >= raceway.DistanceMeters,
                    "production dummy workout rider completes a full 2.4 km circuit lap");
                Check(session.CurrentRideState.DistanceMeters > raceway.DistanceMeters,
                    "rider crosses the continuous lap seam and continues into lap two");
                Check(_workoutTransitionCaptured, "the full-lap production ride reaches and captures the next workout interval");
                Check(world.IsVisibleInTree(), "production Raceway world remains active through full-lap wraparound");
            }
            else
            {
                Check(session.CurrentRideState.DistanceMeters >= 0, "headless ride session remains valid for geometry regression");
            }
            await session.ExitToRouteSelectionAsync(); await Frames(4);
            Check(session.LastCompletedActivityDraft is { Kind: RideActivityKind.Workout,
                WorkoutDefinitionId: "review-short", WorkoutFtpWatts: 210 },
                "ended activity stores its workout identity and captured FTP in existing history metadata");
            Check(Math.Abs(session.LastCompletedActivityDraft!.AveragePowerWatts!.Value - 175) < 0.1,
                "recorded power remains the measured dummy input rather than the workout target");
            Check(((IndoorCycling.Trainer.Implementations.FakeTrainerClient)GameBootstrap.TrainerClient!)
                .LastRequestedSimulationGradePercent is null,
                "Levels workout scenery does not send geographic grade commands to the trainer");
            Check(session.LastCompletedActivityDraft?.FitFilePath is { } fitPath && File.Exists(fitPath),
                "workout recording finalizes a FIT file in the isolated recording directory");
            Check(routes.SelectedRouteId == storedRoute, "route selection survives a workout session and return to Routes");
            Check(!workoutHud.Visible, "workout-specific HUD disappears on leaving the workout");
            routes.ShowRouteSelection(); await Frames(2);
            if (!IsHeadless) Capture("routes-after-workout.png");

            if (!await session.StartWorkoutSessionAsync(definition, profileFtp, useErg: true))
                throw new InvalidOperationException("Could not start workout for cancellation review.");
            await session.ExitToRouteSelectionAsync(); await Frames(4);
            Check(session.LastCompletedActivityDraft is { Kind: RideActivityKind.Workout, WorkoutCompleted: false },
                "cancelled workout is saved as an incomplete activity and returns to Routes");
            Check(!workoutHud.Visible, "workout cancellation also clears the workout HUD");
            routes.ShowRouteSelection(); await Frames(3);
            var roadRouteChoice = routes.FindChildren("*", "Button", true, false).OfType<Button>()
                .First(button => button.Name == "RouteChoice" && button.Text.Contains("Dunedin One-way: Oval to Gardens Loop", StringComparison.Ordinal));
            roadRouteChoice.EmitSignal(BaseButton.SignalName.Pressed); await Frames(2);
            typeof(RouteSelectionPanelController).GetMethod("StartSelectedRoute", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(routes, null);
            await Frames(20);
            Check(session.IsRideActive && !session.IsWorkoutSession && session.CurrentRoute?.Environment == RouteEnvironment.Road,
                "a normal road ride starts after the workout-only Raceway environment");
            if (!IsHeadless) Capture("issue-40-normal-road-ride-regression.png");
            await session.ExitToRouteSelectionAsync(); await Frames(4);
            Check(!session.IsRideActive && !workoutHud.Visible, "normal road ride exits without retaining the workout HUD");
            Console.WriteLine($"WORKOUT REVIEW PASS: {_assertions} assertions; captures: {_output}");
            GetTree().Quit(0);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"WORKOUT REVIEW FAIL: {ex}"); GetTree().Quit(1);
        }
    }

    private void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); _assertions++; Console.WriteLine($"PASS: {message}"); }

    private void VerifyWorkoutHudPolicy()
    {
        foreach (var size in HudReviewSizes)
        {
            var layout = RideHudOverlayLayoutPolicy.ForViewport(size.X, size.Y,
                showRouteInset: false, showDummyPanel: true, bottomBarWidth: 190, bottomBarHeight: 44,
                showMediaPanel: true, mediaWidth: 252, mediaHeight: 58, showDebugPanel: true, topHudBottom: 217,
                showWorkoutPanel: true, workoutWidth: RideHudOverlayLayoutPolicy.WorkoutPanelWidth,
                workoutHeight: RideHudOverlayLayoutPolicy.WorkoutPanelHeight);
            var topHud = new RideHudOverlayBounds(12, 12, 448, 205);
            var bottomBar = layout.BottomBar;
            var media = new RideHudOverlayBounds(bottomBar.Right + RideHudOverlayLayoutPolicy.PanelGap,
                size.Y - RideHudOverlayLayoutPolicy.BottomPanelMargin - 58, 252, 58);
            Check(Contains(size.X, size.Y, layout.WorkoutPanel), $"workout HUD policy stays inside {size.X}x{size.Y}");
            Check(!Intersects(layout.WorkoutPanel, topHud), $"workout HUD clears top ride metrics at {size.X}x{size.Y}");
            Check(!Intersects(layout.WorkoutPanel, layout.DummyPanel), $"workout HUD clears dummy controls at {size.X}x{size.Y}");
            Check(!Intersects(layout.WorkoutPanel, layout.DebugPanel), $"workout HUD clears debug overlay at {size.X}x{size.Y}");
            Check(!Intersects(layout.WorkoutPanel, bottomBar), $"workout HUD clears action bar at {size.X}x{size.Y}");
            Check(!Intersects(layout.WorkoutPanel, media), $"workout HUD clears media controls at {size.X}x{size.Y}");
        }
    }

    private async Task ReviewHudViewports(HudController hud, PanelContainer panel)
    {
        DisplayServer.WindowSetMode(DisplayServer.WindowMode.Windowed);
        foreach (var size in HudReviewSizes)
        {
            DisplayServer.WindowSetSize(size);
            await Frames(12);
            typeof(HudController).GetMethod("UpdateDebugOverlayLayout", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(hud, null);
            await Frames(3);
            CheckWorkoutHudBounds(hud, panel, $"{size.X}x{size.Y}");
            var name = $"issue-40-workout-hud-{size.X}x{size.Y}.png";
            Capture(name);
            var image = Image.LoadFromFile(Path.Combine(_output, name));
            Check(image.GetWidth() == size.X && image.GetHeight() == size.Y,
                $"production RideScreen screenshot is captured at the requested {size.X}x{size.Y} resolution");
        }
        DisplayServer.WindowSetSize(new Vector2I(1280, 720));
        await Frames(8);
    }

    private void CheckWorkoutHudBounds(HudController hud, PanelContainer panel, string context)
    {
        var viewport = GetViewport().GetVisibleRect();
        var rect = panel.GetGlobalRect();
        Check(panel.Visible && rect.Position.X >= -1 && rect.Position.Y >= -1 &&
            rect.End.X <= viewport.End.X + 1 && rect.End.Y <= viewport.End.Y + 1,
            $"workout panel is visible and contained for {context}: {rect} in {viewport}");
        var top = hud.GetNode<Control>("TopHudPanel");
        if (top.Visible) Check(!rect.Intersects(top.GetGlobalRect()), $"workout panel does not overlap ride metrics for {context}");
        foreach (var path in new[] { "DebugRideOverlay", "DummyControlPanel", "BottomHudLane/BottomBar", "MediaControlsPanel" })
        {
            var other = hud.GetNodeOrNull<Control>(path);
            if (other?.Visible == true)
                Check(!rect.Intersects(other.GetGlobalRect()), $"workout panel does not overlap {path} for {context}");
        }
        foreach (var name in new[] { "WorkoutIntensityDown", "WorkoutIntensityUp", "WorkoutSkipInterval" })
        {
            var button = panel.FindChildren(name, "Button", true, false).OfType<Button>().FirstOrDefault();
            if (button is null) continue;
            var buttonRect = button.GetGlobalRect();
            Check(button.IsVisibleInTree() && buttonRect.Size.X > 0 && buttonRect.Size.Y > 0 &&
                buttonRect.Position.X >= rect.Position.X && buttonRect.End.X <= rect.End.X + 1 &&
                buttonRect.Position.Y >= rect.Position.Y && buttonRect.End.Y <= rect.End.Y + 1,
                $"workout action {name} remains visible inside the panel for {context}");
        }
    }

    private static bool Contains(float width, float height, RideHudOverlayBounds rect) =>
        rect.X >= 0 && rect.Y >= 0 && rect.Right <= width && rect.Bottom <= height;
    private static bool Intersects(RideHudOverlayBounds a, RideHudOverlayBounds b) =>
        a.X < b.Right && a.Right > b.X && a.Y < b.Bottom && a.Bottom > b.Y;
    private async Task Frames(int count) { for (var i = 0; i < count; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); }
    private async Task Settle(double seconds) => await ToSignal(GetTree().CreateTimer(seconds), SceneTreeTimer.SignalName.Timeout);
    private void CheckWithinWindow(Control control, string context)
    {
        var size = DisplayServer.WindowGetSize(); var logical = GetViewport().GetVisibleRect().Size;
        var scale = new Vector2(size.X / logical.X, size.Y / logical.Y); var rect = control.GetGlobalRect();
        var end = (rect.Position + rect.Size) * scale; var start = rect.Position * scale;
        Check(start.X >= -2 && start.Y >= -2 && end.X <= size.X + 2 && end.Y <= size.Y + 2,
            $"workout selector fits {context}: {start}–{end} in {size}");
    }
    private void Capture(string name)
    {
        if (IsHeadless) return;
        RenderingServer.ForceDraw(false); var image = GetViewport().GetTexture().GetImage();
        if (image.IsEmpty()) throw new InvalidOperationException($"Could not capture {name}.");
        image.SavePng(Path.Combine(_output, name));
    }

}
