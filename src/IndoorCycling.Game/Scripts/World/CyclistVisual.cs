using System;
using Godot;
using IndoorCycling.Core.Models;

/// <summary>Procedural articulated rider and road-bike proxy. It consumes ride state and never simulates it.</summary>
public partial class CyclistVisual : Node3D
{
    private const string ImportedModelPath = "res://Assets/Models/WodraCyclist.glb";
    private sealed record Dimensions(double RiderHeight, float WheelRadius, float Wheelbase, float CrankRadius,
        float CrankY, float CrankZ, float SaddleY, float SaddleZ, float UpperLeg, float LowerLeg);

    // One seated road-cyclist reference pose drives all visible rider anchors. Coordinates use
    // +Y up and -Z forward, matching the bicycle and the existing cadence/IK solver.
    private sealed record RiderPose(Vector3 Pelvis, Vector3 LeftHip, Vector3 RightHip,
        Vector3 NeckBase, Vector3 NeckTop, Vector3 HeadCenter, Vector3 HelmetCenter,
        Vector3 LeftShoulder, Vector3 RightShoulder, Vector3 LeftElbow, Vector3 RightElbow,
        Vector3 LeftWrist, Vector3 RightWrist);

    private static readonly Dimensions Size = new(1.78, .35f, 1.02f, .17f, .39f, .10f,
        .80f, .25f, .43f, .43f);
    private static readonly RiderPose SeatedPose = new(
        new Vector3(0, .99f, .19f), new Vector3(-.13f, .98f, .19f), new Vector3(.13f, .98f, .19f),
        new Vector3(0, 1.285f, -.055f), new Vector3(0, 1.355f, -.105f),
        new Vector3(0, 1.475f, -.17f), new Vector3(0, 1.59f, -.19f),
        new Vector3(-.19f, 1.285f, -.055f), new Vector3(.19f, 1.285f, -.055f),
        new Vector3(-.295f, 1.105f, -.345f), new Vector3(.295f, 1.105f, -.345f),
        new Vector3(-.30f, .955f, -.565f), new Vector3(.30f, .955f, -.565f));
    private static readonly CylinderMesh RodMesh = new() { TopRadius = .5f, BottomRadius = .5f, Height = 1, RadialSegments = 8 };
    private static readonly BoxMesh UnitBoxMesh = new() { Size = Vector3.One };
    private static readonly SphereMesh UnitSphereMesh = new() { Radius = .5f, Height = 1, RadialSegments = 12, Rings = 8 };
    private static readonly TorusMesh TyreMesh = new() { InnerRadius = .315f, OuterRadius = .35f, Rings = 8, RingSegments = 20 };
    private static readonly TorusMesh RimMesh = new() { InnerRadius = .289f, OuterRadius = .301f, Rings = 6, RingSegments = 20 };
    private static readonly StandardMaterial3D[] FramePalette = [
        Material(new Color(.12f,.63f,.72f)), Material(new Color(.20f,.38f,.63f)),
        Material(new Color(.72f,.37f,.12f)), Material(new Color(.27f,.48f,.27f))];
    private static readonly StandardMaterial3D[] JerseyPalette = [
        Material(new Color(.70f,.25f,.19f)), Material(new Color(.25f,.40f,.53f)),
        Material(new Color(.72f,.52f,.20f)), Material(new Color(.31f,.47f,.35f)),
        Material(new Color(.48f,.34f,.49f)), Material(new Color(.76f,.76f,.69f))];
    private static readonly StandardMaterial3D TyreMaterial = Material(new Color(.025f,.03f,.04f));
    private static readonly StandardMaterial3D RimMaterial = Material(new Color(.67f,.72f,.76f));
    private static readonly StandardMaterial3D MetalMaterial = Material(new Color(.28f,.31f,.34f));
    private static readonly StandardMaterial3D ShortsMaterial = Material(new Color(.07f,.09f,.12f));
    private static readonly StandardMaterial3D SkinMaterial = Material(new Color(.78f,.57f,.43f));
    private static readonly StandardMaterial3D ShoeMaterial = Material(new Color(.88f,.87f,.79f));
    private static readonly StandardMaterial3D HelmetMaterial = Material(new Color(.79f,.61f,.27f));
    private static readonly StandardMaterial3D GloveMaterial = Material(new Color(.10f,.12f,.14f));
    private static readonly Shader RiderOutfitShader = new()
    {
        Code = """
            shader_type spatial;
            render_mode cull_back;
            uniform vec4 jersey_color : source_color = vec4(0.58, 0.13, 0.09, 1.0);
            uniform vec4 shorts_color : source_color = vec4(0.035, 0.047, 0.058, 1.0);
            uniform vec4 skin_color : source_color = vec4(0.58, 0.36, 0.25, 1.0);
            uniform vec4 shoe_color : source_color = vec4(0.68, 0.66, 0.58, 1.0);
            void fragment() {
                float jersey_weight = COLOR.r;
                float shorts_weight = COLOR.g;
                float shoe_weight = COLOR.b;
                float skin_weight = max(0.0, 1.0 - jersey_weight - shorts_weight - shoe_weight);
                vec3 outfit_color = jersey_weight * jersey_color.rgb + shorts_weight * shorts_color.rgb +
                                    skin_weight * skin_color.rgb + shoe_weight * shoe_color.rgb;
                ALBEDO = outfit_color;
                // The low route sun can leave exposed skin nearly black.
                // Add a restrained skin-coloured ambient contribution that
                // preserves the skin hue without brightening clothing or world.
                EMISSION = skin_weight * skin_color.rgb * 0.55;
                ROUGHNESS = 0.82;
            }
            """
    };
    private StandardMaterial3D _frameMaterial = FramePalette[0];
    private StandardMaterial3D _tyreMaterial = TyreMaterial;
    private StandardMaterial3D _rimMaterial = RimMaterial;
    private StandardMaterial3D _metalMaterial = MetalMaterial;
    private StandardMaterial3D _jerseyMaterial = JerseyPalette[0];
    private StandardMaterial3D _shortsMaterial = ShortsMaterial;
    private StandardMaterial3D _skinMaterial = SkinMaterial;
    private StandardMaterial3D _shoeMaterial = ShoeMaterial;
    private Node3D _frontWheel = default!;
    private Node3D _rearWheel = default!;
    private MeshInstance3D _leftCrank = default!;
    private MeshInstance3D _rightCrank = default!;
    private MeshInstance3D _leftPedal = default!;
    private MeshInstance3D _rightPedal = default!;
    private MeshInstance3D _leftUpperLeg = default!;
    private MeshInstance3D _leftLowerLeg = default!;
    private MeshInstance3D _rightUpperLeg = default!;
    private MeshInstance3D _rightLowerLeg = default!;
    private MeshInstance3D _leftFoot = default!;
    private MeshInstance3D _rightFoot = default!;
    private MeshInstance3D _leftAnkle = default!;
    private MeshInstance3D _rightAnkle = default!;
    private MeshInstance3D _leftSole = default!;
    private MeshInstance3D _rightSole = default!;
    private MeshInstance3D _leftKnee = default!;
    private MeshInstance3D _rightKnee = default!;
    private Node3D _crankPivot = default!;
    private Node3D _bicycleModel = default!;
    private Node3D _riderModel = default!;
    private Node3D _upperBody = default!;
    private Vector3 _upperBodyRestPosition;
    private Node3D _headRig = default!;
    private Node3D _leftArmRig = default!;
    private Node3D _rightArmRig = default!;
    private Node3D _leftLegRig = default!;
    private Node3D _rightLegRig = default!;
    private AnimationPlayer? _riderAnimationPlayer;
    private Animation? _pedalCycleAnimation;
    private StringName _pedalCycleName;
    private readonly System.Collections.Generic.List<Node3D> _helmetParts = new();
    private Vector3 _previousForward;
    private bool _hasPreviousForward;
    private double _wheelAngle;
    private double _crankAngle;
    private double _animatedCadenceRpm;
    private double _leanRadians;
    private bool _built;
    private int _approximateTriangleCount = 2200;

