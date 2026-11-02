using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Godot;

/// <summary>Review focused rider finishing in the production RideScreen and imported GLB.</summary>
public partial class RiderFinishingSceneReviewTest : Node
{
    private Viewport? _captureViewport;
    private bool _skipCaptures;

    private static float NearestHelmetVertexDistance(MeshInstance3D helmetMesh, Vector3 worldPoint)
    {
        var closest = float.PositiveInfinity;
        var mesh = helmetMesh.Mesh ?? throw new InvalidOperationException("Rigid helmet mesh is unavailable.");
        for (var surface = 0; surface < mesh.GetSurfaceCount(); surface++)
        {
            var vertices = mesh.SurfaceGetArrays(surface)[(int)Mesh.ArrayType.Vertex].AsVector3Array();
            foreach (var vertex in vertices)
                closest = Math.Min(closest, (helmetMesh.GlobalTransform * vertex).DistanceTo(worldPoint));
        }
        return closest;
    }

    private static bool PhysicalHelmetFeaturesFaceForward(Vector3 brow, Vector3 rearTail,
        Vector3 eyeCenter, Vector3 faceForward) =>
        (brow - eyeCenter).Dot(faceForward) > 0f && (rearTail - eyeCenter).Dot(faceForward) < 0f;

    private static Vector3[] SkinVerticesToWorld(Skeleton3D skeleton, MeshInstance3D mesh,
        Vector3[] vertices, int[] boneIndices, float[] weights)
    {
        var skin = mesh.Skin ?? throw new InvalidOperationException("Supplied helmet mesh has no imported skin.");
        if (boneIndices.Length != vertices.Length * 4 || weights.Length != boneIndices.Length ||
            skin.GetBindCount() != skeleton.GetBoneCount())
            throw new InvalidOperationException("Imported helmet vertex skin arrays do not match its skeleton binds.");
        var transforms = new Transform3D[skeleton.GetBoneCount()];
        for (var bone = 0; bone < transforms.Length; bone++)
            transforms[bone] = skeleton.GlobalTransform * skeleton.GetBoneGlobalPose(bone) * skin.GetBindPose(bone);
        var result = new Vector3[vertices.Length];
        for (var vertex = 0; vertex < vertices.Length; vertex++)
        {
            var skinned = Vector3.Zero;
            for (var influence = 0; influence < 4; influence++)
            {
                var offset = vertex * 4 + influence;
                var weight = weights[offset];
                if (weight <= 0f) continue;
                var bone = boneIndices[offset];
                if ((uint)bone >= transforms.Length)
                    throw new InvalidOperationException($"Imported helmet vertex {vertex} references invalid bone {bone}.");
                skinned += transforms[bone] * vertices[vertex] * weight;
            }
            result[vertex] = skinned;
        }
        return result;
    }

