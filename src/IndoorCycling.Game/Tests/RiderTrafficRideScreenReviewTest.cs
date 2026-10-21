using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Godot;

/// <summary>Motion and model-envelope regressions against the production RideScreen and its imported riders.</summary>
public partial class RiderTrafficRideScreenReviewTest : Node
{
    private static readonly RiderCameraView[] Views =
    [
        RiderCameraView.GameplayOriginal, RiderCameraView.GameplayRefined, RiderCameraView.CloseRear,
        RiderCameraView.RearThreeQuarter, RiderCameraView.Side, RiderCameraView.FrontThreeQuarter
    ];

    public override async void _Ready()
    {
        try
        {
            DisplayServer.WindowSetSize(new Vector2I(1280, 720));
            var output = Path.Combine(ProjectSettings.GlobalizePath("res://../.."),
                "artifacts", "alpha52-rider-traffic-review");
            Directory.CreateDirectory(output);
            var frames = Path.Combine(output, "motion-frames");
            Directory.CreateDirectory(frames);
            var screen = GD.Load<PackedScene>("res://Scenes/RideScreen.tscn").Instantiate();
            AddChild(screen);
            await Settle(6);
            GameBootstrap.Settings.DebugModeEnabled = true;
            GameBootstrap.Settings.Recording.OutputFolder = Path.Combine(output, "fit");
            GameBootstrap.Settings.DummyTrainer.Mode = "Watts";
            GameBootstrap.Settings.DummyTrainer.TargetWatts = 220;
            GameBootstrap.Settings.DummyTrainer.CadenceRpm = 86;
            GameBootstrap.ConfigureFakeTrainerMode();
            var world = screen.GetNode<RideWorldView>("RideWorldView");
            world.AiRiderTargetCount = 4;
            world.AiRiderMaximumCount = 4;
            var session = screen.GetNode<RideSessionNode>("RideSessionNode");
            if (!await session.StartTrainerSessionAsync(RouteCatalog.GetById("dunedin-rolling-loop")))
                throw new InvalidOperationException("Could not start the production Rolling City ride with the fake trainer.");
            GD.Print("[RiderTrafficRideScreenReview] production ride started");
            var hud = screen.GetNode<HudController>("HudController");
            hud.Show();
            foreach (var window in screen.GetNode<StartPanelController>("StartPanel").GetChildren())
                if (window is Window popup) popup.Hide();
            screen.GetNode<StartPanelController>("StartPanel").Hide();
            await Settle(8);
            GD.Print("[RiderTrafficRideScreenReview] initial route settle complete");
            if (world.RouteGeometry is null) throw new InvalidOperationException("Production RideScreen route did not load.");

            session.JumpToRouteDistance("720");
            session.ConfirmDebugDistanceJump();
            hud.HideDebugJumpConfirmation();
            hud.SetRecordingState(false, false);
            await Settle(8);
            GD.Print("[RiderTrafficRideScreenReview] positioned at 720m");

            var minimumRiderClearance = float.PositiveInfinity;
            var minimumActorClearance = float.PositiveInfinity;
            foreach (var view in Views)
            {
                world.SelectCameraInspectionView(view);
                await Settle(15);
                GD.Print($"[RiderTrafficRideScreenReview] checking camera={view}");
                VerifyCameraFraming(world, hud, view);
                VerifyModelClearance(world, out var riderClearance, out var actorClearance);
                minimumRiderClearance = Math.Min(minimumRiderClearance, riderClearance);
                minimumActorClearance = Math.Min(minimumActorClearance, actorClearance);
                await Capture(Path.Combine(output, $"ride-{view}-debug-on.png"));
                if (view is RiderCameraView.Side or RiderCameraView.GameplayRefined)
                {
                    hud.SetDebugDisplayEnabled(false);
                    await Settle(15);
                    VerifyModelClearance(world, out riderClearance, out actorClearance);
                    minimumRiderClearance = Math.Min(minimumRiderClearance, riderClearance);
                    minimumActorClearance = Math.Min(minimumActorClearance, actorClearance);
                    await Capture(Path.Combine(output, $"ride-{view}-debug-off.png"));
                    hud.SetDebugDisplayEnabled(true);
                }
            }

            world.SelectCameraInspectionView(RiderCameraView.RearThreeQuarter);
            await Settle(20);
            var framesCaptured = 0;
            var maximumFrameClearance = float.PositiveInfinity;
            for (var frame = 0; frame < 720; frame++)
            {
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                VerifyModelClearance(world, out var riderClearance, out var actorClearance);
                minimumRiderClearance = Math.Min(minimumRiderClearance, riderClearance);
                minimumActorClearance = Math.Min(minimumActorClearance, actorClearance);
                maximumFrameClearance = Math.Min(maximumFrameClearance, Math.Min(riderClearance, actorClearance));
                if (frame % 8 == 0)
                {
                    await Capture(Path.Combine(frames, $"frame-{framesCaptured:D4}.png"));
                    framesCaptured++;
                }
            }
            var diagnostic = world.GetDiagnosticState();
            if (diagnostic.AiRiders < 3) throw new InvalidOperationException($"RideScreen did not load the test rider population: {diagnostic.AiRiders}.");
            if (diagnostic.AmbientCars <= 0) throw new InvalidOperationException("Production RideScreen did not spawn ambient vehicles for the collision scenario.");
            GD.Print($"[RiderTrafficRideScreenReview] PASS: actual RideScreen, route={session.CurrentRoute?.Id}, AI={diagnostic.AiRiders}, cars={diagnostic.AmbientCars}, rider-model-clearance={minimumRiderClearance:F3}m, actor-model-clearance={minimumActorClearance:F3}m, frames={framesCaptured}; evidence={output}");
            GetTree().Quit(0);
        }
        catch (Exception error)
        {
            GD.PushError($"[RiderTrafficRideScreenReview] FAIL: {error}");
            GetTree().Quit(1);
        }
    }

