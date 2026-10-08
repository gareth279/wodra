using System;
using Godot;
using IndoorCycling.Core.Models;

/// <summary>Procedural articulated rider and road-bike proxy. It consumes ride state and never simulates it.</summary>
public partial class CyclistVisual : Node3D
{
    private sealed record Dimensions(double RiderHeight, float WheelRadius, float Wheelbase, float CrankRadius,
        float CrankY, float CrankZ, float SaddleY, float SaddleZ, float HeadY, float HeadZ,
        float HipY, float HipZ, float ShoulderY, float ShoulderZ, float UpperLeg, float LowerLeg);

    private static readonly Dimensions Size = new(1.78, .35f, 1.02f, .17f, .39f, .10f,
        .80f, .25f, 1.57f, -.22f, 1.00f, .21f, 1.34f, -.13f, .43f, .43f);
    private static readonly CylinderMesh RodMesh = new() { TopRadius = .5f, BottomRadius = .5f, Height = 1, RadialSegments = 8 };
    private static readonly BoxMesh UnitBoxMesh = new() { Size = Vector3.One };
    private static readonly SphereMesh UnitSphereMesh = new() { Radius = .5f, Height = 1, RadialSegments = 12, Rings = 8 };
    private static readonly TorusMesh TyreMesh = new() { InnerRadius = .315f, OuterRadius = .35f, Rings = 8, RingSegments = 20 };
    private static readonly TorusMesh RimMesh = new() { InnerRadius = .289f, OuterRadius = .301f, Rings = 6, RingSegments = 20 };
    private static readonly StandardMaterial3D[] FramePalette = [
        Material(new Color(.12f,.63f,.72f)), Material(new Color(.20f,.38f,.63f)),
        Material(new Color(.72f,.37f,.12f)), Material(new Color(.27f,.48f,.27f))];
    private static readonly StandardMaterial3D[] JerseyPalette = [
        Material(new Color(.88f,.23f,.13f)), Material(new Color(.18f,.42f,.78f)),
        Material(new Color(.87f,.66f,.16f)), Material(new Color(.34f,.62f,.38f)),
        Material(new Color(.66f,.34f,.68f)), Material(new Color(.91f,.91f,.88f))];
    private static readonly StandardMaterial3D TyreMaterial = Material(new Color(.025f,.03f,.04f));
    private static readonly StandardMaterial3D RimMaterial = Material(new Color(.67f,.72f,.76f));
    private static readonly StandardMaterial3D MetalMaterial = Material(new Color(.28f,.31f,.34f));
    private static readonly StandardMaterial3D ShortsMaterial = Material(new Color(.07f,.09f,.12f));
    private static readonly StandardMaterial3D SkinMaterial = Material(new Color(.78f,.57f,.43f));
    private static readonly StandardMaterial3D ShoeMaterial = Material(new Color(.88f,.87f,.79f));
    private static readonly StandardMaterial3D HelmetMaterial = Material(new Color(.95f,.75f,.12f));
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
    private MeshInstance3D _leftKnee = default!;
    private MeshInstance3D _rightKnee = default!;
    private Vector3 _previousForward;
    private bool _hasPreviousForward;
    private double _wheelAngle;
    private double _crankAngle;
    private double _leanRadians;
    private bool _built;

    public double WheelAngleRadians => _wheelAngle;
    public double CrankAngleRadians => _crankAngle;
    public double LeanDegrees => _leanRadians * 180 / Math.PI;
    public double CurrentGradientPercent { get; private set; }
    public double RiderHeightMeters => Size.RiderHeight;
    public int ApproximateNodeCount => CountNodes(this);
    public int ApproximateTriangleCount => 3000;

