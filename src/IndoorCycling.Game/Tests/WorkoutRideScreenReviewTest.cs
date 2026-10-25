using Godot;
using IndoorCycling.Core.Models;
using IndoorCycling.Core.Workouts;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

/// <summary>Production RideScreen workout selector/studio/HUD review using disposable test profile data.</summary>
public partial class WorkoutRideScreenReviewTest : Node
{
    private readonly string _output = System.Environment.GetEnvironmentVariable("WODRA_REVIEW_OUTPUT")
        ?? Path.Combine(Path.GetTempPath(), "WodraWorkoutReview");
    private int _assertions;
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
            GameBootstrap.Settings.DummyTrainer.TargetWatts = 190;
            GameBootstrap.Settings.DummyTrainer.CadenceRpm = 90;
            GameBootstrap.Settings.Recording.OutputFolder = Path.Combine(_output, "fit");
            GameBootstrap.Settings.Save(); GameBootstrap.ConfigureFakeTrainerMode();

            var routes = ride.GetNode<RouteSelectionPanelController>("RouteSelectionPanel");
            var workouts = ride.GetNode<WorkoutSelectionPanelController>("WorkoutSelectionPanel");
            var session = ride.GetNode<RideSessionNode>("RideSessionNode");
            var hud = ride.GetNode<HudController>("HudController");
            var world = ride.GetNode<RideWorldView>("RideWorldView");
            var storedRoute = routes.SelectedRouteId;

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
                new("warm", "Warm-up", WorkoutBlockKind.WarmUp, 3, new(.5, WorkoutTargetUnit.FtpPercent), new(.7, WorkoutTargetUnit.FtpPercent), 85),
                new("work", "Work", WorkoutBlockKind.Work, 12, new(200, WorkoutTargetUnit.Watts), null, 95)]);
            if (!await session.StartWorkoutSessionAsync(definition, profileFtp, useErg: true))
                throw new InvalidOperationException("Could not start production dummy workout session.");
            workouts.Hide(); routes.Hide(); hud.Show(); await Settle(2.5);
            var workoutHud = hud.GetNode<PanelContainer>("WorkoutHudPanel");
            Check(session.IsWorkoutSession && session.CurrentRoute?.Environment == RouteEnvironment.TrainingStudio,
                "workout is a separate activity in the explicit training-studio environment");
            Check(workoutHud.Visible, "workout HUD appears only during workout activity");
            Check(hud.GetNode<Control>("RouteGradientInset").Visible == false,
                "geographical route inset is absent from the training studio");
            Check(world.LocalAiRiderCount == 0, "training studio creates no local AI cyclists");
            if (!IsHeadless)
            {
                Capture("workout-hud-active.png");
                session.ToggleRidePause(); await Settle(.3); Capture("workout-hud-paused.png");
                session.ResumeRide(); await Settle(.6); Capture("workout-hud-resumed.png");
            }
            await session.ExitToRouteSelectionAsync(); await Frames(4);
            Check(session.LastCompletedActivityDraft is { Kind: RideActivityKind.Workout,
                WorkoutDefinitionId: "review-short", WorkoutFtpWatts: 210 },
                "completed activity stores its workout identity and captured FTP in existing history metadata");
            Check(Math.Abs(session.LastCompletedActivityDraft!.AveragePowerWatts!.Value - 190) < 0.1,
                "recorded power remains the measured dummy input rather than the workout target");
            Check(((IndoorCycling.Trainer.Implementations.FakeTrainerClient)GameBootstrap.TrainerClient!)
                .LastRequestedSimulationGradePercent is null,
                "workout studio does not send geographic grade commands to the trainer");
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