    public bool UsesImportedModel { get; private set; }

    public double WheelAngleRadians => _wheelAngle;

    public void SetRiderVisible(bool visible)
    {
        if (_riderModel is not null) _riderModel.Visible = visible;
    }
    public double CrankAngleRadians => _crankAngle;
    public double LeanDegrees => _leanRadians * 180 / Math.PI;
    public double CurrentGradientPercent { get; private set; }
    public double RiderHeightMeters => Size.RiderHeight;
    /// <summary>World-space bounds of the visible rider and bicycle, for camera framing and diagnostics.</summary>
    public Aabb GetWorldVisualBounds()
    {
        var minimum = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
        var maximum = new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);
        AccumulateVisibleBounds(this, ref minimum, ref maximum);
        if (!float.IsFinite(minimum.X) || !float.IsFinite(maximum.X))
            return new Aabb(GlobalPosition + new Vector3(-.55f, 0, -.75f), new Vector3(1.1f, 1.8f, 1.5f));
        return new Aabb(minimum, maximum - minimum);
    }

    private static void AccumulateVisibleBounds(Node node, ref Vector3 minimum, ref Vector3 maximum)
    {
        if (node is GeometryInstance3D geometry && geometry.IsVisibleInTree())
        {
            var box = geometry.GetAabb();
            for (var i = 0; i < 8; i++)
            {
                var local = box.Position + new Vector3((i & 1) == 0 ? 0 : box.Size.X,
                    (i & 2) == 0 ? 0 : box.Size.Y, (i & 4) == 0 ? 0 : box.Size.Z);
                var world = geometry.GlobalTransform * local;
                minimum = minimum.Min(world);
                maximum = maximum.Max(world);
            }
        }
        foreach (Node child in node.GetChildren()) AccumulateVisibleBounds(child, ref minimum, ref maximum);
    }
    public int ApproximateNodeCount => CountNodes(this);
    public int ApproximateTriangleCount => _approximateTriangleCount;

    /// <summary>Show or hide the optional imported road helmet and its straps.</summary>
    public void SetHelmetVisible(bool visible)
    {
        foreach (var part in _helmetParts)
            part.Visible = visible;
    }

    public void Build(int visualVariant = 0)
    {
        if (_built) return;
        _frameMaterial = FramePalette[Math.Abs(visualVariant) % FramePalette.Length];
        _jerseyMaterial = JerseyPalette[Math.Abs(visualVariant) % JerseyPalette.Length];
        if (TryBuildImportedModel())
        {
            _built = true;
            UsesImportedModel = true;
            UpdatePedalingGeometry();
#if DEBUG
            ValidateImportedModelBounds();
#endif
            return;
        }
        _built = true;
        _bicycleModel = new Node3D { Name = "BicycleModel" };
        _riderModel = new Node3D { Name = "RiderModel" };
        AddChild(_bicycleModel);
        AddChild(_riderModel);
        var rearZ = Size.Wheelbase / 2;
        var frontZ = -Size.Wheelbase / 2;
        var wheelY = Size.WheelRadius;
        _rearWheel = AddWheel("RearWheel", new Vector3(0,wheelY,rearZ), _bicycleModel);
        _frontWheel = AddWheel("FrontWheel", new Vector3(0,wheelY,frontZ), _bicycleModel);

        var rearHub = new Vector3(0,wheelY,rearZ);
        var frontHub = new Vector3(0,wheelY,frontZ);
        var bottomBracket = new Vector3(0,Size.CrankY,Size.CrankZ);
        var seatTop = new Vector3(0,Size.SaddleY,Size.SaddleZ);
        var headLow = new Vector3(0,.72f,-.34f);
        var headHigh = new Vector3(0,.84f,-.39f);
        AddRod("SeatTube",bottomBracket,seatTop,.035f,_frameMaterial,_bicycleModel);
        AddRod("DownTube",bottomBracket,headLow,.035f,_frameMaterial,_bicycleModel);
        AddRod("TopTube",seatTop,headHigh,.03f,_frameMaterial,_bicycleModel);
        foreach (var side in new[] { -.052f, .052f })
        {
            AddRod("ChainStay",new Vector3(side,wheelY,rearZ),new Vector3(side,Size.CrankY,Size.CrankZ),.018f,_frameMaterial,_bicycleModel);
            AddRod("SeatStay",new Vector3(side,wheelY,rearZ),new Vector3(side,Size.SaddleY,Size.SaddleZ),.018f,_frameMaterial,_bicycleModel);
            AddRod("ForkLeg",new Vector3(side,wheelY,frontZ),new Vector3(side,.72f,-.34f),.019f,_frameMaterial,_bicycleModel);
        }
        AddRod("HeadTube",headLow,headHigh,.045f,_frameMaterial,_bicycleModel);
        AddRod("SeatPost",seatTop,new Vector3(0,.88f,.27f),.025f,_metalMaterial,_bicycleModel);
        AddRod("Stem",headHigh,new Vector3(0,.95f,-.565f),.025f,_metalMaterial,_bicycleModel);
        AddRod("HandlebarTop",new Vector3(-.31f,.95f,-.565f),new Vector3(.31f,.95f,-.565f),.018f,_metalMaterial,_bicycleModel);
        foreach (var side in new[] { -1f, 1f })
        {
            var hoodBase = new Vector3(side*.29f,.95f,-.565f);
            var hoodFront = new Vector3(side*.30f,.94f,-.645f);
            var drop = new Vector3(side*.27f,.82f,-.635f);
            AddRod("BarCurve",hoodBase,hoodFront,.018f,_metalMaterial,_bicycleModel);
            AddRod("BarDrop",hoodFront,drop,.018f,_metalMaterial,_bicycleModel);
            AddRod("BarReturn",drop,new Vector3(side*.20f,.81f,-.565f),.018f,_metalMaterial,_bicycleModel);
            AddSphere("BrakeHood",new Vector3(side*.29f,.985f,-.565f),new Vector3(.055f,.07f,.09f),GloveMaterial,_bicycleModel);
        }
        AddBox("Saddle",new Vector3(.24f,.04f,.13f),new Vector3(0,.91f,.27f),_shortsMaterial,_bicycleModel);
        AddRod("CrankAxle",new Vector3(-.1f,Size.CrankY,Size.CrankZ),new Vector3(.1f,Size.CrankY,Size.CrankZ),.045f,_metalMaterial,_bicycleModel);
        AddCrankAndChain(rearHub,bottomBracket,_bicycleModel);

        _upperBody = new Node3D { Name = "TorsoRig" };
        _riderModel.AddChild(_upperBody);
        _upperBodyRestPosition = _upperBody.Position;
        var torso = new MeshInstance3D { Name = "JerseyTorso", Mesh = BuildJerseyMesh(), MaterialOverride = _jerseyMaterial };
        _upperBody.AddChild(torso);
        AddSphere("PelvisAndShorts",SeatedPose.Pelvis,new Vector3(.235f,.145f,.205f),_shortsMaterial,_upperBody);
        AddRod("Neck",SeatedPose.NeckBase,SeatedPose.NeckTop,.065f,_skinMaterial,_upperBody);
        _headRig = new Node3D { Name = "HeadRig" };
        _upperBody.AddChild(_headRig);
        AddSphere("Head",SeatedPose.HeadCenter,new Vector3(.205f,.235f,.205f),_skinMaterial,_headRig);
        AddSphere("HelmetShell",SeatedPose.HelmetCenter,new Vector3(.245f,.14f,.275f),HelmetMaterial,_headRig);
        AddBox("HelmetBrim",new Vector3(.22f,.022f,.125f),new Vector3(0,1.545f,-.325f),HelmetMaterial,_headRig);

        _leftArmRig = new Node3D { Name = "LeftArmRig" };
        _rightArmRig = new Node3D { Name = "RightArmRig" };
        _upperBody.AddChild(_leftArmRig);
        _upperBody.AddChild(_rightArmRig);
        AddLoftLimb("LeftJerseySleeve",SeatedPose.LeftShoulder,SeatedPose.LeftElbow,.074f,.056f,_jerseyMaterial,_leftArmRig);
        AddLoftLimb("RightJerseySleeve",SeatedPose.RightShoulder,SeatedPose.RightElbow,.074f,.056f,_jerseyMaterial,_rightArmRig);
        AddLoftLimb("LeftForearm",SeatedPose.LeftElbow,SeatedPose.LeftWrist,.054f,.043f,_skinMaterial,_leftArmRig);
        AddLoftLimb("RightForearm",SeatedPose.RightElbow,SeatedPose.RightWrist,.054f,.043f,_skinMaterial,_rightArmRig);
        AddSphere("LeftElbowJoint",SeatedPose.LeftElbow,new Vector3(.076f,.076f,.076f),_skinMaterial,_leftArmRig);
        AddSphere("RightElbowJoint",SeatedPose.RightElbow,new Vector3(.076f,.076f,.076f),_skinMaterial,_rightArmRig);
        AddSphere("LeftGlovedHand",SeatedPose.LeftWrist,new Vector3(.09f,.068f,.09f),GloveMaterial,_leftArmRig);
        AddSphere("RightGlovedHand",SeatedPose.RightWrist,new Vector3(.09f,.068f,.09f),GloveMaterial,_rightArmRig);

        _crankPivot = new Node3D { Name = "CrankPivot", Position = bottomBracket };
        _bicycleModel.AddChild(_crankPivot);
        _leftCrank=AddRod("LeftCrank",new Vector3(-.13f,0,0),new Vector3(-.13f,0,Size.CrankRadius),.024f,_metalMaterial,_crankPivot);
        _rightCrank=AddRod("RightCrank",new Vector3(.13f,0,0),new Vector3(.13f,0,-Size.CrankRadius),.024f,_metalMaterial,_crankPivot);
        _leftPedal=AddBox("LeftPedal",new Vector3(.18f,.045f,.075f),new Vector3(-.13f,0,Size.CrankRadius),_metalMaterial,_crankPivot);
        _rightPedal=AddBox("RightPedal",new Vector3(.18f,.045f,.075f),new Vector3(.13f,0,-Size.CrankRadius),_metalMaterial,_crankPivot);
        _leftLegRig = new Node3D { Name = "LeftLegRig" };
        _rightLegRig = new Node3D { Name = "RightLegRig" };
        _riderModel.AddChild(_leftLegRig);
        _riderModel.AddChild(_rightLegRig);
        _leftUpperLeg=AddRod("LeftThigh",Vector3.Zero,Vector3.Up,.085f,_shortsMaterial,_leftLegRig);
        _leftLowerLeg=AddRod("LeftShin",Vector3.Zero,Vector3.Up,.055f,_skinMaterial,_leftLegRig);
        _rightUpperLeg=AddRod("RightThigh",Vector3.Zero,Vector3.Up,.085f,_shortsMaterial,_rightLegRig);
        _rightLowerLeg=AddRod("RightShin",Vector3.Zero,Vector3.Up,.055f,_skinMaterial,_rightLegRig);
        _leftFoot=AddSphere("LeftCyclingShoe",Vector3.Zero,new Vector3(.15f,.065f,.22f),_shoeMaterial,_leftLegRig);
        _rightFoot=AddSphere("RightCyclingShoe",Vector3.Zero,new Vector3(.15f,.065f,.22f),_shoeMaterial,_rightLegRig);
        _leftSole=AddBox("LeftShoeSole",new Vector3(.13f,.018f,.19f),Vector3.Zero,_shortsMaterial,_leftLegRig);
        _rightSole=AddBox("RightShoeSole",new Vector3(.13f,.018f,.19f),Vector3.Zero,_shortsMaterial,_rightLegRig);
        _leftAnkle=AddSphere("LeftAnkle",Vector3.Zero,new Vector3(.065f,.065f,.065f),_skinMaterial,_leftLegRig);
        _rightAnkle=AddSphere("RightAnkle",Vector3.Zero,new Vector3(.065f,.065f,.065f),_skinMaterial,_rightLegRig);
        _leftKnee=AddSphere("LeftKnee",Vector3.Zero,new Vector3(.095f,.095f,.095f),_shortsMaterial,_leftLegRig);
        _rightKnee=AddSphere("RightKnee",Vector3.Zero,new Vector3(.095f,.095f,.095f),_shortsMaterial,_rightLegRig);
        UpdatePedalingGeometry();
    }

    public void ResetVisualInterpolation()
    {
        _hasPreviousForward = false;
        _leanRadians = 0;
    }

    private bool TryBuildImportedModel()
    {
        if (!ResourceLoader.Exists(ImportedModelPath)) return false;
        var packedScene = ResourceLoader.Load<PackedScene>(ImportedModelPath);
        var instance = packedScene?.Instantiate();
        if (instance is null)
        {
            GD.PushWarning($"Imported cyclist at {ImportedModelPath} could not be loaded; using procedural fallback.");
            return false;
        }
        AddChild(instance);
        var requiredTargets = new (string Name, Type Type)[]
        {
            ("BicycleModel", typeof(Node3D)), ("RiderModel", typeof(Node3D)),
            ("FrontWheelSpin", typeof(Node3D)), ("RearWheelSpin", typeof(Node3D)),
            ("CrankPivot", typeof(Node3D)),
            ("LeftCrank", typeof(MeshInstance3D)), ("RightCrank", typeof(MeshInstance3D)),
            ("LeftPedal", typeof(MeshInstance3D)), ("RightPedal", typeof(MeshInstance3D))
        };
        var invalidTargets = new System.Collections.Generic.List<string>();
        foreach (var required in requiredTargets)
        {
            var found = instance.FindChild(required.Name, true, false);
            if (found is null || !required.Type.IsInstanceOfType(found))
                invalidTargets.Add(required.Name);
        }
        if (invalidTargets.Count > 0)
        {
            GD.PushWarning($"Imported cyclist at {ImportedModelPath} is missing or has invalid animation targets: {string.Join(", ", invalidTargets)}; using procedural fallback.");
            RemoveChild(instance);
            instance.QueueFree();
            return false;
        }

        _riderAnimationPlayer = FindFirstNode<AnimationPlayer>(instance);
        var skeleton = FindFirstNode<Skeleton3D>(instance);
        if (_riderAnimationPlayer is null || skeleton is null)
        {
            GD.PushWarning($"Imported cyclist at {ImportedModelPath} must contain a Skeleton3D and AnimationPlayer; using procedural fallback.");
            RemoveChild(instance);
            instance.QueueFree();
            return false;
        }
        foreach (var animationName in _riderAnimationPlayer.GetAnimationList())
        {
            if (!animationName.ToString().Contains("PedalCycle", StringComparison.OrdinalIgnoreCase)) continue;
            _pedalCycleName = animationName;
            _pedalCycleAnimation = _riderAnimationPlayer.GetAnimation(animationName);
            break;
        }
        if (_pedalCycleAnimation is null || _pedalCycleAnimation.Length <= 0)
        {
            GD.PushWarning($"Imported cyclist at {ImportedModelPath} has no baked PedalCycle animation; using procedural fallback.");
            RemoveChild(instance);
            instance.QueueFree();
            return false;
        }
        _riderAnimationPlayer.Play(_pedalCycleName);
        _riderAnimationPlayer.Pause(); // The ride cadence drives sampling; the player must not free-run.
        GD.Print($"[Cyclist GLB] Skeleton bones={skeleton.GetBoneCount()} animation={_pedalCycleName} length={_pedalCycleAnimation.Length:F3}s");
        _bicycleModel = FindModelNode<Node3D>(instance, "BicycleModel")!;
        _riderModel = FindModelNode<Node3D>(instance, "RiderModel")!;
        _frontWheel = FindModelNode<Node3D>(instance, "FrontWheelSpin")!;
        _rearWheel = FindModelNode<Node3D>(instance, "RearWheelSpin")!;
        _crankPivot = FindModelNode<Node3D>(instance, "CrankPivot")!;
        _upperBody = null!; // The complete imported rider is animated by its Skeleton3D.
        _leftCrank = FindModelNode<MeshInstance3D>(instance, "LeftCrank")!;
        _rightCrank = FindModelNode<MeshInstance3D>(instance, "RightCrank")!;
        _leftPedal = FindModelNode<MeshInstance3D>(instance, "LeftPedal")!;
        _rightPedal = FindModelNode<MeshInstance3D>(instance, "RightPedal")!;

        if (_bicycleModel is null || _riderModel is null || _frontWheel is null || _rearWheel is null ||
            _crankPivot is null || _leftCrank is null || _rightCrank is null ||
            _leftPedal is null || _rightPedal is null)
        {
            GD.PushWarning($"Imported cyclist at {ImportedModelPath} does not match the Wodra animation-target contract; using procedural fallback.");
            RemoveChild(instance);
            instance.QueueFree();
            return false;
        }

        SetImportedMaterial(instance, "BikeFrame", _frameMaterial);
        ApplyImportedRiderMaterials(instance);
        CollectHelmetParts(instance);
        _approximateTriangleCount = CountTriangles(instance);
        return true;
    }

    private void ApplyImportedRiderMaterials(Node root)
    {
        var human = FindModelNode<MeshInstance3D>(root, "Human")
            ?? throw new InvalidOperationException("Imported rider is missing the weighted Human mesh.");
        var outfit = new ShaderMaterial { Shader = RiderOutfitShader };
        outfit.SetShaderParameter("jersey_color", _jerseyMaterial.AlbedoColor);
        outfit.SetShaderParameter("shorts_color", new Color(.035f,.047f,.058f));
        outfit.SetShaderParameter("skin_color", new Color(.58f,.36f,.25f));
        outfit.SetShaderParameter("shoe_color", new Color(.68f,.66f,.58f));
        var surfaces = human.Mesh?.GetSurfaceCount() ?? 0;
        var outfitSurfaces = 0;
        for (var surface = 0; surface < surfaces; surface++)
        {
            var sourceMaterial = human.Mesh!.SurfaceGetMaterial(surface);
            var eyeSurface =
                sourceMaterial?.ResourceName.Contains("Rider Eye", StringComparison.OrdinalIgnoreCase) == true;
            if (eyeSurface) continue;
            human.SetSurfaceOverrideMaterial(surface, outfit);
            outfitSurfaces++;
        }
        if (outfitSurfaces == 0)
            throw new InvalidOperationException("Imported rider has no outfit surface for its colour shader.");
        GD.Print($"[Cyclist GLB] Human outfit shader applied to {outfitSurfaces}/{surfaces} surfaces; eye material remains separate.");
    }

    /// <summary>Set an exact baked pedal phase for the Godot visual review harness.</summary>
    public void SetReviewCrankPhase(int degrees)
    {
        if (!UsesImportedModel || _riderAnimationPlayer is null || _pedalCycleAnimation is null) return;
        _crankAngle = Math.Tau * ((degrees % 360 + 360) % 360) / 360.0;
        _crankPivot.Rotation = new Vector3((float)-_crankAngle, 0, 0);
        var cycleLength = _pedalCycleAnimation.Length / 3.0;
        _riderAnimationPlayer.Seek((_crankAngle / Math.Tau) * cycleLength, true);
    }

    private void CollectHelmetParts(Node node)
    {
        if (node is Node3D spatial &&
            (spatial.Name.ToString().StartsWith("CyclingHelmet", StringComparison.Ordinal) ||
             spatial.Name.ToString().StartsWith("HelmetRim", StringComparison.Ordinal) ||
             spatial.Name.ToString().StartsWith("HelmetVent", StringComparison.Ordinal) ||
             spatial.Name.ToString().StartsWith("HelmetStrap", StringComparison.Ordinal) ||
             spatial.Name.ToString().StartsWith("HelmetYAnchor", StringComparison.Ordinal)))
            _helmetParts.Add(spatial);
        foreach (var child in node.GetChildren())
            CollectHelmetParts(child);
    }

    private static T? FindFirstNode<T>(Node root) where T : Node
    {
        if (root is T match) return match;
        foreach (var child in root.GetChildren())
            if (FindFirstNode<T>(child) is { } result)
                return result;
        return null;
    }

    private static int CountTriangles(Node root)
    {
        var total = root is MeshInstance3D { Mesh: { } mesh }
            ? mesh.GetFaces().Length / 3 : 0;
        foreach (var child in root.GetChildren())
            total += CountTriangles(child);
        return total;
    }

    private static T? FindModelNode<T>(Node root, string name) where T : Node =>
        root.FindChild(name, true, false) as T;

    private static void SetImportedMaterial(Node root, string name, Material material)
    {
        if (FindModelNode<MeshInstance3D>(root, name) is { } mesh)
            mesh.MaterialOverride = material;
    }

    private void AddCrankAndChain(Vector3 rearHub, Vector3 bottomBracket, Node3D parent)
    {
        var ring = new MeshInstance3D
        {
            Name = "Chainring",
            Mesh = new CylinderMesh { TopRadius = .105f, BottomRadius = .105f, Height = .018f, RadialSegments = 16 },
            Position = new Vector3(.11f, bottomBracket.Y, bottomBracket.Z),
            Rotation = new Vector3(0, 0, Mathf.Pi / 2), MaterialOverride = MetalMaterial
        };
        parent.AddChild(ring);
        var cassette = new MeshInstance3D
        {
            Name = "RearCassette",
            Mesh = new CylinderMesh { TopRadius = .055f, BottomRadius = .055f, Height = .025f, RadialSegments = 12 },
            Position = new Vector3(.09f, rearHub.Y, rearHub.Z),
            Rotation = new Vector3(0, 0, Mathf.Pi / 2), MaterialOverride = MetalMaterial
        };
        parent.AddChild(cassette);
        var chainX = .12f;
        AddRod("ChainUpper", new Vector3(chainX, bottomBracket.Y + .07f, bottomBracket.Z + .07f),
            new Vector3(chainX, rearHub.Y + .025f, rearHub.Z), .006f, MetalMaterial, parent);
        AddRod("ChainLower", new Vector3(chainX, bottomBracket.Y - .07f, bottomBracket.Z + .07f),
            new Vector3(chainX, rearHub.Y - .025f, rearHub.Z), .006f, MetalMaterial, parent);
    }

    public void UpdateFromRiderState(RiderState rider, VisualRoutePose routePose,
        double gradientPercent, double deltaSeconds)
    {
        var input = RiderVisualInputMath.Create(rider, routePose, gradientPercent);
        var position = input.Position;
        UpdateFromRideState(new Vector3(position.X, position.Y, position.Z),
            new Vector3(input.Forward.X, input.Forward.Y, input.Forward.Z), input.SpeedKph,
            input.CadenceRpm, input.GradientPercent, input.Paused, deltaSeconds);
    }

    public void UpdateFromRideState(Vector3 routePosition, Vector3 routeForward, double virtualSpeedKph,
        int cadenceRpm, double gradientPercent, bool paused, double deltaSeconds)
    {
        if (!_built) Build();
        CurrentGradientPercent = gradientPercent;
        var delta = Math.Max(0,deltaSeconds);
        var speed = paused ? 0 : Math.Max(0,virtualSpeedKph);
        var cadence = paused ? 0 : Math.Max(0,cadenceRpm);
        _animatedCadenceRpm = CyclistAnimationMath.SmoothCadence(_animatedCadenceRpm, cadence, delta);
        _wheelAngle=CyclistAnimationMath.AdvanceWheelAngle(_wheelAngle,speed,delta,Size.WheelRadius,paused);
        _crankAngle=CyclistAnimationMath.AdvanceCrankAngle(_crankAngle,_animatedCadenceRpm,delta,paused);
        _frontWheel.Rotation=new Vector3((float)_wheelAngle,0,0);
        _rearWheel.Rotation=new Vector3((float)_wheelAngle,0,0);
        var targetLean=_hasPreviousForward
            ? CyclistAnimationMath.CornerLeanDegrees(ToNumerics(_previousForward),ToNumerics(routeForward),delta,speed)
            : 0;
        _previousForward=routeForward; _hasPreviousForward=true;
        var smoothing=1-Math.Exp(-5*delta);
        _leanRadians+=((targetLean*Math.PI/180)-_leanRadians)*smoothing;
        GlobalPosition=routePosition;
        LookAt(routePosition+routeForward,Vector3.Up);
        RotateObjectLocal(Vector3.Back,(float)_leanRadians);
        if (!UsesImportedModel)
            _upperBody.Position = _upperBodyRestPosition + new Vector3(0, paused ? 0 : (float)(.003 * Math.Sin(_crankAngle * 2) * Math.Clamp(_animatedCadenceRpm / 90, 0, 1)), 0);
        UpdatePedalingGeometry();
    }

    private void UpdatePedalingGeometry()
    {
        if (UsesImportedModel)
        {
            // The baked skeleton owns rider motion. Crank and wheel visuals remain driven
            // independently from cadence and road speed, respectively. At zero cadence the
            // crank phase and skeleton sample hold their last pose; restart resumes there.
            _crankPivot.Rotation = new Vector3((float)-_crankAngle, 0, 0);
            if (_riderAnimationPlayer is not null && _pedalCycleAnimation is not null)
            {
                var cycleLength = _pedalCycleAnimation.Length / 3.0;
                var phase = CyclistAnimationMath.WrapAngle(_crankAngle) / Math.Tau;
                _riderAnimationPlayer.Seek(phase * cycleLength, true);
            }
            return;
        }
        var bb=new Vector3(0,Size.CrankY,Size.CrankZ);
        var left=PedalWorldPosition(_crankAngle);
        var right=PedalWorldPosition(_crankAngle+Math.PI);
        left.X=-.13f; right.X=.13f;
        _crankPivot.Rotation = new Vector3((float)-_crankAngle, 0, 0);
        // The GLB stores these meshes as normalized one-metre templates. The procedural
        // fallback creates them at final size, so apply the same dimensions here for both
        // paths. Without this, the imported crank rods and shoe/sole/knee meshes render as
        // large blocks around the rider.
        SetRod(_leftCrank, new Vector3(-.13f, 0, 0), new Vector3(-.13f, 0, Size.CrankRadius), .018f);
        SetRod(_rightCrank, new Vector3(.13f, 0, 0), new Vector3(.13f, 0, -Size.CrankRadius), .018f);
        _leftPedal.Position = new Vector3(-.13f, 0, Size.CrankRadius);
        _rightPedal.Position = new Vector3(.13f, 0, -Size.CrankRadius);
        _leftPedal.Scale = _rightPedal.Scale = new Vector3(.18f, .045f, .075f);
        _leftFoot.Position=left+new Vector3(-.015f,.04f,-.035f);
        _rightFoot.Position=right+new Vector3(.015f,.04f,-.035f);
        _leftFoot.Scale = _rightFoot.Scale = new Vector3(.15f,.065f,.22f);
        _leftSole.Position=_leftFoot.Position+new Vector3(0,-.05f,0);
        _rightSole.Position=_rightFoot.Position+new Vector3(0,-.05f,0);
        _leftSole.Scale = _rightSole.Scale = new Vector3(.13f,.018f,.19f);
        _leftAnkle.Position=left+new Vector3(-.015f,.035f,.035f);
        _rightAnkle.Position=right+new Vector3(.015f,.035f,.035f);
        _leftAnkle.Scale = _rightAnkle.Scale = Vector3.One * .065f;
        _leftFoot.Rotation = new Vector3((float)(.12 * Math.Sin(_crankAngle)), 0, 0);
        _rightFoot.Rotation = new Vector3((float)(.12 * Math.Sin(_crankAngle + Math.PI)), 0, 0);
        _leftSole.Rotation = _leftFoot.Rotation;
        _rightSole.Rotation = _rightFoot.Rotation;
        var leftHip=SeatedPose.LeftHip;
        var rightHip=SeatedPose.RightHip;
        var leftKnee2=CyclistAnimationMath.SolveKnee(new System.Numerics.Vector2(leftHip.Z,leftHip.Y),new System.Numerics.Vector2(left.Z,left.Y),Size.UpperLeg,Size.LowerLeg,-1);
        var rightKnee2=CyclistAnimationMath.SolveKnee(new System.Numerics.Vector2(rightHip.Z,rightHip.Y),new System.Numerics.Vector2(right.Z,right.Y),Size.UpperLeg,Size.LowerLeg,-1);
        var leftKnee=new Vector3(-.15f,leftKnee2.Y,leftKnee2.X);
        var rightKnee=new Vector3(.15f,rightKnee2.Y,rightKnee2.X);
        _leftKnee.Position=leftKnee; _rightKnee.Position=rightKnee;
        _leftKnee.Scale = _rightKnee.Scale = Vector3.One * .095f;
        SetRod(_leftUpperLeg,leftHip,leftKnee,.085f); SetRod(_leftLowerLeg,leftKnee,left,.055f);
        SetRod(_rightUpperLeg,rightHip,rightKnee,.085f); SetRod(_rightLowerLeg,rightKnee,right,.055f);
    }

