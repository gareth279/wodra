using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Godot;

/// <summary>Review focused rider finishing in the production RideScreen and imported GLB.</summary>
public partial class RiderFinishingSceneReviewTest : Node
{
    public override async void _Ready()
    {
        try
        {
            DisplayServer.WindowSetSize(new Vector2I(1500, 900));
            var output = Path.Combine(ProjectSettings.GlobalizePath("res://../.."), "artifacts", "rider-finishing-alpha47-reviewed");
            Directory.CreateDirectory(output);
            var screen = GD.Load<PackedScene>("res://Scenes/RideScreen.tscn").Instantiate();
            AddChild(screen);
            await Settle(12);
            GameBootstrap.Settings.DebugModeEnabled = true;
            GameBootstrap.ConfigureFakeTrainerMode();
            var session = screen.GetNode<RideSessionNode>("RideSessionNode");
            if (!await session.StartTrainerSessionAsync(RouteCatalog.GetById("dunedin-rolling-loop")))
                throw new InvalidOperationException("Could not start Rolling City in RideScreen.");
            var world = screen.GetNode<RideWorldView>("RideWorldView");
            var hud = screen.GetNode<HudController>("HudController");
            hud.Show();
            await Settle(12);
            foreach (var window in screen.GetNode<StartPanelController>("StartPanel").GetChildren().OfType<Window>()) window.Hide();
            screen.GetNode<StartPanelController>("StartPanel").Hide();
            session.JumpToRouteDistance("738.5");
            session.ConfirmDebugDistanceJump();
            await Settle(24);

            var camera = world.InspectionCamera;
            foreach (var debug in new[] { true, false })
            {
                hud.SetDebugDisplayEnabled(debug);
                await Settle(12);
                world.SelectCameraInspectionView(RiderCameraView.Side);
                await Settle(24);
                VerifySideCenter(world, hud, debug);
                await Save(output, $"ride-side-debug-{(debug ? "on" : "off")}.png");
            }
            world.SelectCameraInspectionView(RiderCameraView.GameplayRefined);
            await Settle(24);
            await Save(output, "ride-chase.png");

            // Freeze normal ride updates only while temporarily using the actual RideScreen
            // rider and imported GLB for close fit and accessory inspection.
            world.SetProcess(false);
            foreach (var child in screen.GetChildren())
                if (child is CanvasItem overlay) overlay.Hide();
            var rider = world.PlayerRider;
            if (!rider.UsesImportedModel) throw new InvalidOperationException("RideScreen used the procedural rider fallback.");
            foreach (var other in FindAll<CyclistVisual>(world)) other.Visible = other == rider;
            var skeleton = Find<Skeleton3D>(rider) ?? throw new InvalidOperationException("Imported rider skeleton missing.");
            var human = Find<MeshInstance3D>(rider, "Human") ?? throw new InvalidOperationException("Weighted human mesh missing.");
            var surfaceCount = human.Mesh?.GetSurfaceCount() ?? 0;
            var eyeSurfaceCount = 0;
            for (var surface = 0; surface < surfaceCount; surface++)
            {
                var source = human.Mesh!.SurfaceGetMaterial(surface);
                GD.Print($"[RiderFinishingSceneReview] human surface={surface} material={source?.ResourceName}");
                if (source?.ResourceName.Contains("Rider Eye", StringComparison.OrdinalIgnoreCase) == true)
                    eyeSurfaceCount++;
            }
            if (surfaceCount != 2 || eyeSurfaceCount != 1)
                throw new InvalidOperationException($"Expected outfit and preserved eye materials; surfaces={surfaceCount}, eyeSurfaces={eyeSurfaceCount}.");
            var headIndex = skeleton.FindBone("head");
            if (headIndex < 0) throw new InvalidOperationException("Imported head bone missing.");
            GD.Print($"[RiderFinishingSceneReview] Human local bounds={human.GetAabb()}, node={human.Transform}, headBone={skeleton.GetBoneGlobalPose(headIndex)}");
            var head = skeleton.GlobalTransform * skeleton.GetBoneGlobalPose(headIndex).Origin;
            var frontWheel = Find<Node3D>(rider, "FrontWheelSpin") ?? throw new InvalidOperationException("Front wheel anchor missing.");
            var rearWheel = Find<Node3D>(rider, "RearWheelSpin") ?? throw new InvalidOperationException("Rear wheel anchor missing.");
            var bikeForward = (frontWheel.GlobalPosition - rearWheel.GlobalPosition).Normalized();
            var bikeSide = bikeForward.Cross(Vector3.Up).Normalized();
            var savedFov = camera.Fov;
            camera.Fov = 34f;
            camera.MakeCurrent();
            var headTarget = head + Vector3.Up * .04f;
            rider.SetHelmetVisible(false);
            AimAtHead(camera, bikeForward, bikeSide, headTarget, "front");
            await Settle(2);
            foreach (var angle in new[] { "front", "side", "three-quarter" })
            {
                AimAtHead(camera, bikeForward, bikeSide, headTarget, angle);
                await Settle(3);
                await Save(output, $"head-{angle}-helmet-off.png");
            }
            rider.SetHelmetVisible(true);
            foreach (var angle in new[] { "front", "side", "three-quarter" })
            {
                AimAtHead(camera, bikeForward, bikeSide, headTarget, angle);
                await Settle(3);
                await Save(output, $"head-{angle}-helmet-on.png");
            }

            var leftHood = Find<GeometryInstance3D>(rider, "BrakeHoodL")
                ?? throw new InvalidOperationException("Left runtime brake hood missing.");
            var rightHood = Find<GeometryInstance3D>(rider, "BrakeHoodR")
                ?? throw new InvalidOperationException("Right runtime brake hood missing.");
            camera.Fov = 24f;
            foreach (var (hood, side) in new[] { (leftHood, -1f), (rightHood, 1f) })
            {
                var hoodCenter = hood.GlobalTransform * hood.GetAabb().GetCenter();
                var wristIndex = skeleton.FindBone(side < 0 ? "wrist.L" : "wrist.R");
                if (wristIndex < 0) throw new InvalidOperationException("Imported wrist bone missing.");
                var wrist = skeleton.GlobalTransform * skeleton.GetBoneGlobalPose(wristIndex).Origin;
                var contactError = wrist.DistanceTo(hoodCenter);
                GD.Print($"[RiderFinishingSceneReview] {sideName(side)} wrist-to-hood anchor distance={contactError:F3} m");
                if (contactError > .11f)
                    throw new InvalidOperationException($"{sideName(side)} wrist moved too far from its runtime hood anchor: {contactError:F3} m.");
                var target = hoodCenter.Lerp(wrist, .48f);
                // Use a rider-scale, nearly lateral view instead of a macro
                // angle that foreshortens fingers and makes the hood appear
                // larger than the contact surfaces.
                var view = (bikeSide * side + bikeForward * .18f).Normalized();
                camera.GlobalPosition = target + view * .52f + Vector3.Up * .035f;
                camera.LookAt(target, Vector3.Up);
                await Settle(3);
                await Save(output, side < 0 ? "hand-left-hood-close.png" : "hand-right-hood-close.png");
            }
            camera.Fov = savedFov;
            GD.Print($"[RiderFinishingSceneReview] PASS: imported GLB in RideScreen; evidence: {output}");
            GetTree().Quit(0);
        }
        catch (Exception error)
        {
            GD.PushError($"[RiderFinishingSceneReview] FAIL: {error}");
            GetTree().Quit(1);
        }
    }