    public void Build(int visualVariant = 0)
    {
        if (_built) return;
        _built = true;
        _frameMaterial = FramePalette[Math.Abs(visualVariant) % FramePalette.Length];
        _jerseyMaterial = JerseyPalette[Math.Abs(visualVariant) % JerseyPalette.Length];
        var rearZ = Size.Wheelbase / 2;
        var frontZ = -Size.Wheelbase / 2;
        var wheelY = Size.WheelRadius;
        _rearWheel = AddWheel("RearWheel", new Vector3(0,wheelY,rearZ));
        _frontWheel = AddWheel("FrontWheel", new Vector3(0,wheelY,frontZ));

        var rearHub = new Vector3(0,wheelY,rearZ);
        var frontHub = new Vector3(0,wheelY,frontZ);
        var bottomBracket = new Vector3(0,Size.CrankY,Size.CrankZ);
        var seatTop = new Vector3(0,Size.SaddleY,Size.SaddleZ);
        var headLow = new Vector3(0,.72f,-.34f);
        var headHigh = new Vector3(0,.84f,-.39f);
        AddRod("SeatTube",bottomBracket,seatTop,.035f,_frameMaterial);
        AddRod("DownTube",bottomBracket,headLow,.035f,_frameMaterial);
        AddRod("TopTube",seatTop,headHigh,.03f,_frameMaterial);
        AddRod("ChainStay",rearHub,bottomBracket,.023f,_frameMaterial);
        AddRod("SeatStay",rearHub,seatTop,.022f,_frameMaterial);
        AddRod("Fork",frontHub,headLow,.025f,_frameMaterial);
        AddRod("HeadTube",headLow,headHigh,.045f,_frameMaterial);
        AddRod("SeatPost",seatTop,new Vector3(0,.88f,.27f),.025f,_metalMaterial);
        AddRod("Stem",headHigh,new Vector3(0,.91f,-.48f),.025f,_metalMaterial);
        AddRod("Handlebar",new Vector3(-.34f,.91f,-.48f),new Vector3(.34f,.91f,-.48f),.022f,_metalMaterial);
        AddBox("Saddle",new Vector3(.27f,.055f,.13f),new Vector3(0,.91f,.27f),_shortsMaterial);
        AddRod("CrankAxle",new Vector3(-.1f,Size.CrankY,Size.CrankZ),new Vector3(.1f,Size.CrankY,Size.CrankZ),.045f,_metalMaterial);

        AddRod("Torso",new Vector3(0,Size.HipY,Size.HipZ),new Vector3(0,Size.ShoulderY,Size.ShoulderZ),.19f,_jerseyMaterial);
        AddSphere("Shorts",new Vector3(0,.99f,.20f),new Vector3(.29f,.22f,.26f),_shortsMaterial);
        AddSphere("Head",new Vector3(0,Size.HeadY,Size.HeadZ),new Vector3(.16f,.19f,.16f),_skinMaterial);
        AddSphere("Helmet",new Vector3(0,1.70f,Size.HeadZ),new Vector3(.19f,.12f,.21f),HelmetMaterial);
        AddSphere("LeftHand",new Vector3(-.30f,.91f,-.47f),new Vector3(.075f,.065f,.08f),_skinMaterial);
        AddSphere("RightHand",new Vector3(.30f,.91f,-.47f),new Vector3(.075f,.065f,.08f),_skinMaterial);
        AddRod("LeftUpperArm",new Vector3(-.17f,1.32f,-.13f),new Vector3(-.28f,1.11f,-.34f),.052f,_jerseyMaterial);
        AddRod("LeftForearm",new Vector3(-.28f,1.11f,-.34f),new Vector3(-.30f,.92f,-.47f),.043f,_skinMaterial);
        AddRod("RightUpperArm",new Vector3(.17f,1.32f,-.13f),new Vector3(.28f,1.11f,-.34f),.052f,_jerseyMaterial);
        AddRod("RightForearm",new Vector3(.28f,1.11f,-.34f),new Vector3(.30f,.92f,-.47f),.043f,_skinMaterial);

        _leftCrank=AddRod("LeftCrank",Vector3.Zero,Vector3.Up,.024f,_metalMaterial);
        _rightCrank=AddRod("RightCrank",Vector3.Zero,Vector3.Up,.024f,_metalMaterial);
        _leftPedal=AddBox("LeftPedal",new Vector3(.18f,.045f,.075f),Vector3.Zero,_metalMaterial);
        _rightPedal=AddBox("RightPedal",new Vector3(.18f,.045f,.075f),Vector3.Zero,_metalMaterial);
        _leftUpperLeg=AddRod("LeftThigh",Vector3.Zero,Vector3.Up,.085f,_shortsMaterial);
        _leftLowerLeg=AddRod("LeftShin",Vector3.Zero,Vector3.Up,.055f,_skinMaterial);
        _rightUpperLeg=AddRod("RightThigh",Vector3.Zero,Vector3.Up,.085f,_shortsMaterial);
        _rightLowerLeg=AddRod("RightShin",Vector3.Zero,Vector3.Up,.055f,_skinMaterial);
        _leftFoot=AddBox("LeftFoot",new Vector3(.14f,.08f,.22f),Vector3.Zero,_shoeMaterial);
        _rightFoot=AddBox("RightFoot",new Vector3(.14f,.08f,.22f),Vector3.Zero,_shoeMaterial);
        _leftKnee=AddSphere("LeftKnee",Vector3.Zero,new Vector3(.105f,.105f,.105f),_shortsMaterial);
        _rightKnee=AddSphere("RightKnee",Vector3.Zero,new Vector3(.105f,.105f,.105f),_shortsMaterial);
        UpdatePedalingGeometry();
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
        _wheelAngle=CyclistAnimationMath.AdvanceWheelAngle(_wheelAngle,speed,delta,Size.WheelRadius,paused);
        _crankAngle=CyclistAnimationMath.AdvanceCrankAngle(_crankAngle,cadence,delta,paused);
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
        UpdatePedalingGeometry();
    }

