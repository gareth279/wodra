using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Godot;

/// <summary>Rendered camera framing checks using the production RideScreen, route, rider and HUD.</summary>
public partial class RideCameraSceneReviewTest : Node
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
            var output = Path.Combine(ProjectSettings.GlobalizePath("res://../.."), "artifacts", "camera-alpha46-review");
            Directory.CreateDirectory(output);
            var screen = GD.Load<PackedScene>("res://Scenes/RideScreen.tscn").Instantiate();
            AddChild(screen);
            await Settle(5);
            GameBootstrap.Settings.DebugModeEnabled = true;
            GameBootstrap.ConfigureFakeTrainerMode();
            var session = screen.GetNode<RideSessionNode>("RideSessionNode");
            if (!await session.StartTrainerSessionAsync(RouteCatalog.GetById("dunedin-rolling-loop")))
                throw new InvalidOperationException("Could not start the built-in Rolling City dummy ride.");
            var rideWorld = screen.GetNode<RideWorldView>("RideWorldView");
            var hud = screen.GetNode<HudController>("HudController");
            hud.Show();
            // In the sandbox the optional FIT path may be unavailable; the production debug jump
            // applies immediately in that case, otherwise ConfirmDebugDistanceJump stops it first.
            await Settle(2);
            var startPanel = screen.GetNode<StartPanelController>("StartPanel");
            foreach (var profileWindow in startPanel.GetChildren().OfType<Window>()) profileWindow.Hide();
            startPanel.Hide();
            session.JumpToRouteDistance("699.672");
            session.ConfirmDebugDistanceJump();
            await Settle(6);
            if (rideWorld.RouteGeometry is null) throw new InvalidOperationException("RideScreen route did not load.");

            foreach (var size in new[] { new Vector2I(1500, 900), new Vector2I(1920, 1080) })
            foreach (var debug in new[] { true, false })
            {
                DisplayServer.WindowSetSize(size);
                hud.SetDebugDisplayEnabled(debug);
                await Settle(3);
                foreach (var view in Views)
                {
                    rideWorld.SelectCameraInspectionView(view);
                    await Settle(8);
                    VerifyProjectedBounds(rideWorld, hud, size, debug, view);
                    await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                    var image = GetViewport().GetTexture().GetImage();
                    var path = Path.Combine(output, $"{size.X}x{size.Y}-debug-{(debug ? "on" : "off")}-{view}.png");
                    var error = image.SavePng(path);
                    if (error != Error.Ok) throw new IOException($"Could not save ride review image {path}: {error}.");
                }
            }
            GD.Print($"[RideCameraSceneReview] PASS: rendered actual RideScreen/HUD at 1500x900 and 1920x1080, all six presets, debug on/off. Evidence: {output}");
            GetTree().Quit(0);
        }
        catch (Exception error)
        {
            GD.PushError($"[RideCameraSceneReview] FAIL: {error}");
            GetTree().Quit(1);
        }
    }

    private void VerifyProjectedBounds(RideWorldView world, HudController hud, Vector2I size, bool debug, RiderCameraView view)
    {
        var camera = world.InspectionCamera;
        var bounds = world.PlayerRider.GetWorldVisualBounds();
        var safe = hud.GetCameraSafeViewportRect();
        var viewport = camera.GetViewport().GetVisibleRect().Size;
        for (var i = 0; i < 8; i++)
        {
            var point = bounds.Position + new Vector3((i & 1) == 0 ? 0 : bounds.Size.X,
                (i & 2) == 0 ? 0 : bounds.Size.Y, (i & 4) == 0 ? 0 : bounds.Size.Z);
            var projected = camera.UnprojectPosition(point);
            var normalized = projected / viewport;
            const float margin = .018f;
            if (!float.IsFinite(normalized.X) || !float.IsFinite(normalized.Y) ||
                normalized.X < safe.Position.X + margin || normalized.X > safe.End.X - margin ||
                normalized.Y < safe.Position.Y + margin || normalized.Y > safe.End.Y - margin)
                throw new InvalidOperationException($"Projected rider/bike bounds leave the HUD-safe region at {size}, debug={debug}, view={view}: point={normalized}, safe={safe}, camera={camera.GlobalPosition}, desired={camera.DesiredPosition}, cameraDistance={camera.RiderToCameraDistance:F2}m, clearance={camera.ClearanceResult}, rider={world.PlayerRider.GlobalPosition}, bounds={bounds}.");
        }
    }

    private async Task Settle(int frames)
    {
        for (var i = 0; i < frames; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }
}