    private static Aabb SkinnedMeshBoundsInWorld(Skeleton3D skeleton, MeshInstance3D mesh)
    {
        var points = new List<Vector3>();
        for (var surface = 0; surface < mesh.Mesh!.GetSurfaceCount(); surface++)
        {
            var arrays = mesh.Mesh.SurfaceGetArrays(surface);
            points.AddRange(SkinVerticesToWorld(skeleton, mesh,
                arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array(),
                arrays[(int)Mesh.ArrayType.Bones].AsInt32Array(),
                arrays[(int)Mesh.ArrayType.Weights].AsFloat32Array()));
        }
        if (points.Count == 0) throw new InvalidOperationException($"{mesh.Name} has no skinned vertices.");
        var minimum = points.Aggregate(new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity),
            (current, point) => current.Min(point));
        var maximum = points.Aggregate(new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity),
            (current, point) => current.Max(point));
        return new Aabb(minimum, maximum - minimum);
    }

    private static (Vector3 Toe, Vector3 Heel, Vector3 AnkleOpening, float WidthDifference) FindPhysicalShoeFeatures(
        Skeleton3D skeleton, MeshInstance3D shoe, Vector3 bikeForward, Vector3 soleUp, Vector3 openingReference)
    {
        var points = new List<Vector3>();
        for (var surface = 0; surface < shoe.Mesh!.GetSurfaceCount(); surface++)
        {
            var arrays = shoe.Mesh.SurfaceGetArrays(surface);
            points.AddRange(SkinVerticesToWorld(skeleton, shoe,
                arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array(),
                arrays[(int)Mesh.ArrayType.Bones].AsInt32Array(),
                arrays[(int)Mesh.ArrayType.Weights].AsFloat32Array()));
        }
        var side = bikeForward.Cross(Vector3.Up).Normalized();
        var along = points.Select(point => point.Dot(bikeForward)).ToArray();
        var across = points.Select(point => point.Dot(side)).ToArray();
        var low = along.Min(); var high = along.Max();
        var depth = (high - low) * .12f;
        var rear = Enumerable.Range(0, points.Count).Where(i => along[i] <= low + depth).ToArray();
        var front = Enumerable.Range(0, points.Count).Where(i => along[i] >= high - depth).ToArray();
        var rearWidth = across[rear.MaxBy(i => across[i])] - across[rear.MinBy(i => across[i])];
        var frontWidth = across[front.MaxBy(i => across[i])] - across[front.MinBy(i => across[i])];
        var difference = Math.Abs(frontWidth - rearWidth);
        if (difference < .008f)
            throw new InvalidOperationException($"{shoe.Name} toe box and heel cup are not distinguishable by actual mesh width: {frontWidth:F3}/{rearWidth:F3}m.");
        var toeIndexes = frontWidth > rearWidth ? front : rear;
        var heelIndexes = frontWidth > rearWidth ? rear : front;
        var toe = toeIndexes.Aggregate(Vector3.Zero, (sum, index) => sum + points[index]) / toeIndexes.Length;
        var heel = heelIndexes.Aggregate(Vector3.Zero, (sum, index) => sum + points[index]) / heelIndexes.Length;
        // Resolve the actual heel-side upper opening from a rear cross-section
        // above the outsole. The ring is independent of the ankle and contact
        // markers, so this catches a correctly aimed shoe with a misplaced leg.
        var vertical = points.Select(point => point.Dot(soleUp)).ToArray();
        var lowestSole = vertical.Min();
        var rings = Enumerable.Range(0, points.Count)
            .GroupBy(index => (int)MathF.Round(along[index] / .015f))
            .Select(group => group.ToArray())
            .Where(indices => indices.Length >= 6 && indices.Max(index => vertical[index]) > lowestSole + .025f)
            .ToArray();
        if (rings.Length == 0)
            throw new InvalidOperationException($"{shoe.Name} has no measurable heel-side ankle opening in its skinned mesh.");
        var heelAlong = heel.Dot(bikeForward);
        var openingRing = rings.MinBy(indices =>
        {
            var center = indices.Aggregate(Vector3.Zero, (sum, index) => sum + points[index]) / indices.Length;
            var heelSide = Math.Abs(center.Dot(bikeForward) - heelAlong);
            return center.DistanceTo(openingReference) + Math.Max(0f, heelSide - .12f) * .5f;
        })!;
        var opening = openingRing.Aggregate(Vector3.Zero, (sum, index) => sum + points[index]) / openingRing.Length;
        return (toe, heel, opening, difference);
    }

    public override async void _Ready()
    {
        try
        {
            _skipCaptures = System.Environment.GetEnvironmentVariable("WODRA_VISUAL_REVIEW_SKIP_CAPTURES") == "1";
            DisplayServer.WindowSetSize(new Vector2I(1500, 900));
            var output = System.Environment.GetEnvironmentVariable("WODRA_VISUAL_REVIEW_OUTPUT");
            if (string.IsNullOrWhiteSpace(output))
                output = Path.Combine(ProjectSettings.GlobalizePath("res://../.."), "artifacts", "issue85-leg-failure", "after");
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
            GameBootstrap.Settings.DummyTrainer.CadenceRpm = 85;
            GameBootstrap.Settings.Recording.OutputFolder = Path.Combine(output, "fit");
            GameBootstrap.ConfigureFakeTrainerMode();
            var session = screen.GetNode<RideSessionNode>("RideSessionNode");
            if (!await session.StartTrainerSessionAsync(RouteCatalog.GetById("dunedin-one-way-oval-gardens")))
                throw new InvalidOperationException("Could not start Dunedin One-way: Oval to Gardens Loop in RideScreen.");
            var world = screen.GetNode<RideWorldView>("RideWorldView");
            var hud = screen.GetNode<HudController>("HudController");
            // Keep the model review camera clear of a collectible card that can
            // otherwise fill the front-three-quarter frame at the fixed test distance.
            Find<CollectibleCatLayer>(world, "CollectibleCats")?.Hide();
            hud.Show();
            await Settle(12);
            foreach (var window in screen.GetNode<StartPanelController>("StartPanel").GetChildren().OfType<Window>()) window.Hide();
            screen.GetNode<StartPanelController>("StartPanel").Hide();
            var liveRider = world.PlayerRider;
            var liveFrontWheel = Find<Node3D>(liveRider, "FrontWheelSpin")
                ?? throw new InvalidOperationException("Imported front wheel anchor missing for live leg inspection.");
            var liveRearWheel = Find<Node3D>(liveRider, "RearWheelSpin")
                ?? throw new InvalidOperationException("Imported rear wheel anchor missing for live leg inspection.");
            var camera = world.InspectionCamera;
            world.SelectCameraInspectionView(RiderCameraView.Side);
            session.JumpToRouteDistance("738.5");
            session.ConfirmDebugDistanceJump();
            hud.HideDebugJumpConfirmation();
            hud.SetRecordingState(false, false);
            await Settle(24);
            // Debug route loading can recreate the pickup layer after the ride
            // starts; hide it again so production fit screenshots show only the rider.
            Find<CollectibleCatLayer>(world, "CollectibleCats")?.Hide();
            await Settle(2);
            if (!_skipCaptures)
                await CaptureLiveLegPhases(output, world, liveRider, camera, hud, liveFrontWheel, liveRearWheel);

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
            var collectibleLayer = Find<CollectibleCatLayer>(world, "CollectibleCats");
            collectibleLayer?.SetProcess(false);
            collectibleLayer?.Hide();
            foreach (var child in screen.GetChildren())
                if (child is CanvasItem overlay) overlay.Hide();
            var rider = world.PlayerRider;
            if (!rider.UsesImportedModel) throw new InvalidOperationException("RideScreen used the procedural rider fallback.");
            rider.SetCustomization("Teal", "Charcoal");
            // Match Blender frame 1 exactly (glTF t=0) while inspecting source
            // deformation. The ride clock is frozen below; seeking this imported
            // animation here avoids comparing unrelated crank phases.
            rider.SetProcess(false);
            var riderAnimation = Find<AnimationPlayer>(rider)
                ?? throw new InvalidOperationException("Imported rider AnimationPlayer missing.");
            var activeRiderClipName = riderAnimation.GetAnimationList()
                .Where(name => name.ToString().Contains("PedalCycle", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(name => Math.Min(
                    CountBoneTrackKeys(riderAnimation.GetAnimation(name), "foot.L"),
                    CountBoneTrackKeys(riderAnimation.GetAnimation(name), "foot.R")))
                .First();
            GD.Print($"[RiderFinishingSceneReviewTest] animations={string.Join(";", riderAnimation.GetAnimationList()
                .Select(name => { var clip = riderAnimation.GetAnimation(name); return $"{name}:{clip.Length:F3}s/{clip.GetTrackCount()}tracks/foot={Enumerable.Range(0, clip.GetTrackCount()).Where(i => clip.TrackGetPath(i).ToString().Contains("foot.L", StringComparison.OrdinalIgnoreCase)).Sum(i => clip.TrackGetKeyCount(i))}"; }))}");
            var activeRiderClip = riderAnimation.GetAnimation(activeRiderClipName);
            if (Math.Min(CountBoneTrackKeys(activeRiderClip, "foot.L"),
                         CountBoneTrackKeys(activeRiderClip, "foot.R")) < 72)
                throw new InvalidOperationException($"Selected imported PedalCycle clip {activeRiderClipName} does not animate both feet through a full cycle.");
            var activeTrackCount = activeRiderClip.GetTrackCount();
            GD.Print($"[RiderFinishingSceneReviewTest] active clip={activeRiderClipName} " +
                $"tracks={activeTrackCount} footTracks={string.Join(";", Enumerable.Range(0, activeTrackCount)
                    .Where(index => activeRiderClip.TrackGetPath(index).ToString().Contains("foot", StringComparison.OrdinalIgnoreCase) ||
                                   activeRiderClip.TrackGetPath(index).ToString().Contains("lowerleg01", StringComparison.OrdinalIgnoreCase))
                    .Select(index => $"{activeRiderClip.TrackGetPath(index)}:{activeRiderClip.TrackGetKeyCount(index)}"))}");
            riderAnimation.Seek(0.0, true);
            await Settle(2);
            foreach (var other in FindAll<CyclistVisual>(world)) other.Visible = other == rider;
            var skeleton = Find<Skeleton3D>(rider) ?? throw new InvalidOperationException("Imported rider skeleton missing.");
            var human = Find<MeshInstance3D>(rider, "Human") ?? throw new InvalidOperationException("Weighted human mesh missing.");
            var legacyHelmet = Find<MeshInstance3D>(rider, "CyclingHelmet")
                ?? throw new InvalidOperationException("Imported rider's legacy helmet node is missing.");
            if (legacyHelmet.Visible)
                throw new InvalidOperationException("The legacy skinned helmet must stay hidden when the rigid replacement is attached.");
            var helmetAttachment = Find<BoneAttachment3D>(rider, "CyclingHelmetHeadAttachment")
                ?? throw new InvalidOperationException("Production rider is missing its rigid head-bone helmet attachment.");
            var rigidHelmetPose = Find<Node3D>(rider, "CyclingHelmetRigidPose")
                ?? throw new InvalidOperationException("Production rider is missing the rigid helmet local pose.");
            var rigidHelmetAssembly = Find<Node3D>(rider, "OriginalSuppliedHelmetRigidAssembly")
                ?? throw new InvalidOperationException("Production rider is missing the original supplied helmet assembly.");
            var physicalFrontBrow = Find<Node3D>(rider, "HelmetPhysicalFrontBrowMarker")
                ?? throw new InvalidOperationException("Original helmet's independently identified physical brow landmark is missing.");
            var physicalRearTail = Find<Node3D>(rider, "HelmetPhysicalRearTailMarker")
                ?? throw new InvalidOperationException("Original helmet's independently identified physical rear-tail landmark is missing.");
            var rigidHelmetMesh = Find<MeshInstance3D>(rigidHelmetAssembly)
                ?? throw new InvalidOperationException("Rigid supplied helmet mesh is missing from the assembly.");
            var shellMaterial = rigidHelmetMesh.GetSurfaceOverrideMaterial(0) as ShaderMaterial
                ?? throw new InvalidOperationException("The rigid helmet shell must use the UV-aware colour material.");
            if (shellMaterial.Shader?.Code.Contains("smoothstep(0.055, 0.18, luminance)", StringComparison.Ordinal) != true ||
                shellMaterial.GetShaderParameter("helmet_texture").AsGodotObject() is null)
                throw new InvalidOperationException("Helmet colour treatment must preserve the supplied texture and dark hardware mask.");
            var outfitMaterial = human.GetSurfaceOverrideMaterial(0) as ShaderMaterial
                ?? throw new InvalidOperationException("The imported clothing surface must keep using RiderOutfitShader.");
            rider.SetCustomization("Navy blue", "Red");
            var navy = outfitMaterial.GetShaderParameter("jersey_color").AsColor();
            var redHelmet = shellMaterial.GetShaderParameter("shell_color").AsColor();
            if (navy.B < navy.R || redHelmet.R < redHelmet.B)
                throw new InvalidOperationException("Rider profile colour selections did not reach the jersey and shell materials.");
            rider.SetCustomization("White", "Original purple/blue");
            if (rigidHelmetMesh.GetSurfaceOverrideMaterial(0) is not StandardMaterial3D)
                throw new InvalidOperationException("The original purple/blue option must restore the supplied helmet material.");
            rider.SetCustomization("Teal", "Charcoal");
            var helmetVertices = rigidHelmetMesh.Mesh is { } helmetMesh
                ? Enumerable.Range(0, helmetMesh.GetSurfaceCount()).Sum(surface =>
                    helmetMesh.SurfaceGetArrays(surface)[(int)Mesh.ArrayType.Vertex].AsVector3Array().Length)
                : 0;
            if (helmetAttachment.BoneName != "head" || !helmetAttachment.IsAncestorOf(rigidHelmetPose) ||
                !rigidHelmetPose.IsAncestorOf(rigidHelmetAssembly) ||
                !rigidHelmetAssembly.IsAncestorOf(physicalFrontBrow) ||
                !rigidHelmetAssembly.IsAncestorOf(physicalRearTail) || helmetVertices < 30000)
                throw new InvalidOperationException($"Rigid supplied helmet is not a complete assembly on the head bone ({helmetVertices} vertices).");
            var browMeshDistance = NearestHelmetVertexDistance(rigidHelmetMesh, physicalFrontBrow.GlobalPosition);
            var rearMeshDistance = NearestHelmetVertexDistance(rigidHelmetMesh, physicalRearTail.GlobalPosition);
            if (browMeshDistance > .03f || rearMeshDistance > .03f)
                throw new InvalidOperationException($"Physical helmet landmarks are detached from the imported mesh: " +
                    $"brow={browMeshDistance:F3}m rear={rearMeshDistance:F3}m.");
            var helmetHeadBone = skeleton.FindBone("head");
            if (helmetHeadBone < 0) throw new InvalidOperationException("Imported head bone missing.");
            var helmetBikeForward = (liveFrontWheel.GlobalPosition - liveRearWheel.GlobalPosition).Normalized();
            var riderUp = skeleton.GlobalTransform.Basis.Y.Normalized();
            var helmetSide = helmetBikeForward.Cross(riderUp).Normalized();

            var headPoints = new List<Vector3>();
            var eyePoints = new List<Vector3>();
            for (var surface = 0; surface < human.Mesh!.GetSurfaceCount(); surface++)
            {
                var humanArrays = human.Mesh.SurfaceGetArrays(surface);
                var vertices = humanArrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
                var bones = humanArrays[(int)Mesh.ArrayType.Bones].AsInt32Array();
                var weights = humanArrays[(int)Mesh.ArrayType.Weights].AsFloat32Array();
                if (vertices.Length == 0 || bones.Length != vertices.Length * 4 || weights.Length != bones.Length)
                    throw new InvalidOperationException($"Imported rider surface {surface} has incomplete skin data.");
                var worldPoints = SkinVerticesToWorld(skeleton, human, vertices, bones, weights);
                if (human.Mesh.SurfaceGetMaterial(surface)?.ResourceName.Contains("Rider Eye", StringComparison.OrdinalIgnoreCase) == true)
                    eyePoints.AddRange(worldPoints);
                for (var vertex = 0; vertex < vertices.Length; vertex++)
                {
                    var headWeight = 0f;
                    for (var influence = 0; influence < 4; influence++)
                    {
                        var offset = vertex * 4 + influence;
                        if (bones[offset] == helmetHeadBone) headWeight += weights[offset];
                    }
                    if (headWeight > .5f) headPoints.Add(worldPoints[vertex]);
                }
            }
            if (headPoints.Count < 500 || eyePoints.Count < 20)
                throw new InvalidOperationException("Imported rider face or weighted head geometry is incomplete.");
            var headCenter = headPoints.Aggregate(Vector3.Zero, (sum, point) => sum + point) / headPoints.Count;
            var eyeCenter = eyePoints.Aggregate(Vector3.Zero, (sum, point) => sum + point) / eyePoints.Count;
            var faceForward = eyeCenter - headCenter;
            if (faceForward.Length() < .01f)
                throw new InvalidOperationException("Imported eye/head landmarks do not define a face-forward axis.");
            faceForward = faceForward.Normalized();
            var faceBikeAlignment = faceForward.Dot(helmetBikeForward);
            if (faceBikeAlignment < .5f)
                throw new InvalidOperationException($"Actual eye/head geometry does not face the front wheel (dot={faceBikeAlignment:F3}).");
            var noseCandidates = headPoints.Where(point =>
                Math.Abs((point - eyeCenter).Dot(helmetSide)) <= .03f &&
                Math.Abs((point - eyeCenter).Dot(riderUp)) <= .09f).ToArray();
            if (noseCandidates.Length == 0)
                throw new InvalidOperationException("Actual rider forehead/nose geometry is unavailable for the facing check.");
            var noseTip = noseCandidates.MaxBy(point => point.Dot(helmetBikeForward));
            var noseVector = noseTip - eyeCenter;
            if (noseVector.Length() < .02f || noseVector.Dot(helmetBikeForward) < .02f ||
                noseVector.Normalized().Dot(helmetBikeForward) < .35f)
                throw new InvalidOperationException("Actual nose geometry does not project toward the bicycle front wheel.");

            GD.Print($"[RiderFinishingSceneReviewTest] rigid original helmet attached to head; " +
                     $"physical brow/rear landmarks parented to head assembly, vertices={helmetVertices}");
            GD.Print($"[RiderFinishingSceneReviewTest] legacy skinned helmet hidden; rigid assembly={rigidHelmetAssembly.GetPath()}");
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
            var outfitColors = human.Mesh!.SurfaceGetArrays(0)[(int)Mesh.ArrayType.Color].AsColorArray();
            var humanVertices = human.Mesh.SurfaceGetArrays(0)[(int)Mesh.ArrayType.Vertex].AsVector3Array();
            var jerseyMaskCount = outfitColors.Count(color => color.R > .25f);
            var shortsMaskCount = outfitColors.Count(color => color.G > .25f);
            var shoeMaskCount = outfitColors.Count(color => color.B > .25f);
            var skinMaskCount = outfitColors.Count(color => color.R + color.G + color.B < .05f);
            if (outfitColors.Length == 0 || jerseyMaskCount < 100 || shortsMaskCount < 100 ||
                shoeMaskCount < 20 || skinMaskCount < 100)
                throw new InvalidOperationException($"Imported RiderOutfitWeights mask is missing or flattened: vertices={outfitColors.Length}, jersey={jerseyMaskCount}, shorts={shortsMaskCount}, shoes={shoeMaskCount}, skin={skinMaskCount}.");
            var humanBounds = human.GetAabb();
            var upperChestLow = humanBounds.Position.Y + humanBounds.Size.Y * .72f;
            var upperChestHigh = humanBounds.Position.Y + humanBounds.Size.Y * .77f;
            var upperChestVertices = Enumerable.Range(0, Math.Min(humanVertices.Length, outfitColors.Length))
                .Where(index => Math.Abs(humanVertices[index].X) < .16f &&
                                humanVertices[index].Y >= upperChestLow && humanVertices[index].Y <= upperChestHigh)
                .ToArray();
            var coveredUpperChestVertices = upperChestVertices.Count(index => outfitColors[index].R > .35f);
            if (upperChestVertices.Length < 80 || coveredUpperChestVertices < upperChestVertices.Length * .70f)
                throw new InvalidOperationException($"The fitted jersey mask does not cover the central upper chest up to the neck base: covered={coveredUpperChestVertices}/{upperChestVertices.Length}, verticalBand={upperChestLow:F3}-{upperChestHigh:F3}m.");
            GD.Print($"[RiderFinishingSceneReviewTest] outfit COLOR_0 vertices={outfitColors.Length} jersey={jerseyMaskCount} shorts={shortsMaskCount} shoes={shoeMaskCount} skin={skinMaskCount}");
            GD.Print($"[RiderFinishingSceneReviewTest] upper-chest jersey coverage={coveredUpperChestVertices}/{upperChestVertices.Length} central vertices through neck-base band");
            var jerseyShell = Find<MeshInstance3D>(rider, "RiderJerseyShell")
                ?? throw new InvalidOperationException("The imported skinned jersey surface is missing.");
            var shortsShell = Find<MeshInstance3D>(rider, "RiderBibShortsShell")
                ?? throw new InvalidOperationException("The imported skinned bib-short surface is missing.");
            if (Find<Node>(rider, "RiderRoadKitDetails") is not null || Find<Node>(rider, "RiderJerseyCollar") is not null)
                throw new InvalidOperationException("Obsolete projected garment overlays must be removed before reviewing the fitted surfaces.");
            if (jerseyShell.Mesh is null || jerseyShell.Mesh.GetSurfaceCount() != 4 || jerseyShell.Skin is null ||
                shortsShell.Mesh is null || shortsShell.Mesh.GetSurfaceCount() != 2 || shortsShell.Skin is null)
                throw new InvalidOperationException("The jersey and bib shorts need continuous skinned fabric surfaces.");
            if (System.Environment.GetEnvironmentVariable("WODRA_VISUAL_REVIEW_BASELINE") != "1")
            {
                rider.SetCustomization("Navy blue", "Charcoal");
                var jerseyMaterial = jerseyShell.GetSurfaceOverrideMaterial(0) as ShaderMaterial;
                var selectedJerseyColor = jerseyMaterial?.GetShaderParameter("jersey_color").AsColor() ?? Colors.Transparent;
                var expectedJerseyColor = JerseyReviewColor("Navy blue");
                if (jerseyMaterial is null || Math.Abs(selectedJerseyColor.R - expectedJerseyColor.R) > .02f ||
                    Math.Abs(selectedJerseyColor.G - expectedJerseyColor.G) > .02f ||
                    Math.Abs(selectedJerseyColor.B - expectedJerseyColor.B) > .02f)
                    throw new InvalidOperationException("The skinned jersey fabric must inherit the selected profile colour.");
                GD.Print($"[RiderFinishingSceneReviewTest] skinned garments jerseySurfaces={jerseyShell.Mesh.GetSurfaceCount()} shortsSurfaces={shortsShell.Mesh.GetSurfaceCount()} selectedColour={selectedJerseyColor}");
            }
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
            camera.Fov = 50f;
            camera.GlobalPosition = bikeCenter + bikeSide * 2.65f + bikeForward * .08f + Vector3.Up * .54f;
            camera.LookAt(bikeCenter + Vector3.Up * .42f, Vector3.Up);
            await Settle(3);
            await Save(output, "fit-rider-side-full.png");
            rider.SetCustomization("Navy blue", "Red");
            await Settle(2);
            await Save(output, "fit-rider-navy-red-full.png");
            rider.SetCustomization("White", "Original purple/blue");
            await Settle(2);
            await Save(output, "fit-rider-white-original-full.png");
            rider.SetCustomization("Teal", "Charcoal");
            camera.GlobalPosition = bikeCenter + bikeForward * 3.6f + Vector3.Up * .72f;
            camera.LookAt(bikeCenter + Vector3.Up * .62f, Vector3.Up);
            await Settle(3);
            await Save(output, "fit-rider-front-full.png");
            camera.GlobalPosition = bikeCenter + bikeSide * 1.55f + bikeForward * 2.05f + Vector3.Up * .92f;
            camera.LookAt(bikeCenter + Vector3.Up * .42f, Vector3.Up);
            await Settle(3);
            await Save(output, "fit-rider-front-three-quarter-full.png");
            camera.GlobalPosition = bikeCenter - bikeForward * 3.6f + Vector3.Up * .72f;
            camera.LookAt(bikeCenter + Vector3.Up * .62f, Vector3.Up);
            await Settle(3);
            await Save(output, "fit-rider-rear-full.png");
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
            await CaptureGarmentEvidence(output, rider, skeleton, camera, bikeForward, bikeSide);
            // Keep the complete head and neck visible without the perspective
            // exaggeration of the earlier 0.58 m macro framing.
            camera.Fov = 46f;
            camera.MakeCurrent();
            var headTarget = head + Vector3.Up * .045f;
            rider.SetHelmetVisible(false);
            AimAtHead(camera, bikeForward, bikeSide, headTarget, "front");
            await Settle(2);
            foreach (var angle in new[] { "front", "side", "three-quarter", "rear" })
            {
                AimAtHead(camera, bikeForward, bikeSide, headTarget, angle);
                await Settle(3);
                await Save(output, $"head-{angle}-helmet-off.png");
            }
            rider.SetHelmetVisible(true);
            AimAtHead(camera, bikeForward, bikeSide, headTarget, "side");
            var reviewHelmetPose = Find<Node3D>(rider, "CyclingHelmetRigidPose")!;
            foreach (var pitch in new[] { 3f, 6f, 9f, 12f })
            {
                rider.SetReviewHelmetForwardPitchDegrees(pitch);
                await Settle(3);
                await Save(output, $"head-side-helmet-pitch-{pitch:0}deg.png");
            }
            rider.SetReviewHelmetForwardPitchDegrees(12f);
            await Settle(2);
            foreach (var angle in new[] { "front", "side", "three-quarter", "rear" })
            {
                AimAtHead(camera, bikeForward, bikeSide, headTarget, angle);
                await Settle(3);
                await Save(output, $"head-{angle}-helmet-on.png");
            }
            camera.Fov = 32f;
            foreach (var (degrees, color) in new[]
                     { (0, "Navy blue"), (90, "Navy blue"), (180, "Teal"), (270, "Teal") })
            {
                rider.SetCustomization(color, "Charcoal");
                rider.SetReviewCrankPhase(degrees);
                var neckTarget = headTarget - Vector3.Up * .28f;
                camera.GlobalPosition = neckTarget + bikeSide * .62f + Vector3.Up * .06f;
                camera.LookAt(neckTarget, Vector3.Up);
                await Settle(3);
                await Save(output, $"jersey-collar-{color.ToLowerInvariant()}-phase-{degrees:D3}.png");
            }
            rider.SetCustomization("Navy blue", "Charcoal");
            rider.SetReviewCrankPhase(0);
            camera.Fov = 46f;
            rider.SetCustomization("Teal", "Charcoal");
            // Validate actual physical features selected from the owner-supplied
            // OBJ after inspecting its raw front/side/rear renders. The short
            // brow lip must project in front of the face and the long rear tail
            // behind it. This fails if the whole assembly is yawed 180 degrees;
            // it does not infer helmet front from the pose basis or a reference bone.
            AimAtHead(camera, bikeForward, bikeSide, headTarget, "side");
            await Settle(2);
            var eyeScreen = camera.UnprojectPosition(eyeCenter);
            var browScreen = camera.UnprojectPosition(physicalFrontBrow.GlobalPosition);
            var tailScreen = camera.UnprojectPosition(physicalRearTail.GlobalPosition);
            var browAlongFace = (physicalFrontBrow.GlobalPosition - eyeCenter).Dot(faceForward);
            var tailAlongFace = (physicalRearTail.GlobalPosition - eyeCenter).Dot(faceForward);
            if (!PhysicalHelmetFeaturesFaceForward(physicalFrontBrow.GlobalPosition,
                    physicalRearTail.GlobalPosition, eyeCenter, faceForward) ||
                browScreen.X <= eyeScreen.X + 1f || tailScreen.X >= eyeScreen.X - 1f)
                throw new InvalidOperationException($"Original helmet physical features are not oriented around the actual rider face: " +
                    $"brow/eyes/rear-tail X={browScreen.X:F1}/{eyeScreen.X:F1}/{tailScreen.X:F1}px, " +
                    $"face projections={browAlongFace:F3}/{tailAlongFace:F3}m.");
            GD.Print($"[RiderFinishingSceneReviewTest] physical-feature orientation in production side camera: " +
                $"brow/eyes/rear-tail X={browScreen.X:F1}/{eyeScreen.X:F1}/{tailScreen.X:F1}px, " +
                     $"face projections={browAlongFace:F3}/{tailAlongFace:F3}m; " +
                     $"mesh proximity={browMeshDistance:F3}/{rearMeshDistance:F3}m; accepted forward orientation retained");
            if (System.Environment.GetEnvironmentVariable("WODRA_VISUAL_REVIEW_BASELINE") == "1" &&
                System.Environment.GetEnvironmentVariable("WODRA_VISUAL_REVIEW_BASELINE_DEEP") != "1")
            {
                var baselineLeftHood = Find<Node3D>(rider, "Contact_LeftHood")
                    ?? throw new InvalidOperationException("Baseline left hood contact missing.");
                var baselineRightHood = Find<Node3D>(rider, "Contact_RightHood")
                    ?? throw new InvalidOperationException("Baseline right hood contact missing.");
                var baselineLeftPalm = skeleton.FindBone("palm_contact.L");
                var baselineRightPalm = skeleton.FindBone("palm_contact.R");
                if (baselineLeftPalm < 0 || baselineRightPalm < 0)
                    throw new InvalidOperationException("Baseline sculpted palm-contact markers are missing.");
                foreach (var (hood, side) in new[] { (baselineLeftHood, -1f), (baselineRightHood, 1f) })
                {
                    var palmBone = side < 0 ? baselineLeftPalm : baselineRightPalm;
                    var palm = skeleton.GlobalTransform * skeleton.GetBoneGlobalPose(palmBone).Origin;
                    var hoodCenter = hood.GlobalPosition;
                    var target = hoodCenter.Lerp(palm, .58f);
                    var view = (bikeSide * side * .58f + bikeForward * .72f + Vector3.Up * .12f).Normalized();
                    camera.Fov = 24f;
                    camera.GlobalPosition = target + view * .52f;
                    camera.LookAt(target, Vector3.Up);
                    await Settle(3);
                    await Save(output, side < 0 ? "hand-left-hood-close.png" : "hand-right-hood-close.png");
                    var elevated = (bikeSide * side * .42f + bikeForward * .48f + Vector3.Up * .78f).Normalized();
                    camera.GlobalPosition = target + elevated * .58f;
                    camera.LookAt(target, Vector3.Up);
                    await Settle(3);
                    await Save(output, side < 0 ? "hand-left-hood-elevated.png" : "hand-right-hood-elevated.png");
                }
                GD.Print($"[RiderFinishingSceneReview] PASS: production RideScreen baseline captures; evidence: {output}");
                GetTree().Quit(0);
                return;
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
            var leftPedalContactBone = skeleton.FindBone("pedal_contact.L");
            var rightPedalContactBone = skeleton.FindBone("pedal_contact.R");
            var shoeBones = new[] { skeleton.FindBone("shoe.L"), skeleton.FindBone("shoe.R") };
            var shoeHeelBones = new[] { skeleton.FindBone("shoe_heel.L"), skeleton.FindBone("shoe_heel.R") };
            var shoeToeBones = new[] { skeleton.FindBone("shoe_toe.L"), skeleton.FindBone("shoe_toe.R") };
            var shoeOpeningBones = new[] { skeleton.FindBone("shoe_ankle_opening.L"), skeleton.FindBone("shoe_ankle_opening.R") };
            var shoeMeshes = new[]
            {
                Find<MeshInstance3D>(rider, "CyclingShoeL"),
                Find<MeshInstance3D>(rider, "CyclingShoeR")
            };
            var pelvisBones = new[] { skeleton.FindBone("pelvis.L"), skeleton.FindBone("pelvis.R") }.Where(i => i >= 0).ToArray();
            if (leftPalmContact < 0 || rightPalmContact < 0 ||
                leftFootBone < 0 || rightFootBone < 0 || leftPedalContactBone < 0 ||
                rightPedalContactBone < 0 || pelvisBones.Length == 0 || shoeBones.Any(index => index < 0) ||
                shoeHeelBones.Any(index => index < 0) || shoeToeBones.Any(index => index < 0) ||
                shoeOpeningBones.Any(index => index < 0) || shoeMeshes.Any(mesh => mesh?.Mesh is null))
                throw new InvalidOperationException("Sculpted palms, pelvis, ankle/shoe separation, or shoe geometry markers are missing.");
            var shoeProfiles = shoeMeshes.Select(mesh => mesh!.Mesh!.GetAabb().Size).ToArray();
            GD.Print($"[RiderFinishingSceneReviewTest] shoe mesh source bounds L={shoeProfiles[0]} R={shoeProfiles[1]}");
            var leftPedalSurface = Find<Node3D>(rider, "Contact_LeftPedalSurface")
                ?? throw new InvalidOperationException("Measured left pedal platform marker missing.");
            var rightPedalSurface = Find<Node3D>(rider, "Contact_RightPedalSurface")
                ?? throw new InvalidOperationException("Measured right pedal platform marker missing.");
            var hipBones = new[] { skeleton.FindBone("upperleg01.L"), skeleton.FindBone("upperleg01.R") };
            var kneeBones = new[] { skeleton.FindBone("lowerleg01.L"), skeleton.FindBone("lowerleg01.R") };
            var ankleBones = new[] { skeleton.FindBone("foot.L"), skeleton.FindBone("foot.R") };
            if (hipBones.Any(index => index < 0) || kneeBones.Any(index => index < 0) || ankleBones.Any(index => index < 0))
                throw new InvalidOperationException("Imported anatomical hip/knee/ankle bones are missing.");
            var maxKneeStep = new float[2];
            var maxLegLengthDelta = new float[2];
            var minKneeFlex = new float[] { float.PositiveInfinity, float.PositiveInfinity };
            var maxKneeFlex = new float[2];
            var minForwardKnee = new float[] { float.PositiveInfinity, float.PositiveInfinity };
            var kneeAtStart = new Vector3[2];
            var kneePrevious = new Vector3[2];
            var upperLengthAtStart = new float[2];
            var lowerLengthAtStart = new float[2];
            var maxLeftPalm = 0f;
            var maxRightPalm = 0f;
            var maxPelvis = 0f;
            var maxLeftShoeContact = 0f;
            var maxRightShoeContact = 0f;
            var maxToeForwardError = new float[2];
            var maxSoleNormalError = new float[2];
            var maxFootToShoeAxisError = new float[2];
            var maxFootToPedalNormalError = new float[2];
            var maxShoeAxisStep = new float[2];
            var maxShoeContactStep = new float[2];
            var maxPedalSurfaceStep = new float[2];
            var maxHeelPlaneError = new float[2];
            var maxToePlaneError = new float[2];
            var maxMeshAxisError = new float[2];
            var minForefootToeAdvantage = new float[] { float.PositiveInfinity, float.PositiveInfinity };
            var minHeelPlane = new float[] { float.PositiveInfinity, float.PositiveInfinity };
            var minToePlane = new float[] { float.PositiveInfinity, float.PositiveInfinity };
            var maxAnkleOpeningDistance = new float[2];
            var minAnklePositionFromHeel = new float[] { float.PositiveInfinity, float.PositiveInfinity };
            var maxAnklePositionFromHeel = new float[2];
            var shoeToeAtStart = new Vector3[2];
            var shoeToePrevious = new Vector3[2];
            var shoeContactAtStart = new Vector3[2];
            var shoeContactPrevious = new Vector3[2];
            var pedalSurfacePrevious = new Vector3[2];
            var maxLeftShoePhase = 0;
            var maxRightShoePhase = 0;
            var maxLeftShoeError = Vector3.Zero;
            var maxRightShoeError = Vector3.Zero;
            var minFootTravel = float.PositiveInfinity;
            var maxFootTravel = 0f;
            var leftFootAtStart = Vector3.Zero;
            var rightFootAtStart = Vector3.Zero;
            var pedalSurfaces = new[] { leftPedalSurface, rightPedalSurface };
            // Sample the entire imported cadence cycle, rather than relying on
            // a single still pose that could hide hand or pedal drift.
            const int fitSamples = 72;
            for (var sample = 0; sample < fitSamples; sample++)
            {
                rider.SetReviewCrankPhase(sample * 360 / fitSamples);
                await Settle(1);
                var leftPalm = skeleton.GlobalTransform * skeleton.GetBoneGlobalPose(leftPalmContact).Origin;
                var rightPalm = skeleton.GlobalTransform * skeleton.GetBoneGlobalPose(rightPalmContact).Origin;
                var leftFoot = skeleton.GlobalTransform * skeleton.GetBoneGlobalPose(leftFootBone).Origin;
                var rightFoot = skeleton.GlobalTransform * skeleton.GetBoneGlobalPose(rightFootBone).Origin;
                var leftShoeBall = skeleton.GlobalTransform * skeleton.GetBoneGlobalPose(leftPedalContactBone).Origin;
                var rightShoeBall = skeleton.GlobalTransform * skeleton.GetBoneGlobalPose(rightPedalContactBone).Origin;
                var shoeContactPositions = new[] { leftShoeBall, rightShoeBall };
                var pelvis = pelvisBones.Select(i => skeleton.GlobalTransform * skeleton.GetBoneGlobalPose(i).Origin)
                    .Aggregate(Vector3.Zero, (sum, p) => sum + p) / pelvisBones.Length;
                for (var side = 0; side < 2; side++)
                {
                    var hip = skeleton.GlobalTransform * skeleton.GetBoneGlobalPose(hipBones[side]).Origin;
                    var knee = skeleton.GlobalTransform * skeleton.GetBoneGlobalPose(kneeBones[side]).Origin;
                    var ankle = skeleton.GlobalTransform * skeleton.GetBoneGlobalPose(ankleBones[side]).Origin;
                    var upperLength = hip.DistanceTo(knee);
                    var lowerLength = knee.DistanceTo(ankle);
                    var flexion = Mathf.Pi - (hip - knee).AngleTo(ankle - knee);
                    var legAxis = ankle - hip;
                    var closest = hip + legAxis * Mathf.Clamp((knee - hip).Dot(legAxis) / legAxis.LengthSquared(), 0f, 1f);
                    var forwardOffset = (knee - closest).Dot(bikeForward);
                    minKneeFlex[side] = Math.Min(minKneeFlex[side], flexion);
                    maxKneeFlex[side] = Math.Max(maxKneeFlex[side], flexion);
                    minForwardKnee[side] = Math.Min(minForwardKnee[side], forwardOffset);
                    if (sample == 0)
                    {
                        kneeAtStart[side] = knee;
                        upperLengthAtStart[side] = upperLength;
                        lowerLengthAtStart[side] = lowerLength;
                    }
                    else
                    {
                        maxKneeStep[side] = Math.Max(maxKneeStep[side], knee.DistanceTo(kneePrevious[side]));
                        maxLegLengthDelta[side] = Math.Max(maxLegLengthDelta[side],
                            Math.Max(Math.Abs(upperLength - upperLengthAtStart[side]), Math.Abs(lowerLength - lowerLengthAtStart[side])));
                    }
                    kneePrevious[side] = knee;
                }
                maxLeftPalm = Math.Max(maxLeftPalm, leftPalm.DistanceTo(leftHood.GlobalPosition));
                maxRightPalm = Math.Max(maxRightPalm, rightPalm.DistanceTo(rightHood.GlobalPosition));
                maxPelvis = Math.Max(maxPelvis, pelvis.DistanceTo(saddle.GlobalPosition));
                var leftShoeError = leftShoeBall - leftPedalSurface.GlobalPosition;
                var rightShoeError = rightShoeBall - rightPedalSurface.GlobalPosition;
                for (var side = 0; side < 2; side++)
                {
                    var shoePose = skeleton.GlobalTransform * skeleton.GetBoneGlobalPose(shoeBones[side]);
                    var toeAxis = shoePose.Basis.Y.Normalized();
                    var soleUp = shoePose.Basis.Z.Normalized();
                    var footPose = skeleton.GlobalTransform * skeleton.GetBoneGlobalPose(ankleBones[side]);
                    var footAxis = footPose.Basis.Y.Normalized();
                    var footUp = footPose.Basis.Z.Normalized();
                    var surfaceNormal = pedalSurfaces[side].GlobalBasis.Y.Normalized();
                    var footToShoeAxisError = Mathf.Acos(Mathf.Clamp(footAxis.Dot(toeAxis), -1f, 1f));
                    var footToPedalNormalError = Mathf.Acos(Mathf.Clamp(footUp.Dot(surfaceNormal), -1f, 1f));
                    maxFootToShoeAxisError[side] = Math.Max(maxFootToShoeAxisError[side], footToShoeAxisError);
                    maxFootToPedalNormalError[side] = Math.Max(maxFootToPedalNormalError[side], footToPedalNormalError);
                    if (sample % 18 == 0)
                    {
                        GD.Print($"[RiderFinishingSceneReviewTest] phase={sample * 5} side={side} " +
                            $"ankleToShoeAxis={Mathf.RadToDeg(footToShoeAxisError):F1}deg " +
                            $"ankleToPedalNormal={Mathf.RadToDeg(footToPedalNormalError):F1}deg " +
                            $"forefoot={shoeContactPositions[side]} heel={skeleton.GlobalTransform * skeleton.GetBoneGlobalPose(shoeHeelBones[side]).Origin} " +
                            $"toe={skeleton.GlobalTransform * skeleton.GetBoneGlobalPose(shoeToeBones[side]).Origin}");
                    }
                    if (sample == 0)
                    {
                        shoeToeAtStart[side] = toeAxis;
                        shoeContactAtStart[side] = shoeContactPositions[side];
                    }
                    else
                    {
                        maxShoeAxisStep[side] = Math.Max(maxShoeAxisStep[side],
                            Mathf.Acos(Mathf.Clamp(shoeToePrevious[side].Dot(toeAxis), -1f, 1f)));
                        maxShoeContactStep[side] = Math.Max(maxShoeContactStep[side],
                            shoeContactPrevious[side].DistanceTo(shoeContactPositions[side]));
                        maxPedalSurfaceStep[side] = Math.Max(maxPedalSurfaceStep[side],
                            pedalSurfacePrevious[side].DistanceTo(pedalSurfaces[side].GlobalPosition));
                    }
                    shoeToePrevious[side] = toeAxis;
                    shoeContactPrevious[side] = shoeContactPositions[side];
                    pedalSurfacePrevious[side] = pedalSurfaces[side].GlobalPosition;
                    var toeAngle = Mathf.Acos(Mathf.Clamp(toeAxis.Dot(bikeForward), -1f, 1f));
                    var soleAngle = Mathf.Acos(Mathf.Clamp(soleUp.Dot(surfaceNormal), -1f, 1f));
                    var heel = skeleton.GlobalTransform * skeleton.GetBoneGlobalPose(shoeHeelBones[side]).Origin;
                    var toe = skeleton.GlobalTransform * skeleton.GetBoneGlobalPose(shoeToeBones[side]).Origin;
                    // Identify the physical toe from independently measured
                    // toe-box/heel-cup widths in the skinned mesh. Neither the
                    // pedal, contact markers, authored indices, bone labels,
                    // nor presumed local-axis signs select either end.
                    var openingMarker = skeleton.GlobalTransform * skeleton.GetBoneGlobalPose(shoeOpeningBones[side]).Origin;
                    var physicalFeatures = FindPhysicalShoeFeatures(skeleton, shoeMeshes[side]!, bikeForward, surfaceNormal, openingMarker);
                    var physicalMeshAxis = (physicalFeatures.Toe - physicalFeatures.Heel).Normalized();
                    maxMeshAxisError[side] = Math.Max(maxMeshAxisError[side],
                        Mathf.Acos(Mathf.Clamp(physicalMeshAxis.Dot(bikeForward), -1f, 1f)));
                    minForefootToeAdvantage[side] = Math.Min(minForefootToeAdvantage[side],
                        physicalFeatures.WidthDifference);
                    var ankleOpening = physicalFeatures.AnkleOpening;
                    var ankleJoint = skeleton.GlobalTransform * skeleton.GetBoneGlobalPose(ankleBones[side]).Origin;
                    var ankleOpeningDistance = ankleJoint.DistanceTo(ankleOpening);
                    var markerToOpeningDistance = openingMarker.DistanceTo(ankleOpening);
                    var heelToToeLength = (physicalFeatures.Toe - physicalFeatures.Heel).Dot(bikeForward);
                    var ankleFromHeel = (ankleJoint - physicalFeatures.Heel).Dot(bikeForward) / heelToToeLength;
                    maxAnkleOpeningDistance[side] = Math.Max(maxAnkleOpeningDistance[side], ankleOpeningDistance);
                    minAnklePositionFromHeel[side] = Math.Min(minAnklePositionFromHeel[side], ankleFromHeel);
                    maxAnklePositionFromHeel[side] = Math.Max(maxAnklePositionFromHeel[side], ankleFromHeel);
                    if (markerToOpeningDistance > .020f)
                        throw new InvalidOperationException($"{shoeMeshes[side]!.Name} opening marker disagrees with the physical heel-side upper rim by {markerToOpeningDistance:F3}m.");
                    var heelHeight = (heel - pedalSurfaces[side].GlobalPosition).Dot(surfaceNormal);
                    var toeHeight = (toe - pedalSurfaces[side].GlobalPosition).Dot(surfaceNormal);
                    maxToeForwardError[side] = Math.Max(maxToeForwardError[side], toeAngle);
                    maxSoleNormalError[side] = Math.Max(maxSoleNormalError[side], soleAngle);
                    maxHeelPlaneError[side] = Math.Max(maxHeelPlaneError[side], Math.Abs(heelHeight));
                    maxToePlaneError[side] = Math.Max(maxToePlaneError[side], Math.Abs(toeHeight));
                    minHeelPlane[side] = Math.Min(minHeelPlane[side], heelHeight);
                    minToePlane[side] = Math.Min(minToePlane[side], toeHeight);
                }
                var leftCrossPedalDistance = leftShoeBall.DistanceTo(rightPedalSurface.GlobalPosition);
                var rightCrossPedalDistance = rightShoeBall.DistanceTo(leftPedalSurface.GlobalPosition);
                if (leftCrossPedalDistance < leftShoeError.Length() + .08f ||
                    rightCrossPedalDistance < rightShoeError.Length() + .08f)
                    throw new InvalidOperationException("A shoe is associated with the opposite pedal platform.");
                var phase = sample * 360 / fitSamples;
                if (leftShoeError.Length() > maxLeftShoeContact)
                {
                    maxLeftShoeContact = leftShoeError.Length();
                    maxLeftShoePhase = phase;
                    maxLeftShoeError = leftShoeError;
                }
                if (rightShoeError.Length() > maxRightShoeContact)
                {
                    maxRightShoeContact = rightShoeError.Length();
                    maxRightShoePhase = phase;
                    maxRightShoeError = rightShoeError;
                }
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
            for (var side = 0; side < 2; side++)
            {
                maxKneeStep[side] = Math.Max(maxKneeStep[side], kneeAtStart[side].DistanceTo(kneePrevious[side]));
                maxShoeAxisStep[side] = Math.Max(maxShoeAxisStep[side],
                    Mathf.Acos(Mathf.Clamp(shoeToeAtStart[side].Dot(shoeToePrevious[side]), -1f, 1f)));
                maxShoeContactStep[side] = Math.Max(maxShoeContactStep[side],
                    shoeContactAtStart[side].DistanceTo(shoeContactPrevious[side]));
                maxPedalSurfaceStep[side] = Math.Max(maxPedalSurfaceStep[side],
                    pedalSurfacePrevious[side].DistanceTo(pedalSurfaces[side].GlobalPosition));
            }
            GD.Print($"[RiderFinishingSceneReviewTest] full-cycle max palm-to-hood L={maxLeftPalm:F3}m R={maxRightPalm:F3}m; pelvis-to-saddle={maxPelvis:F3}m; ball-of-foot to moving pedal top L={maxLeftShoeContact:F5}m@{maxLeftShoePhase}deg {maxLeftShoeError} R={maxRightShoeContact:F5}m@{maxRightShoePhase}deg {maxRightShoeError}; ankle-bone travel={minFootTravel:F3}-{maxFootTravel:F3}m");
            GD.Print($"[RiderFinishingSceneReviewTest] anatomical knees step={maxKneeStep[0]:F4}/{maxKneeStep[1]:F4}m flex={Mathf.RadToDeg(minKneeFlex[0]):F1}-{Mathf.RadToDeg(maxKneeFlex[0]):F1}/{Mathf.RadToDeg(minKneeFlex[1]):F1}-{Mathf.RadToDeg(maxKneeFlex[1]):F1}deg forwardMin={minForwardKnee[0]:F4}/{minForwardKnee[1]:F4}m legLengthDelta={maxLegLengthDelta[0]:F5}/{maxLegLengthDelta[1]:F5}m");
            GD.Print($"[RiderFinishingSceneReviewTest] shoe toe error={Mathf.RadToDeg(maxToeForwardError[0]):F1}/{Mathf.RadToDeg(maxToeForwardError[1]):F1}deg sole error={Mathf.RadToDeg(maxSoleNormalError[0]):F1}/{Mathf.RadToDeg(maxSoleNormalError[1]):F1}deg ankle-to-shoe axis={Mathf.RadToDeg(maxFootToShoeAxisError[0]):F1}/{Mathf.RadToDeg(maxFootToShoeAxisError[1]):F1}deg ankle-to-pedal normal={Mathf.RadToDeg(maxFootToPedalNormalError[0]):F1}/{Mathf.RadToDeg(maxFootToPedalNormalError[1]):F1}deg heel plane={maxHeelPlaneError[0]:F3}/{maxHeelPlaneError[1]:F3}m toe plane={maxToePlaneError[0]:F3}/{maxToePlaneError[1]:F3}m signed min heel={minHeelPlane[0]:F3}/{minHeelPlane[1]:F3} toe={minToePlane[0]:F3}/{minToePlane[1]:F3}m shoe step={Mathf.RadToDeg(maxShoeAxisStep[0]):F2}/{Mathf.RadToDeg(maxShoeAxisStep[1]):F2}deg contact={maxShoeContactStep[0]:F3}/{maxShoeContactStep[1]:F3}m surface={maxPedalSurfaceStep[0]:F3}/{maxPedalSurfaceStep[1]:F3}m");
            GD.Print($"[RiderFinishingSceneReviewTest] physical toe-box/heel-cup width advantage={minForefootToeAdvantage[0]:F3}/{minForefootToeAdvantage[1]:F3}m; physical toe-axis error={Mathf.RadToDeg(maxMeshAxisError[0]):F1}/{Mathf.RadToDeg(maxMeshAxisError[1]):F1}deg; ankle/opening distance={maxAnkleOpeningDistance[0]:F3}/{maxAnkleOpeningDistance[1]:F3}m; ankle fraction heel-to-toe={minAnklePositionFromHeel[0]:F2}-{maxAnklePositionFromHeel[0]:F2}/{minAnklePositionFromHeel[1]:F2}-{maxAnklePositionFromHeel[1]:F2}");
            if (maxLeftPalm > .045f || maxRightPalm > .045f)
                throw new InvalidOperationException($"Hands leave the hood contact zones during PedalCycle (L={maxLeftPalm:F3}m, R={maxRightPalm:F3}m).");
            if (maxPelvis > .18f)
                throw new InvalidOperationException($"Pelvis left the saddle fit envelope ({maxPelvis:F3}m).");
            if (maxFootTravel < .05f)
                throw new InvalidOperationException("PedalCycle did not animate the feet through the full cycle.");
            if (minKneeFlex.Any(value => value < Mathf.DegToRad(2f)) || maxKneeFlex.Any(value => value > Mathf.DegToRad(125f)))
                throw new InvalidOperationException("Imported knee flexion leaves the validated road-cycling range.");
            if (minForwardKnee.Any(value => value < -.012f))
                throw new InvalidOperationException("An imported knee bends backwards behind its hip-to-ankle line.");
            if (maxKneeStep.Any(value => value > .022f))
                throw new InvalidOperationException("Imported knee movement has an abrupt step or cycle seam.");
            if (maxLegLengthDelta.Any(value => value > .003f))
                throw new InvalidOperationException("Imported leg animation stretches the thigh or shin.");
            if (maxLeftShoeContact > .012f || maxRightShoeContact > .012f)
                throw new InvalidOperationException($"A shoe leaves its actual moving pedal top (L={maxLeftShoeContact:F3}m, R={maxRightShoeContact:F3}m).");
            if (maxToeForwardError.Any(value => value > Mathf.DegToRad(18f)))
                throw new InvalidOperationException("A shoe toe points away from bicycle-forward.");
            if (maxMeshAxisError.Any(value => value > Mathf.DegToRad(12f)) ||
                minForefootToeAdvantage.Any(value => value < .02f))
                throw new InvalidOperationException("Physical toe-box geometry does not face bicycle-forward or the ankle-to-shoe articulation is invalid.");
            if (maxAnkleOpeningDistance.Any(value => value > .085f) ||
                minAnklePositionFromHeel.Any(value => value < .04f) ||
                maxAnklePositionFromHeel.Any(value => value > .40f))
                throw new InvalidOperationException("The lower-leg ankle joint is not attached near the physical heel-side shoe opening.");
            if (maxFootToShoeAxisError.Any(value => value > Mathf.DegToRad(10f)))
                throw new InvalidOperationException("Ankle/foot bones remain toe-down relative to the actual shoe geometry.");
            if (maxFootToPedalNormalError.Any(value => value > Mathf.DegToRad(12f)))
                throw new InvalidOperationException("Ankle/foot bones remain rotated away from the pedal platform.");
            if (maxSoleNormalError.Any(value => value > Mathf.DegToRad(14f)))
                throw new InvalidOperationException("A shoe sole tilts too far from the actual pedal platform.");
            if (maxShoeAxisStep.Any(value => value > Mathf.DegToRad(1f)) ||
                maxShoeContactStep.Any(value => value > .035f) ||
                Enumerable.Range(0, 2).Any(side => maxShoeContactStep[side] > maxPedalSurfaceStep[side] +
                    2f * (side == 0 ? maxLeftShoeContact : maxRightShoeContact) + .002f))
                throw new InvalidOperationException("A shoe orientation or forefoot contact jumps during the pedal cycle.");
            if (maxHeelPlaneError.Any(value => value > .030f) || maxToePlaneError.Any(value => value > .030f) ||
                minHeelPlane.Any(value => value < -.030f) || minToePlane.Any(value => value < -.030f))
                throw new InvalidOperationException("The heel or toe leaves the measured pedal-plane support envelope.");
            if (shoeProfiles.Any(size => size.Y is < .045f or > .065f || size.Z is < .24f or > .30f))
                throw new InvalidOperationException($"Imported road shoe profile is outside measured proportions (L={shoeProfiles[0]}, R={shoeProfiles[1]}).");
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
            var crank = Find<Node3D>(rider, "CrankPivot")
                ?? throw new InvalidOperationException("Imported crank pivot missing from pedal review.");
            foreach (var degrees in new[] { 0, 45, 90, 135, 180, 225, 270, 315 })
            {
                rider.SetReviewCrankPhase(degrees);
                var crankCenter = crank.GlobalPosition;
                camera.Fov = 38f;
                camera.GlobalPosition = crankCenter + bikeSide * 1.05f + Vector3.Up * .12f;
                camera.LookAt(crankCenter, Vector3.Up);
                await Settle(2);
                await Save(output, $"pedals-side-phase-{degrees:D3}.png");
            }
            rider.SetReviewCrankPhase(90);
            var finalCrankCenter = crank.GlobalPosition;
            // Keep context around the shoes and crank instead of letting the
            // near shoe fill the frame from an exaggerated close perspective.
            camera.Fov = 48f;
            camera.GlobalPosition = finalCrankCenter + bikeForward * 1.55f + bikeSide * .62f + Vector3.Up * .12f;
            camera.LookAt(finalCrankCenter, Vector3.Up);
            await Settle(2);
            await Save(output, "pedals-front-alignment.png");
            camera.GlobalPosition = finalCrankCenter - bikeForward * 1.55f - bikeSide * .62f + Vector3.Up * .12f;
            camera.LookAt(finalCrankCenter, Vector3.Up);
            await Settle(2);
            await Save(output, "pedals-rear-alignment.png");

            // These two low oblique close-ups center the actual measured pedal
            // top marker under the authored ball-of-foot/cleat vertex.
            foreach (var (surface, side, name) in new[]
            {
                (leftPedalSurface, -1f, "pedals-left-shoe-contact.png"),
                (rightPedalSurface, 1f, "pedals-right-shoe-contact.png")
            })
            {
                var contactTarget = surface.GlobalPosition;
                var view = (bikeSide * side * .84f + bikeForward * .28f + Vector3.Up * -.12f).Normalized();
                camera.Fov = 34f;
                camera.GlobalPosition = contactTarget + view * .92f;
                camera.LookAt(contactTarget, Vector3.Up);
                await Settle(2);
                await Save(output, name);
            }
            rider.SetReviewCrankPhase(0);
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
            "rear" => -forward,
            "side" => side,
            _ => (forward + side).Normalized()
        };
        camera.GlobalPosition = head + direction * .95f + Vector3.Up * .02f;
        camera.LookAt(head, Vector3.Up);
    }

    private static Vector3 BoneWorldPosition(Skeleton3D skeleton, string boneName)
    {
        var index = skeleton.FindBone(boneName);
        if (index < 0) throw new InvalidOperationException($"Imported {boneName} bone is unavailable for garment review.");
        return skeleton.GlobalTransform * skeleton.GetBoneGlobalPose(index).Origin;
    }

    private async Task CaptureGarmentEvidence(string output, CyclistVisual rider, Skeleton3D skeleton,
        Camera3D camera, Vector3 bikeForward, Vector3 bikeSide)
    {
        var savedFov = camera.Fov;
        var views = new[]
        {
            ("front", bikeForward),
            ("side", bikeSide),
            ("rear", -bikeForward),
            ("three-quarter", (bikeForward + bikeSide).Normalized())
        };
        rider.SetCustomization("Teal", "Charcoal");
        rider.SetReviewCrankPhase(0);
        camera.Fov = 40f;
        var neckBase = BoneWorldPosition(skeleton, "neck01");
        var collarTarget = neckBase + Vector3.Down * .025f;
        camera.Fov = 38f;
        foreach (var (angle, direction) in views.Where(view => view.Item1 is "front" or "side" or "rear"))
        {
            camera.GlobalPosition = collarTarget + direction * .56f + Vector3.Up * .09f;
            camera.LookAt(collarTarget, Vector3.Up);
            await Settle(3);
            await Save(output, $"garment-collar-{angle}.png");
        }

        foreach (var (angle, direction) in views)
        {
            var neck = BoneWorldPosition(skeleton, "neck01");
            var target = neck + Vector3.Down * .05f;
            camera.GlobalPosition = target + direction * .78f + Vector3.Up * .025f;
            camera.LookAt(target, Vector3.Up);
            await Settle(3);
            await Save(output, $"garment-jersey-{angle}.png");
        }

        camera.Fov = 50f;
        rider.SetCustomization("Navy blue", "Charcoal");
        foreach (var (angle, direction) in views)
        {
            var pelvis = (BoneWorldPosition(skeleton, "pelvis.L") + BoneWorldPosition(skeleton, "pelvis.R")) * .5f;
            var upperLegs = (BoneWorldPosition(skeleton, "upperleg01.L") + BoneWorldPosition(skeleton, "upperleg01.R")) * .5f;
            var target = pelvis.Lerp(upperLegs, .62f) + Vector3.Up * .015f;
            camera.GlobalPosition = target + direction * .98f + Vector3.Up * .01f;
            camera.LookAt(target, Vector3.Up);
            await Settle(3);
            await Save(output, $"garment-shorts-{angle}.png");
        }

        foreach (var degrees in new[] { 0, 45, 90, 135, 180, 225, 270, 315 })
        {
            rider.SetReviewCrankPhase(degrees);
            var pelvis = (BoneWorldPosition(skeleton, "pelvis.L") + BoneWorldPosition(skeleton, "pelvis.R")) * .5f;
            var upperLegs = (BoneWorldPosition(skeleton, "upperleg01.L") + BoneWorldPosition(skeleton, "upperleg01.R")) * .5f;
            var target = pelvis.Lerp(upperLegs, .62f) + Vector3.Up * .015f;
            camera.GlobalPosition = target + bikeSide * .98f + Vector3.Up * .01f;
            camera.LookAt(target, Vector3.Up);
            await Settle(3);
            await Save(output, $"garment-shorts-side-phase-{degrees:D3}.png");
        }
        rider.SetReviewCrankPhase(0);
        camera.Fov = savedFov;
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

    private async Task CaptureLiveLegPhases(string output, RideWorldView world, CyclistVisual rider,
        ChaseCamera camera, HudController hud, Node3D frontWheel, Node3D rearWheel)
    {
        camera.MakeCurrent();
        var savedFov = camera.Fov;
        foreach (var degrees in new[] { 0, 45, 90, 135, 180, 225, 270, 315 })
        {
            var wanted = Math.Tau * degrees / 360.0;
            var previousDelta = Math.Atan2(Math.Sin(rider.CrankAngleRadians - wanted),
                Math.Cos(rider.CrankAngleRadians - wanted));
            var reached = Math.Abs(previousDelta) <= .08;
            for (var frame = 0; frame < 240 && !reached; frame++)
            {
                var delta = Math.Atan2(Math.Sin(rider.CrankAngleRadians - wanted),
                    Math.Cos(rider.CrankAngleRadians - wanted));
                // At high render cost a cadence update can jump over the narrow
                // in-phase window. Capture the nearest live sample that crosses
                // the target instead of failing or waiting indefinitely.
                var crossedTarget = previousDelta * delta < 0 && Math.Abs(previousDelta - delta) < Math.PI;
                if (Math.Abs(delta) <= .08 || crossedTarget)
                {
                    reached = true;
                    break;
                }
                previousDelta = delta;
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            }
            if (!reached)
                throw new InvalidOperationException($"Live RideScreen crank did not reach {degrees} degrees at 85 rpm.");
            var livePhase = rider.CrankAngleRadians;
            world.SetProcess(false);
            rider.SetProcess(false);
            // The live 85 rpm update has reached/crossed this phase. Freeze the
            // imported runtime rider at the exact corresponding clip sample so
            // GPU render latency cannot make a phase-labeled evidence frame stale.
            rider.SetReviewCrankPhase(degrees);
            var capturedPhase = rider.CrankAngleRadians;
            if (Math.Abs(Math.Atan2(Math.Sin(capturedPhase - wanted), Math.Cos(capturedPhase - wanted))) > .001)
                throw new InvalidOperationException($"Live RideScreen phase capture target={degrees} actual={capturedPhase * 180 / Math.PI:F2}.");
            var phaseForward = (frontWheel.GlobalPosition - rearWheel.GlobalPosition).Normalized();
            var phaseSide = phaseForward.Cross(Vector3.Up).Normalized();
            var bikeCenter = (frontWheel.GlobalPosition + rearWheel.GlobalPosition) * .5f + Vector3.Up * .30f;
            var target = bikeCenter + Vector3.Up * .10f;
            camera.Fov = 42f;
            camera.GlobalPosition = bikeCenter + phaseSide * 2.35f + phaseForward * .08f + Vector3.Up * .54f;
            camera.LookAt(target, Vector3.Up);
            await Settle(2);
            await Save(output, $"legs-live-side-phase-{degrees:D3}.png");
            GD.Print($"[RiderFinishingSceneReviewTest] live RideScreen phase target={degrees} " +
                $"crossing={livePhase * 180 / Math.PI:F1} capture={capturedPhase * 180 / Math.PI:F1} cadence=85rpm");
            rider.SetProcess(true);
            world.SetProcess(true);
            world.SelectCameraInspectionView(RiderCameraView.Side);
        }
        camera.Fov = savedFov;

        // The cadence-driven captures above prove the live 85 rpm update path.
        // For clean visual comparisons, pause that same imported RideScreen
        // rider and seek its baked clip and pedal visuals to exact labeled phases.
        // This avoids labeling a slow-render frame with a phase it has already
        // advanced past while the screenshot is being rendered.
        var hudWasVisible = hud.Visible;
        world.SetProcess(false);
        rider.SetProcess(false);
        hud.Hide();
        camera.Fov = 42f;
        var fullBikeForward = (frontWheel.GlobalPosition - rearWheel.GlobalPosition).Normalized();
        var fullBikeSide = fullBikeForward.Cross(Vector3.Up).Normalized();
        var fullBikeCenter = (frontWheel.GlobalPosition + rearWheel.GlobalPosition) * .5f + Vector3.Up * .30f;
        camera.GlobalPosition = fullBikeCenter + fullBikeSide * 2.35f + fullBikeForward * .08f + Vector3.Up * .54f;
        camera.LookAt(fullBikeCenter + Vector3.Up * .20f, Vector3.Up);
        foreach (var degrees in new[] { 0, 90, 180, 270 })
        {
            rider.SetReviewCrankPhase(degrees);
            var exactPhase = rider.CrankAngleRadians * 180 / Math.PI;
            if (Math.Abs(exactPhase - degrees) > .01)
                throw new InvalidOperationException($"Clear RideScreen phase target={degrees} actual={exactPhase:F2}.");
            await Settle(2);
            await Save(output, $"rider-side-full-phase-{degrees:D3}.png");
            GD.Print($"[RiderFinishingSceneReviewTest] clear RideScreen phase target={degrees} actual={exactPhase:F1} imported model");
        }
        var reviewSkeleton = Find<Skeleton3D>(rider) ?? throw new InvalidOperationException("Shoe review skeleton missing.");
        var ankleBones = new[] { reviewSkeleton.FindBone("foot.L"), reviewSkeleton.FindBone("foot.R") };
        var pedalSurfacesForReview = new[]
        {
            Find<Node3D>(rider, "Contact_LeftPedalSurface") ?? throw new InvalidOperationException("Left pedal surface missing."),
            Find<Node3D>(rider, "Contact_RightPedalSurface") ?? throw new InvalidOperationException("Right pedal surface missing.")
        };
        var pedalSpindlesForReview = new[]
        {
            Find<Node3D>(rider, "Contact_LeftPedal") ?? throw new InvalidOperationException("Left pedal spindle missing."),
            Find<Node3D>(rider, "Contact_RightPedal") ?? throw new InvalidOperationException("Right pedal spindle missing.")
        };
        if (ankleBones.Any(index => index < 0)) throw new InvalidOperationException("Ankle joint bones are missing in shoe review.");
        var featureBones = new (int Toe, int Heel)[]
        {
            (reviewSkeleton.FindBone("shoe_toe.L"), reviewSkeleton.FindBone("shoe_heel.L")),
            (reviewSkeleton.FindBone("shoe_toe.R"), reviewSkeleton.FindBone("shoe_heel.R"))
        };
        if (featureBones.Any(pair => pair.Toe < 0 || pair.Heel < 0))
            throw new InvalidOperationException("Imported physical shoe-feature markers are missing.");
        var shoeMeshes = new[]
        {
            Find<MeshInstance3D>(rider, "CyclingShoeL") ?? throw new InvalidOperationException("Left shoe mesh missing."),
            Find<MeshInstance3D>(rider, "CyclingShoeR") ?? throw new InvalidOperationException("Right shoe mesh missing.")
        };
        var labels = new Label3D[8];
        for (var shoe = 0; shoe < 2; shoe++)
        {
            var first = shoe * 4;
            labels[first] = MakeShoeLabel("T", new Color(.25f, 1f, .38f));
            labels[first + 1] = MakeShoeLabel("H", new Color(1f, .63f, .18f));
            labels[first + 2] = MakeShoeLabel("A", new Color(.35f, .78f, 1f));
            labels[first + 3] = MakeShoeLabel("P", new Color(1f, .92f, .25f));
            for (var marker = 0; marker < 4; marker++)
            {
                labels[first + marker].Visible = false;
                rider.AddChild(labels[first + marker]);
            }
        }
        foreach (var degrees in new[] { 0, 45, 90, 135, 180, 225, 270, 315 })
        {
            rider.SetReviewCrankPhase(degrees);
            var skeletonToWorld = reviewSkeleton.GlobalTransform;
            var sideForward = (frontWheel.GlobalPosition - rearWheel.GlobalPosition).Normalized();
            var sideRight = sideForward.Cross(Vector3.Up).Normalized();
            for (var shoe = 0; shoe < 2; shoe++)
            {
                foreach (var label in labels) label.Visible = false;
                var openingMarker = reviewSkeleton.GlobalTransform * reviewSkeleton.GetBoneGlobalPose(
                    reviewSkeleton.FindBone(shoe == 0 ? "shoe_ankle_opening.L" : "shoe_ankle_opening.R")).Origin;
                var ends = FindPhysicalShoeFeatures(reviewSkeleton, shoeMeshes[shoe], sideForward,
                    pedalSurfacesForReview[shoe].GlobalBasis.Y.Normalized(), openingMarker);
                var toe = ends.Toe;
                var heel = ends.Heel;
                var center = (toe + heel) * .5f;
                var first = shoe * 4;
                var ankleJoint = reviewSkeleton.GlobalTransform * reviewSkeleton.GetBoneGlobalPose(ankleBones[shoe]).Origin;
                var pedalSpindle = pedalSpindlesForReview[shoe].GlobalPosition;
                camera.Fov = 23f;
                var sign = shoe == 0 ? -1f : 1f;
                camera.GlobalPosition = center + sideRight * (1.05f * sign) + sideForward * .04f + Vector3.Up * .12f;
                camera.LookAt(center + Vector3.Up * .015f, Vector3.Up);
                camera.MakeCurrent();
                var frontScreenX = camera.UnprojectPosition(frontWheel.GlobalPosition).X;
                var rearScreenX = camera.UnprojectPosition(rearWheel.GlobalPosition).X;
                var toeScreenX = camera.UnprojectPosition(toe).X;
                var heelScreenX = camera.UnprojectPosition(heel).X;
                var frontProjectsRight = frontScreenX > rearScreenX;
                var toeProjectsRight = toeScreenX > heelScreenX;
                if (frontProjectsRight != toeProjectsRight)
                    throw new InvalidOperationException($"Rendered physical toe points opposite the front wheel on the {(shoe == 0 ? "left" : "right")} shoe at {degrees}°: wheel X={frontScreenX:F1}/{rearScreenX:F1}, toe/heel X={toeScreenX:F1}/{heelScreenX:F1}.");
                await Settle(3);
                await Save(output, $"shoe-{(shoe == 0 ? "left" : "right")}-side-phase-{degrees:D3}-unannotated.png");
                labels[first].GlobalPosition = toe + Vector3.Up * .012f;
                labels[first + 1].GlobalPosition = heel + Vector3.Up * .012f;
                labels[first + 2].GlobalPosition = ankleJoint + Vector3.Up * .012f;
                labels[first + 3].GlobalPosition = pedalSpindle + Vector3.Up * .012f;
                for (var marker = 0; marker < 4; marker++) labels[first + marker].Visible = true;
                await Settle(1);
                await Save(output, $"shoe-{(shoe == 0 ? "left" : "right")}-side-phase-{degrees:D3}-annotated.png");
            }
        }
        foreach (var label in labels) label.QueueFree();
        camera.Fov = savedFov;
        if (hudWasVisible) hud.Show();
        rider.SetProcess(true);
        world.SetProcess(true);
        world.SelectCameraInspectionView(RiderCameraView.Side);
    }

    private static Label3D MakeShoeLabel(string text, Color color) => new()
    {
        Text = text, Modulate = color, FontSize = 12, PixelSize = .00055f,
        OutlineSize = 1, NoDepthTest = true,
        Billboard = BaseMaterial3D.BillboardModeEnum.Enabled
    };

    private static string sideName(float side) => side < 0 ? "Left" : "Right";

    private static int CountBoneTrackKeys(Animation animation, string boneName)
    {
        var total = 0;
        for (var track = 0; track < animation.GetTrackCount(); track++)
            if (animation.TrackGetPath(track).ToString().Contains(boneName, StringComparison.OrdinalIgnoreCase))
                total += animation.TrackGetKeyCount(track);
        return total;
    }

    private static Color JerseyReviewColor(string color) => color switch
    {
        "Navy blue" => new Color(.075f, .16f, .34f),
        _ => new Color(.05f, .42f, .43f)
    };

    private async Task Settle(int frames)
    {
        for (var i = 0; i < frames; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }
}