    private void UpdatePedalingGeometry()
    {
        var bb=new Vector3(0,Size.CrankY,Size.CrankZ);
        var left=PedalWorldPosition(_crankAngle);
        var right=PedalWorldPosition(_crankAngle+Math.PI);
        left.X=-.13f; right.X=.13f;
        SetRod(_leftCrank,bb,left,.024f); SetRod(_rightCrank,bb,right,.024f);
        _leftPedal.Position=left; _rightPedal.Position=right;
        _leftFoot.Position=left+new Vector3(-.015f,.04f,-.035f);
        _rightFoot.Position=right+new Vector3(.015f,.04f,-.035f);
        var leftHip=new Vector3(-.13f,Size.HipY,Size.HipZ);
        var rightHip=new Vector3(.13f,Size.HipY,Size.HipZ);
        var leftKnee2=CyclistAnimationMath.SolveKnee(new System.Numerics.Vector2(leftHip.Z,leftHip.Y),new System.Numerics.Vector2(left.Z,left.Y),Size.UpperLeg,Size.LowerLeg,1);
        var rightKnee2=CyclistAnimationMath.SolveKnee(new System.Numerics.Vector2(rightHip.Z,rightHip.Y),new System.Numerics.Vector2(right.Z,right.Y),Size.UpperLeg,Size.LowerLeg,-1);
        var leftKnee=new Vector3(-.15f,leftKnee2.Y,leftKnee2.X);
        var rightKnee=new Vector3(.15f,rightKnee2.Y,rightKnee2.X);
        _leftKnee.Position=leftKnee; _rightKnee.Position=rightKnee;
        SetRod(_leftUpperLeg,leftHip,leftKnee,.085f); SetRod(_leftLowerLeg,leftKnee,left,.055f);
        SetRod(_rightUpperLeg,rightHip,rightKnee,.085f); SetRod(_rightLowerLeg,rightKnee,right,.055f);
    }

    private static Vector3 PedalWorldPosition(double angle)
    {
        var offset=CyclistAnimationMath.PedalOffset(angle,Size.CrankRadius);
        return new Vector3(0,Size.CrankY+offset.Y,Size.CrankZ+offset.X);
    }

    private static System.Numerics.Vector3 ToNumerics(Vector3 value) => new(value.X, value.Y, value.Z);

    private Node3D AddWheel(string name,Vector3 position)
    {
        var wheel=new Node3D{Name=name,Position=position}; AddChild(wheel);
        foreach(var pair in new[]{(Mesh:TyreMesh,Material:_tyreMaterial),(Mesh:RimMesh,Material:_rimMaterial)})
            wheel.AddChild(new MeshInstance3D{Mesh=pair.Mesh,Rotation=new Vector3(0,0,-Mathf.Pi/2),MaterialOverride=pair.Material});
        return wheel;
    }

    private MeshInstance3D AddRod(string name,Vector3 a,Vector3 b,float radius,Material material)
    {
        var rod=new MeshInstance3D{Name=name,Mesh=RodMesh,MaterialOverride=material};
        AddChild(rod); SetRod(rod,a,b,radius); return rod;
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
    private MeshInstance3D AddBox(string name,Vector3 size,Vector3 position,Material material)
    {
        var mesh=new MeshInstance3D{Name=name,Mesh=UnitBoxMesh,Position=position,Scale=size,MaterialOverride=material}; AddChild(mesh); return mesh;
    }
    private MeshInstance3D AddSphere(string name,Vector3 position,Vector3 scale,Material material)
    {
        var mesh=new MeshInstance3D{Name=name,Mesh=UnitSphereMesh,Position=position,Scale=scale,MaterialOverride=material}; AddChild(mesh); return mesh;
    }
    private static StandardMaterial3D Material(Color color)=>new(){AlbedoColor=color,Roughness=.88f};
    private static int CountNodes(Node node)
    {
        var count=1; foreach(Node child in node.GetChildren()) count+=CountNodes(child); return count;
    }
}