    private static T? Find<T>(Node root, string? name = null) where T : Node
    {
        if (root is T match && (name is null || match.Name == name)) return match;
        foreach (var child in root.GetChildren())
            if (Find<T>(child, name) is { } result) return result;
        return null;
    }

    private static System.Collections.Generic.IEnumerable<T> FindAll<T>(Node root) where T : Node
    {
        if (root is T match) yield return match;
        foreach (var child in root.GetChildren())
            foreach (var found in FindAll<T>(child)) yield return found;
    }

    private void AimAtHead(Camera3D camera, Vector3 forward, Vector3 side, Vector3 head, string angle)
    {
        var direction = angle switch
        {
            "front" => forward,
            "side" => side,
            _ => (forward + side).Normalized()
        };
        camera.GlobalPosition = head + direction * .58f + Vector3.Up * .015f;
        camera.LookAt(head, Vector3.Up);
    }

    private void VerifySideCenter(RideWorldView world, HudController hud, bool debug)
    {
        var camera = world.InspectionCamera;
        var normalized = camera.UnprojectPosition(world.PlayerRider.GetWorldVisualBounds().GetCenter()) /
            camera.GetViewport().GetVisibleRect().Size;
        var safe = hud.GetCameraSafeViewportRect();
        var offset = normalized - safe.GetCenter();
        GD.Print($"[RiderFinishingSceneReview] side centered debug={debug}: subject={normalized}, safeCenter={safe.GetCenter()}, offset={offset}");
        if (Math.Abs(offset.X) > .10f || Math.Abs(offset.Y) > .10f)
            throw new InvalidOperationException($"Settled Side view is off-center with debug={debug}: subject={normalized}, safe={safe}.");
    }

    private async Task Save(string root, string name)
    {
        // Wait for the next tree frame; FramePostDraw may stop firing after
        // the production HUD is hidden in the headless renderer.
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        var path = Path.Combine(root, name);
        var texture = GetViewport().GetTexture()
            ?? throw new InvalidOperationException("Review viewport has no render texture.");
        var image = texture.GetImage()
            ?? throw new InvalidOperationException("Review viewport returned no rendered image.");
        var error = image.SavePng(path);
        if (error != Error.Ok) throw new IOException($"Could not save {path}: {error}");
        GD.Print($"[RiderFinishingSceneReview] captured {path}");
    }

    private static string sideName(float side) => side < 0 ? "Left" : "Right";

    private async Task Settle(int frames)
    {
        for (var i = 0; i < frames; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }
}