#if DEBUG
    private void ValidateImportedModelBounds()
    {
        if (_bicycleModel is null || _riderModel is null) return;
        var modelMin = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
        var modelMax = new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);
        var meshCount = 0;
        var nonUnitScaleCount = 0;
        var largestMesh = string.Empty;
        var largestMeshDimension = 0f;
        foreach (Node child in GetChildren())
            if (child is Node3D node)
                ReportBounds(node, Transform3D.Identity, node.Name.ToString(), ref modelMin, ref modelMax,
                    ref meshCount, ref nonUnitScaleCount, ref largestMesh, ref largestMeshDimension);

        var size = modelMax - modelMin;
        GD.Print($"[Cyclist GLB] Runtime model bounds: {size}; meshes inspected: {meshCount}; " +
            $"non-unit scales: {nonUnitScaleCount}; largest mesh: {largestMesh} ({largestMeshDimension:F2} m)");
        if (size.X > 1.3f || size.Y > 2.1f || size.Z > 2.2f)
            GD.PushWarning($"[Cyclist GLB] Overall bounds exceed adult rider/bicycle sanity limits: {size}.");
    }

    private static void ReportBounds(Node3D node, Transform3D parentTransform, string safePath,
        ref Vector3 modelMin, ref Vector3 modelMax, ref int meshCount, ref int nonUnitScaleCount,
        ref string largestMesh, ref float largestMeshDimension)
    {
        var transform = parentTransform * node.Transform;
        var nodeScale = node.Scale;
        if (Math.Abs(nodeScale.X - 1) > .0001f || Math.Abs(nodeScale.Y - 1) > .0001f || Math.Abs(nodeScale.Z - 1) > .0001f)
        {
            nonUnitScaleCount++;
            if (nonUnitScaleCount <= 5) GD.Print($"[Cyclist GLB] Non-unit node scale: {safePath} = {nodeScale}");
        }
        if (node is MeshInstance3D instance && instance.Mesh is { } mesh)
        {
            // Godot's static mesh AABB does not represent a weighted mesh's evaluated
            // skeleton pose. The Blender export step validates the full skinned envelope.
            if (instance.Skin is not null || !instance.Skeleton.IsEmpty)
            {
                meshCount++;
            }
            else
            {
                var local = mesh.GetAabb();
                var transformedMin = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
                var transformedMax = new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);
                for (var x = 0; x < 2; x++)
                for (var y = 0; y < 2; y++)
                for (var z = 0; z < 2; z++)
                {
                    var corner = local.Position + new Vector3(x == 0 ? 0 : local.Size.X,
                        y == 0 ? 0 : local.Size.Y, z == 0 ? 0 : local.Size.Z);
                    var point = transform * corner;
                    transformedMin = transformedMin.Min(point);
                    transformedMax = transformedMax.Max(point);
                    modelMin = modelMin.Min(point);
                    modelMax = modelMax.Max(point);
                }
                var transformedSize = transformedMax - transformedMin;
                var largestDimension = Math.Max(transformedSize.X, Math.Max(transformedSize.Y, transformedSize.Z));
                if (largestDimension > largestMeshDimension)
                {
                    largestMeshDimension = largestDimension;
                    largestMesh = safePath;
                }
                var scale = instance.Scale;
                if (transformedSize.X > 1.25f || transformedSize.Y > 1.25f || transformedSize.Z > 1.25f)
                    GD.PushWarning($"[Cyclist GLB] Unexpectedly large mesh {safePath}: {transformedSize}.");
                if (!float.IsFinite(scale.X) || !float.IsFinite(scale.Y) || !float.IsFinite(scale.Z))
                    GD.PushWarning($"[Cyclist GLB] Invalid mesh scale on {safePath}: {scale}.");
                meshCount++;
            }
        }
        foreach (Node child in node.GetChildren())
            if (child is Node3D child3D)
                ReportBounds(child3D, transform, $"{safePath}/{child3D.Name}", ref modelMin, ref modelMax,
                    ref meshCount, ref nonUnitScaleCount, ref largestMesh, ref largestMeshDimension);
    }
