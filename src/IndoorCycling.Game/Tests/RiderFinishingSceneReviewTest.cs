using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Godot;

/// <summary>Review focused rider finishing in the production RideScreen and imported GLB.</summary>
public partial class RiderFinishingSceneReviewTest : Node
{
    private Viewport? _captureViewport;
    private bool _skipCaptures;

    public override async void _Ready()
    {
        try
        {
            _skipCaptures = System.Environment.GetEnvironmentVariable("WODRA_VISUAL_REVIEW_SKIP_CAPTURES") == "1";
            DisplayServer.WindowSetSize(new Vector2I(1500, 900));
            var output = System.Environment.GetEnvironmentVariable("WODRA_VISUAL_REVIEW_OUTPUT");
            if (string.IsNullOrWhiteSpace(output))
                output = Path.Combine(ProjectSettings.GlobalizePath("res://../.."), "artifacts", "alpha48-hand-correction", "cockpit-v22", "godot-candidate");
            else
                output = Path.GetFullPath(output);
            Directory.CreateDirectory(output);
            var screen = GD.Load<PackedScene>("res://Scenes/RideScreen.tscn").Instantiate();
            var requestedResolution = System.Environment.GetEnvironmentVariable("WODRA_VISUAL_REVIEW_RESOLUTION");
            var dimensions = requestedResolution?.Split('x', 2, StringSplitOptions.TrimEntries);
            if (dimensions is { Length: 2 } &&
                int.TryParse(dimensions[0], out var width) &&
                int.TryParse(dimensions[1], out var height) &&
                width >= 640 && height >= 360)
            {
                // Render production RideScreen into a Godot viewport target so
                // review evidence can exceed the host display resolution.
                var capture = new SubViewport
                {
                    Name = "RoadBikeReviewViewport",
                    Size = new Vector2I(width, height),
                    OwnWorld3D = true,
                    RenderTargetUpdateMode = SubViewport.UpdateMode.Always
                };
                AddChild(capture);
                capture.AddChild(screen);
                _captureViewport = capture;
            }
            else
            {
                AddChild(screen);
                _captureViewport = screen.GetViewport();
            }
            await Settle(12);
            GameBootstrap.Settings.DebugModeEnabled = true;
            GameBootstrap.Settings.Recording.OutputFolder = Path.Combine(output, "fit");
            GameBootstrap.ConfigureFakeTrainerMode();
            var session = screen.GetNode<RideSessionNode>("RideSessionNode");
            if (!await session.StartTrainerSessionAsync(RouteCatalog.GetById("dunedin-rolling-loop")))
                throw new InvalidOperationException("Could not start Rolling City in RideScreen.");
            var world = screen.GetNode<RideWorldView>("RideWorldView");
            var hud = screen.GetNode<HudController>("HudController");
            // Keep the model review camera clear of a collectible card that can
            // otherwise fill the front-three-quarter frame at the fixed test distance.
            world.GetNodeOrNull<Node3D>("CollectibleCats")?.Hide();
            hud.Show();
            await Settle(12);
            foreach (var window in screen.GetNode<StartPanelController>("StartPanel").GetChildren().OfType<Window>()) window.Hide();
            screen.GetNode<StartPanelController>("StartPanel").Hide();
            session.JumpToRouteDistance("738.5");
            session.ConfirmDebugDistanceJump();
            hud.HideDebugJumpConfirmation();
            hud.SetRecordingState(false, false);
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
            world.SelectCameraInspectionView(RiderCameraView.FrontThreeQuarter);
            await Settle(24);
            await Save(output, "ride-front-three-quarter.png");

            // Freeze normal ride updates only while temporarily using the actual RideScreen
            // rider and imported GLB for close fit and accessory inspection.
            world.SetProcess(false);
            foreach (var child in screen.GetChildren())
                if (child is CanvasItem overlay) overlay.Hide();
            var rider = world.PlayerRider;
            if (!rider.UsesImportedModel) throw new InvalidOperationException("RideScreen used the procedural rider fallback.");
            // Match Blender frame 1 exactly (glTF t=0) while inspecting source
            // deformation. The ride clock is frozen below; seeking this imported
            // animation here avoids comparing unrelated crank phases.
            rider.SetProcess(false);
            var riderAnimation = Find<AnimationPlayer>(rider)
                ?? throw new InvalidOperationException("Imported rider AnimationPlayer missing.");
            var pedalClipName = riderAnimation.GetAnimationList()
                .First(name => name.ToString().Contains("PedalCycle", StringComparison.OrdinalIgnoreCase));
            riderAnimation.Seek(0.0, true);
            await Settle(2);
            foreach (var other in FindAll<CyclistVisual>(world)) other.Visible = other == rider;
            var skeleton = Find<Skeleton3D>(rider) ?? throw new InvalidOperationException("Imported rider skeleton missing.");
            var human = Find<MeshInstance3D>(rider, "Human") ?? throw new InvalidOperationException("Weighted human mesh missing.");
            var surfaceCount = human.Mesh?.GetSurfaceCount() ?? 0;
            var eyeSurfaceCount = 0;
            for (var surface = 0; surface < surfaceCount; surface++)
            {
                var source = human.Mesh!.SurfaceGetMaterial(surface);
                GD.Print($"[RiderFinishingSceneReview] human surface={surface} material={source?.ResourceName}");
                if (source is StandardMaterial3D standard)
                    GD.Print($"[RiderFinishingSceneReview] surface={surface} albedo={standard.AlbedoColor} roughness={standard.Roughness} metallic={standard.Metallic}");
                if (source?.ResourceName.Contains("Rider Eye", StringComparison.OrdinalIgnoreCase) == true)
                    eyeSurfaceCount++;
            }
            if (surfaceCount != 2 || eyeSurfaceCount != 1)
                throw new InvalidOperationException($"Expected outfit and preserved eye materials; surfaces={surfaceCount}, eyeSurfaces={eyeSurfaceCount}.");
            var garmentDetails = Find<MeshInstance3D>(rider, "RiderRoadKitDetails")
                ?? throw new InvalidOperationException("Skinned jersey and bib finishing details are missing.");
            if ((garmentDetails.Mesh?.GetSurfaceCount() ?? 0) < 3)
                throw new InvalidOperationException("Expected collar/zip, sleeve, and bib-gripper detail materials.");
            var headIndex = skeleton.FindBone("head");
            if (headIndex < 0) throw new InvalidOperationException("Imported head bone missing.");
            GD.Print($"[RiderFinishingSceneReview] Human local bounds={human.GetAabb()}, node={human.Transform}, headBone={skeleton.GetBoneGlobalPose(headIndex)}");
            var head = skeleton.GlobalTransform * skeleton.GetBoneGlobalPose(headIndex).Origin;
            var frontWheel = Find<Node3D>(rider, "FrontWheelSpin") ?? throw new InvalidOperationException("Front wheel anchor missing.");
            var rearWheel = Find<Node3D>(rider, "RearWheelSpin") ?? throw new InvalidOperationException("Rear wheel anchor missing.");
            var bikeForward = (frontWheel.GlobalPosition - rearWheel.GlobalPosition).Normalized();
            var bikeSide = bikeForward.Cross(Vector3.Up).Normalized();
            var savedFov = camera.Fov;
            var bikeCenter = (frontWheel.GlobalPosition + rearWheel.GlobalPosition) * .5f + Vector3.Up * .30f;
            camera.Fov = 42f;
            camera.GlobalPosition = bikeCenter + bikeSide * 2.65f + bikeForward * .08f + Vector3.Up * .54f;
            camera.LookAt(bikeCenter + Vector3.Up * .42f, Vector3.Up);
            await Settle(3);
            await Save(output, "fit-rider-side-full.png");
            camera.GlobalPosition = bikeCenter + bikeSide * 1.55f + bikeForward * 2.05f + Vector3.Up * .92f;
            camera.LookAt(bikeCenter + Vector3.Up * .42f, Vector3.Up);
            await Settle(3);
            await Save(output, "fit-rider-front-three-quarter-full.png");
            rider.SetRiderVisible(false);
            camera.Fov = 31f;
            camera.GlobalPosition = bikeCenter + bikeSide * 2.05f + Vector3.Up * .06f;
            camera.LookAt(bikeCenter, Vector3.Up);
            await Settle(3);
            await Save(output, "bike-only-side.png");
            camera.Fov = 34f;
            camera.GlobalPosition = bikeCenter + bikeSide * 1.10f + bikeForward * 1.25f + Vector3.Up * .46f;
            camera.LookAt(bikeCenter, Vector3.Up);
            await Settle(3);
            await Save(output, "bike-only-front-three-quarter.png");
            rider.SetRiderVisible(true);
            // Keep the complete head and neck visible without the perspective
            // exaggeration of the earlier 0.58 m macro framing.
            camera.Fov = 46f;
            camera.MakeCurrent();
            var headTarget = head + Vector3.Up * .045f;
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

            var leftHood = Find<Node3D>(rider, "Contact_LeftHood")
                ?? throw new InvalidOperationException("Left imported hood contact missing.");
            var rightHood = Find<Node3D>(rider, "Contact_RightHood")
                ?? throw new InvalidOperationException("Right imported hood contact missing.");
            var saddle = Find<Node3D>(rider, "Contact_Saddle")
                ?? throw new InvalidOperationException("Imported saddle contact missing.");
            var leftPedal = Find<Node3D>(rider, "Contact_LeftPedal")
                ?? throw new InvalidOperationException("Imported left pedal contact missing.");
            var rightPedal = Find<Node3D>(rider, "Contact_RightPedal")
                ?? throw new InvalidOperationException("Imported right pedal contact missing.");
            // The replacement sculpt is one rigid, posed grip per wrist. The
            // former MPFB metacarpal bones remain for compatibility but no
            // longer describe the rendered hand. These exported markers are
            // at the source mesh's palm support points and follow its wrist.
            var leftPalmContact = skeleton.FindBone("palm_contact.L");
            var rightPalmContact = skeleton.FindBone("palm_contact.R");
            var leftFootBone = skeleton.FindBone("foot.L");
            var rightFootBone = skeleton.FindBone("foot.R");
            var pelvisBones = new[] { skeleton.FindBone("pelvis.L"), skeleton.FindBone("pelvis.R") }.Where(i => i >= 0).ToArray();
            if (leftPalmContact < 0 || rightPalmContact < 0 ||
                leftFootBone < 0 || rightFootBone < 0 || pelvisBones.Length == 0)
                throw new InvalidOperationException("Sculpted palm-contact, pelvis, or pedal-fit bones are missing.");
            var maxLeftPalm = 0f;
            var maxRightPalm = 0f;
            var maxPelvis = 0f;
            var minFootTravel = float.PositiveInfinity;
            var maxFootTravel = 0f;
            var leftFootAtStart = Vector3.Zero;
            var rightFootAtStart = Vector3.Zero;
            var clipLength = riderAnimation.GetAnimation(pedalClipName).Length;
            // Sample the entire imported cadence cycle, rather than relying on
            // a single still pose that could hide hand or pedal drift.
            const int fitSamples = 72;
            for (var sample = 0; sample < fitSamples; sample++)
            {
                riderAnimation.Seek(clipLength * sample / fitSamples, true);
                await Settle(1);
                var leftPalm = skeleton.GlobalTransform * skeleton.GetBoneGlobalPose(leftPalmContact).Origin;
                var rightPalm = skeleton.GlobalTransform * skeleton.GetBoneGlobalPose(rightPalmContact).Origin;
                var leftFoot = skeleton.GlobalTransform * skeleton.GetBoneGlobalPose(leftFootBone).Origin;
                var rightFoot = skeleton.GlobalTransform * skeleton.GetBoneGlobalPose(rightFootBone).Origin;
                var pelvis = pelvisBones.Select(i => skeleton.GlobalTransform * skeleton.GetBoneGlobalPose(i).Origin)
                    .Aggregate(Vector3.Zero, (sum, p) => sum + p) / pelvisBones.Length;
                maxLeftPalm = Math.Max(maxLeftPalm, leftPalm.DistanceTo(leftHood.GlobalPosition));
                maxRightPalm = Math.Max(maxRightPalm, rightPalm.DistanceTo(rightHood.GlobalPosition));
                maxPelvis = Math.Max(maxPelvis, pelvis.DistanceTo(saddle.GlobalPosition));
                if (sample == 0)
                {
                    leftFootAtStart = leftFoot;
                    rightFootAtStart = rightFoot;
                }
                else
                {
                    minFootTravel = Math.Min(minFootTravel, Math.Min(leftFoot.DistanceTo(leftFootAtStart), rightFoot.DistanceTo(rightFootAtStart)));
                    maxFootTravel = Math.Max(maxFootTravel, Math.Max(leftFoot.DistanceTo(leftFootAtStart), rightFoot.DistanceTo(rightFootAtStart)));
                }
            }
            GD.Print($"[RiderFinishingSceneReviewTest] full-cycle max sculpted palm-contact-to-hood L={maxLeftPalm:F3}m R={maxRightPalm:F3}m; pelvis-to-saddle={maxPelvis:F3}m; ankle-bone travel={minFootTravel:F3}-{maxFootTravel:F3}m");
            if (maxLeftPalm > .045f || maxRightPalm > .045f)
                throw new InvalidOperationException($"Hands leave the hood contact zones during PedalCycle (L={maxLeftPalm:F3}m, R={maxRightPalm:F3}m).");
            if (maxPelvis > .18f)
                throw new InvalidOperationException($"Pelvis left the saddle fit envelope ({maxPelvis:F3}m).");
            if (maxFootTravel < .05f)
                throw new InvalidOperationException("PedalCycle did not animate the feet through the full cycle.");
            riderAnimation.Seek(0.0, true);
            await Settle(2);
            camera.Fov = 32f;
            var wasHumanVisible = human.Visible;
            human.Visible = false;
            rider.SetHelmetVisible(false);
            var leftHoodCenter = leftHood.GlobalPosition;
            var rightHoodCenter = rightHood.GlobalPosition;
            var cockpitTarget = (leftHoodCenter + rightHoodCenter) * .5f;
            GD.Print($"[RiderFinishingSceneReview] hood centers L={leftHoodCenter} R={rightHoodCenter} target={cockpitTarget}");
            camera.Fov = 27f;
            camera.GlobalPosition = cockpitTarget + bikeSide * 1.0f + Vector3.Up * .12f;
            camera.LookAt(cockpitTarget, Vector3.Up);
            await Settle(3);
            await Save(output, "cockpit-no-hands-side.png");
            camera.Fov = 36f;
            camera.GlobalPosition = cockpitTarget + Vector3.Up * .72f;
            camera.LookAt(cockpitTarget, bikeForward);
            await Settle(3);
            await Save(output, "cockpit-no-hands-top.png");
            camera.Fov = 30f;
            camera.GlobalPosition = cockpitTarget + bikeForward * .80f + bikeSide * .48f + Vector3.Up * .42f;
            camera.LookAt(cockpitTarget, Vector3.Up);
            await Settle(3);
            await Save(output, "cockpit-no-hands-front-three-quarter.png");
            camera.Fov = 52f;
            camera.GlobalPosition = cockpitTarget - bikeForward * .48f + Vector3.Up * .24f;
            camera.LookAt(cockpitTarget + bikeForward * .10f, Vector3.Up);
            await Settle(3);
            await Save(output, "cockpit-rider-view.png");
            foreach (var (hood, side) in new[] { (leftHood, -1f), (rightHood, 1f) })
            {
                var hoodCenter = hood.GlobalPosition;
                var view = (bikeSide * side * .62f + bikeForward * .52f + Vector3.Up * .18f).Normalized();
                camera.Fov = 24f;
                camera.GlobalPosition = hoodCenter + view * .52f;
                camera.LookAt(hoodCenter, Vector3.Up);
                await Settle(3);
                await Save(output, side < 0 ? "hood-left-no-hands-close.png" : "hood-right-no-hands-close.png");
            }
            human.Visible = wasHumanVisible;
            rider.SetHelmetVisible(true);
            foreach (var (hood, side) in new[] { (leftHood, -1f), (rightHood, 1f) })
            {
                var hoodCenter = hood.GlobalPosition;
                // Aim at the palm bones and actual hood mesh. Wrist-to-hood
                // origin distance is not used as a contact acceptance check.
                var palmBone = side < 0 ? leftPalmContact : rightPalmContact;
                var palm = skeleton.GlobalTransform * skeleton.GetBoneGlobalPose(palmBone).Origin;
                var target = hoodCenter.Lerp(palm, .58f);
                // A near-lateral, rider-scale view shows the palm, fingers,
                // brake hood and lever together instead of clipping the hand.
                var view = (bikeSide * side * .58f + bikeForward * .72f + Vector3.Up * .12f).Normalized();
                camera.GlobalPosition = target + view * .52f;
                camera.LookAt(target, Vector3.Up);
                await Settle(3);
                await Save(output, side < 0 ? "hand-left-hood-close.png" : "hand-right-hood-close.png");

                // An elevated oblique view shows the palm surface seated on
                // the hood shelf and the fingers wrapping its outside edge.
                var elevated = (bikeSide * side * .42f + bikeForward * .48f + Vector3.Up * .78f).Normalized();
                camera.GlobalPosition = target + elevated * .58f;
                camera.LookAt(target, Vector3.Up);
                await Settle(3);
                await Save(output, side < 0 ? "hand-left-hood-elevated.png" : "hand-right-hood-elevated.png");
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
        camera.GlobalPosition = head + direction * .95f + Vector3.Up * .02f;
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
        if (_skipCaptures) return;
        // Wait for the next tree frame; FramePostDraw may stop firing after
        // the production HUD is hidden in the headless renderer.
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        var path = Path.Combine(root, name);
        var texture = (_captureViewport ?? GetViewport()).GetTexture()
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
