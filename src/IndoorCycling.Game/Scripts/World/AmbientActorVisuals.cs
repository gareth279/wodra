using Godot;
using System;

/// <summary>Low-poly, non-collidable car proxy. Meshes and palette materials are shared.</summary>
public partial class AmbientCarVisual : Node3D
{
    private static readonly BoxMesh BodyMesh = new() { Size = new Vector3(1.82f, .55f, 4.45f) };
    private static readonly BoxMesh CabinMesh = new() { Size = new Vector3(1.45f, .68f, 2.15f) };
    private static readonly BoxMesh WindscreenMesh = new() { Size = new Vector3(1.36f, .38f, .06f) };
    private static readonly CylinderMesh WheelMesh = new() { TopRadius = .32f, BottomRadius = .32f, Height = .20f, RadialSegments = 8 };
    private static readonly StandardMaterial3D[] BodyPalette =
    [
        Material(new Color(.55f,.22f,.16f)), Material(new Color(.20f,.31f,.38f)),
        Material(new Color(.68f,.62f,.45f)), Material(new Color(.22f,.32f,.25f)),
        Material(new Color(.60f,.60f,.57f)), Material(new Color(.28f,.27f,.26f))
    ];
    private static readonly StandardMaterial3D Glass = Material(new Color(.16f,.22f,.27f));
    private static readonly StandardMaterial3D Tyre = Material(new Color(.035f,.04f,.045f));

    public void Build(Random random)
    {
        var paint = BodyPalette[random.Next(BodyPalette.Length)];
        AddPart("Body", BodyMesh, paint, new Vector3(0, .72f, 0));
        AddPart("Cabin", CabinMesh, paint, new Vector3(0, 1.29f, .10f));
        AddPart("Windscreen", WindscreenMesh, Glass, new Vector3(0, 1.32f, -.98f));
        for (var xSide = -1; xSide <= 1; xSide += 2)
        for (var zSide = -1; zSide <= 1; zSide += 2)
        {
            var wheel = new MeshInstance3D { Name = "Wheel", Mesh = WheelMesh, MaterialOverride = Tyre,
                Position = new Vector3(xSide * .81f, .32f, zSide * 1.35f), Rotation = new Vector3(0, 0, Mathf.Pi / 2) };
            AddChild(wheel);
        }
    }

    public void SetPose(System.Numerics.Vector3 position, System.Numerics.Vector3 heading)
    {
        GlobalPosition = new Vector3(position.X, position.Y, position.Z);
        var forward = new Vector3(heading.X, heading.Y, heading.Z);
        if (forward.LengthSquared() > .00001f) LookAt(GlobalPosition + forward, Vector3.Up);
    }

    public const int ApproximateTriangleCount = 164;

    private void AddPart(string name, Mesh mesh, Material material, Vector3 position) =>
        AddChild(new MeshInstance3D { Name = name, Mesh = mesh, MaterialOverride = material, Position = position });

    private static StandardMaterial3D Material(Color color) => new() { AlbedoColor = color, Roughness = .9f };
}

/// <summary>Simple four-limb walking proxy with a deterministic alternating gait.</summary>
public partial class AmbientPedestrianVisual : Node3D
{
    private static readonly BoxMesh TorsoMesh = new() { Size = new Vector3(.36f, .55f, .22f) };
    private static readonly SphereMesh HeadMesh = new() { Radius = .14f, Height = .28f, RadialSegments = 6, Rings = 3 };
    private static readonly CylinderMesh LimbMesh = new() { TopRadius = .055f, BottomRadius = .055f, Height = 1, RadialSegments = 6 };
    private static readonly StandardMaterial3D[] Shirts =
    [ Material(new Color(.30f,.37f,.47f)), Material(new Color(.55f,.31f,.22f)),
      Material(new Color(.39f,.43f,.30f)), Material(new Color(.50f,.48f,.39f)) ];
    private static readonly StandardMaterial3D[] Trousers =
    [ Material(new Color(.12f,.15f,.18f)), Material(new Color(.28f,.25f,.20f)), Material(new Color(.20f,.27f,.30f)) ];
    private static readonly StandardMaterial3D Skin = Material(new Color(.67f,.49f,.37f));
    private readonly MeshInstance3D[] _limbs = new MeshInstance3D[4];
    private double _phase;

    public void Build(Random random)
    {
        AddChild(new MeshInstance3D { Name = "Torso", Mesh = TorsoMesh, MaterialOverride = Shirts[random.Next(Shirts.Length)], Position = new Vector3(0, 1.08f, 0) });
        AddChild(new MeshInstance3D { Name = "Head", Mesh = HeadMesh, MaterialOverride = Skin, Position = new Vector3(0, 1.49f, 0) });
        _limbs[0] = Limb("LeftLeg", Trousers[random.Next(Trousers.Length)]);
        _limbs[1] = Limb("RightLeg", Trousers[random.Next(Trousers.Length)]);
        _limbs[2] = Limb("LeftArm", Shirts[random.Next(Shirts.Length)]);
        _limbs[3] = Limb("RightArm", Shirts[random.Next(Shirts.Length)]);
        UpdateGait(0);
    }

    public void AdvanceGait(double deltaSeconds, float speedMetersPerSecond)
    {
        _phase += deltaSeconds * (3.0 + Math.Clamp(speedMetersPerSecond, 0, 2) * .35);
        UpdateGait(_phase);
    }

    public void SetPose(System.Numerics.Vector3 position, System.Numerics.Vector3 heading)
    {
        GlobalPosition = new Vector3(position.X, position.Y, position.Z);
        var forward = new Vector3(heading.X, heading.Y, heading.Z);
        if (forward.LengthSquared() > .00001f) LookAt(GlobalPosition + forward, Vector3.Up);
    }

    public const int ApproximateTriangleCount = 144;

    private MeshInstance3D Limb(string name, Material material)
    {
        var limb = new MeshInstance3D { Name = name, Mesh = LimbMesh, MaterialOverride = material };
        AddChild(limb); return limb;
    }

    private void UpdateGait(double phase)
    {
        if (_limbs[0] is null) return;
        var swing = (float)Math.Sin(phase) * .24f;
        SetLimb(_limbs[0], new Vector3(-.11f,.79f,0), new Vector3(-.11f,.12f,swing));
        SetLimb(_limbs[1], new Vector3(.11f,.79f,0), new Vector3(.11f,.12f,-swing));
        SetLimb(_limbs[2], new Vector3(-.20f,1.29f,0), new Vector3(-.25f,.88f,-swing));
        SetLimb(_limbs[3], new Vector3(.20f,1.29f,0), new Vector3(.25f,.88f,swing));
    }

    private static void SetLimb(MeshInstance3D limb, Vector3 from, Vector3 to)
    {
        var delta = to - from; var length = delta.Length();
        if (length < .0001f) return;
        limb.Position = (from + to) * .5f;
        limb.Basis = new Basis(new Quaternion(Vector3.Up, delta / length));
        limb.Scale = new Vector3(1, length, 1);
    }

    private static StandardMaterial3D Material(Color color) => new() { AlbedoColor = color, Roughness = .95f };
}