#endif

    private static Vector3 PedalWorldPosition(double angle)
    {
        var offset=CyclistAnimationMath.PedalOffset(angle,Size.CrankRadius);
        return new Vector3(0,Size.CrankY+offset.Y,Size.CrankZ+offset.X);
    }

    private static System.Numerics.Vector3 ToNumerics(Vector3 value) => new(value.X, value.Y, value.Z);

    private static ArrayMesh BuildJerseyMesh()
    {
        // Shared ring normals give the jersey a shaped cloth silhouette instead of a stack
        // of visibly separate cylinders. The z-center shift defines its seated forward lean.
        var rings = new (float Y, float Z, float XRadius, float ZRadius)[]
        {
            (.905f, .18f, .125f, .105f),
            (.98f, .15f, .15f, .12f),
            (1.075f, .105f, .17f, .13f),
            (1.18f, .035f, .195f, .14f),
            (1.265f, -.035f, .19f, .125f),
            (1.315f, -.065f, .145f, .105f)
        };
        const int sides = 12;
        var surface = new SurfaceTool();
        surface.Begin(Mesh.PrimitiveType.Triangles);
        for (var ring = 0; ring < rings.Length - 1; ring++)
        for (var side = 0; side < sides; side++)
        {
            var a = TorsoPoint(rings[ring], side);
            var b = TorsoPoint(rings[ring + 1], side);
            var c = TorsoPoint(rings[ring + 1], side + 1);
            var d = TorsoPoint(rings[ring], side + 1);
            surface.AddVertex(a); surface.AddVertex(b); surface.AddVertex(c);
            surface.AddVertex(a); surface.AddVertex(c); surface.AddVertex(d);
        }
        surface.Index();
        surface.GenerateNormals();
        return surface.Commit();
    }

    private static Vector3 TorsoPoint((float Y, float Z, float XRadius, float ZRadius) ring, int side)
    {
        var angle = side * Mathf.Tau / 12f;
        return new Vector3(Mathf.Cos(angle) * ring.XRadius, ring.Y,
            ring.Z + Mathf.Sin(angle) * ring.ZRadius);
    }

    private Node3D AddWheel(string name,Vector3 position, Node3D parent)
    {
        var wheel=new Node3D{Name=name,Position=position}; parent.AddChild(wheel);
        foreach(var pair in new[]{(Mesh:TyreMesh,Material:_tyreMaterial),(Mesh:RimMesh,Material:_rimMaterial)})
            wheel.AddChild(new MeshInstance3D{Mesh=pair.Mesh,Rotation=new Vector3(0,0,-Mathf.Pi/2),MaterialOverride=pair.Material});
        var spokeRadius = .287f;
        for (var spoke = 0; spoke < 16; spoke++)
        {
            var angle = spoke * (float)Math.Tau / 16;
            var offset = new Vector3(spoke % 2 == 0 ? -.009f : .009f, Mathf.Cos(angle) * spokeRadius, Mathf.Sin(angle) * spokeRadius);
            var spokeMesh = new MeshInstance3D { Name = "Spoke", Mesh = RodMesh, MaterialOverride = _metalMaterial };
            wheel.AddChild(spokeMesh);
            SetRod(spokeMesh, new Vector3(-offset.X, 0, 0), offset, .0035f);
        }
        return wheel;
    }

    private MeshInstance3D AddRod(string name,Vector3 a,Vector3 b,float radius,Material material,Node3D? parent=null)
    {
        var rod=new MeshInstance3D{Name=name,Mesh=RodMesh,MaterialOverride=material};
        (parent ?? this).AddChild(rod); SetRod(rod,a,b,radius); return rod;
    }

    private MeshInstance3D AddLoftLimb(string name, Vector3 start, Vector3 end,
        float startRadius, float endRadius, Material material, Node3D parent)
    {
        var mesh = new MeshInstance3D { Name = name, Mesh = BuildLimbMesh(start, end, startRadius, endRadius), MaterialOverride = material };
        parent.AddChild(mesh);
        return mesh;
    }

    private static ArrayMesh BuildLimbMesh(Vector3 start, Vector3 end, float startRadius, float endRadius)
    {
        var direction = (end - start).Normalized();
        var reference = Math.Abs(direction.Dot(Vector3.Up)) > .9f ? Vector3.Right : Vector3.Up;
        var sideAxis = direction.Cross(reference).Normalized();
        var depthAxis = direction.Cross(sideAxis).Normalized();
        var rings = new (float Along, float Radius)[]
        {
            (0f, startRadius * .68f), (.14f, startRadius), (.78f, endRadius), (1f, endRadius * .72f)
        };
        const int sides = 10;
        var surface = new SurfaceTool();
        surface.Begin(Mesh.PrimitiveType.Triangles);
        for (var ring = 0; ring < rings.Length - 1; ring++)
        for (var side = 0; side < sides; side++)
        {
            var a = LimbPoint(rings[ring], side);
            var b = LimbPoint(rings[ring + 1], side);
            var c = LimbPoint(rings[ring + 1], side + 1);
            var d = LimbPoint(rings[ring], side + 1);
            surface.AddVertex(a); surface.AddVertex(b); surface.AddVertex(c);
            surface.AddVertex(a); surface.AddVertex(c); surface.AddVertex(d);
        }
        surface.Index();
        surface.GenerateNormals();
        return surface.Commit();

        Vector3 LimbPoint((float Along, float Radius) ring, int side)
        {
            var angle = side * Mathf.Tau / sides;
            var radial = sideAxis * Mathf.Cos(angle) + depthAxis * Mathf.Sin(angle);
            return start.Lerp(end, ring.Along) + radial * ring.Radius;
        }
    }
    private static void SetRod(MeshInstance3D rod,Vector3 a,Vector3 b,float radius)
    {
        var delta=b-a; var length=delta.Length(); if(length<.0001f)return;
        var direction=delta/length; var dot=Math.Clamp(Vector3.Up.Dot(direction),-1f,1f);
        var axis=Vector3.Up.Cross(direction);
        var rotation=axis.LengthSquared()<.00001f
            ? (dot<0?new Basis(Vector3.Right,Mathf.Pi):Basis.Identity)
            : new Basis(axis.Normalized(),Mathf.Acos(dot));
        var basis=rotation.Scaled(new Vector3(radius*2,length,radius*2));
        rod.Transform=new Transform3D(basis,(a+b)*.5f);
    }
    private MeshInstance3D AddBox(string name,Vector3 size,Vector3 position,Material material,Node3D? parent=null)
    {
        var mesh=new MeshInstance3D{Name=name,Mesh=UnitBoxMesh,Position=position,Scale=size,MaterialOverride=material}; (parent ?? this).AddChild(mesh); return mesh;
    }
    private MeshInstance3D AddSphere(string name,Vector3 position,Vector3 scale,Material material,Node3D? parent=null)
    {
        var mesh=new MeshInstance3D{Name=name,Mesh=UnitSphereMesh,Position=position,Scale=scale,MaterialOverride=material}; (parent ?? this).AddChild(mesh); return mesh;
    }
    private static StandardMaterial3D Material(Color color)=>new(){AlbedoColor=color,Roughness=.88f};
    private static int CountNodes(Node node)
    {
        var count=1; foreach(Node child in node.GetChildren()) count+=CountNodes(child); return count;
    }
}