    private static void VerifyModelClearance(RideWorldView world, out float riderClearance,
        out float actorClearance)
    {
        var riders = world.GetRiderCollisionReviewSamples().Where(sample => sample.RiderId != "ghost").ToArray();
        riderClearance = float.PositiveInfinity;
        for (var first = 0; first < riders.Length; first++)
        for (var second = first + 1; second < riders.Length; second++)
        {
            var a = riders[first]; var b = riders[second];
            var clearance = AmbientActorEnvelopeMath.SignedClearance(ToNumerics(a.WorldPosition),
                ToNumerics(a.WorldForward), a.ModelLengthMeters, a.ModelWidthMeters,
                a.WorldBounds.Size.Y, new AmbientTrafficPose(0, ToNumerics(b.WorldPosition), ToNumerics(b.WorldForward)),
                b.ModelLengthMeters, b.ModelWidthMeters, b.WorldBounds.Size.Y);
            riderClearance = Math.Min(riderClearance, clearance);
            if (clearance < -.02f)
                throw new InvalidOperationException($"Production cyclist model envelopes intersect: {a.RiderId}/{b.RiderId}, clearance={clearance:F3}m, positions={a.WorldPosition}/{b.WorldPosition}.");
        }

        actorClearance = float.PositiveInfinity;
        var actors = world.GetAmbientTrafficCollisionReviewSamples();
        foreach (var rider in riders)
        foreach (var actor in actors)
        {
            var pose = actor.PoseAt(0);
            var clearance = AmbientActorEnvelopeMath.SignedClearance(pose.Position, pose.Forward,
                actor.LengthMeters, actor.WidthMeters, actor.HeightMeters,
                new AmbientTrafficPose(0, ToNumerics(rider.WorldPosition), ToNumerics(rider.WorldForward)),
                rider.ModelLengthMeters, rider.ModelWidthMeters, rider.WorldBounds.Size.Y);
            actorClearance = Math.Min(actorClearance, clearance);
            if (clearance < -.02f)
                throw new InvalidOperationException($"Production ambient actor/rider envelopes intersect: {actor.RiderId}/{rider.RiderId}, clearance={clearance:F3}m.");
        }
    }

    private static void VerifyCameraFraming(RideWorldView world, HudController hud, RiderCameraView view)
    {
        var camera = world.InspectionCamera;
        var bounds = world.PlayerRider.GetWorldVisualBounds();
        var safe = hud.GetCameraSafeViewportRect();
        var viewport = camera.GetViewport().GetVisibleRect().Size;
        for (var corner = 0; corner < 8; corner++)
        {
            var point = bounds.Position + new Vector3((corner & 1) == 0 ? 0 : bounds.Size.X,
                (corner & 2) == 0 ? 0 : bounds.Size.Y, (corner & 4) == 0 ? 0 : bounds.Size.Z);
            var normalized = camera.UnprojectPosition(point) / viewport;
            const float margin = .012f;
            if (!float.IsFinite(normalized.X) || !float.IsFinite(normalized.Y)
                || normalized.X < safe.Position.X + margin || normalized.X > safe.End.X - margin
                || normalized.Y < safe.Position.Y + margin || normalized.Y > safe.End.Y - margin)
                throw new InvalidOperationException($"Rider/bike left the usable camera view during actor interaction: {view}, projected={normalized}, safe={safe}.");
        }
    }

    private async Task Capture(string path)
    {
        // Headless CI uses Godot's dummy renderer and has no viewport texture;
        // the same scene test is also run with a real renderer to collect images.
        if (DisplayServer.GetName() == "headless") return;
        var error = GetViewport().GetTexture().GetImage().SavePng(path);
        if (error != Error.Ok) throw new IOException($"Could not save RideScreen collision evidence {path}: {error}.");
    }

    private async Task Settle(int frames)
    {
        for (var i = 0; i < frames; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }

    private static System.Numerics.Vector3 ToNumerics(Vector3 value) => new(value.X, value.Y, value.Z);
}
