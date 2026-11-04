using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;

/// <summary>
/// Draws individually reviewed route observations as small visual-only models inside the existing
/// route geometry. Coordinates stay tied to the canonical VisualRoute; this layer has no ride state.
/// </summary>
public partial class RouteSceneLayer : Node3D
{
    public sealed record ClearanceCorrection(string FeatureId, string ObjectType, double ChainageMeters,
        float SourceLateralMeters, float VisualLateralMeters, float ClearanceMeters, bool Resolved);
    public const double AuthoredSceneReplacementRadiusMeters = 36;
    public const double FeatureVisibleDistanceMeters = 220;
    private readonly List<(double ChainageMeters, Node3D Node)> _routeFeatures = [];
    private double _routeLengthMeters;
    private bool _wrapsAtRouteEnd;
    public int FeaturesRendered { get; private set; }
    public int VisibleFeatureCount { get; private set; }
    public int TimberFenceFeatures { get; private set; }
    public int RoadMarkingFeatures { get; private set; }
    public int AuthoredAssetFeatures { get; private set; }
    public int AuthoredAssetMeshInstances { get; private set; }
    public int RoadIntrusionsDetected { get; private set; }
    public int RoadIntrusionsRelocated { get; private set; }
    public int RoadIntrusionsUnresolved { get; private set; }
    public int GroundTrianglesClippedAgainstRoads { get; private set; }
    public IReadOnlyList<ClearanceCorrection> ClearanceCorrections => _clearanceCorrections;
    private readonly List<ClearanceCorrection> _clearanceCorrections = [];

    public void Configure(VisualRoute route, RouteSceneData data, RoadCorridorGeometry? sharedCorridor = null)
    {
        _routeLengthMeters = route.LengthMeters;
        _wrapsAtRouteEnd = !route.IsPointToPoint;
        var corridor = sharedCorridor;
        var authoredIntersection = data.Features.FirstOrDefault(feature => feature.ObjectType == "authored-scene-asset");
        var terrain = RouteTerrainDataLoader.TryLoadEmbedded(typeof(RouteCatalog).Assembly,
            $"Wodra.GameData.Routes.{data.Id}.terrain.json", data.Id);
        foreach (var feature in data.Features)
        {
            if (authoredIntersection is not null && feature != authoredIntersection
                && Math.Abs(feature.RouteChainageMeters - authoredIntersection.RouteChainageMeters) <= AuthoredSceneReplacementRadiusMeters
                && feature.ObjectType is "timber-fence" or "picket-fence" or "broadleaf-tree" or "conifer-tree"
                    or "shrub" or "hedge" or "grass-verge" or "planting-bed" or "utility-pole")
                continue;
            var pose = route.GetPoseAtDistance(feature.RouteChainageMeters);
            var routeForward = ProceduralRoad.ToGodot(pose.Forward);
            System.Numerics.Vector3 routeTangent = new(routeForward.X, routeForward.Y, routeForward.Z);
            routeTangent = RouteSceneTransformMath.HorizontalForward(routeTangent);
            var forward = new Vector3(routeTangent.X, 0, routeTangent.Z);
            if (forward.LengthSquared() < 0.9f) continue;
            var routePosition = ProceduralRoad.ToGodot(pose.Position);
            var visualLateral = (float)feature.LateralPositionMeters;
            var visualForward = (float)feature.ForwardOffsetMeters;
            var footprint = RoadCorridorGeometry.AuthoredFeatureFootprint(feature.ObjectType,
                feature.LengthMeters, feature.WidthMeters);
            if (corridor is not null && footprint is { } occupied)
            {
                var placement = corridor.FindSafeRoutePlacement(route, feature.RouteChainageMeters,
                    visualLateral, visualForward, occupied.LengthMeters, occupied.WidthMeters,
                    (float)feature.OrientationDegrees, maximumShiftMeters: 24);
                if (placement.Adjusted || !placement.Resolved)
                {
                    RoadIntrusionsDetected++;
                    if (placement.Resolved) RoadIntrusionsRelocated++;
                    else RoadIntrusionsUnresolved++;
                    visualLateral = placement.VisualLateralMeters;
                    visualForward = placement.VisualForwardMeters;
                    _clearanceCorrections.Add(new ClearanceCorrection(feature.Id, feature.ObjectType,
                        feature.RouteChainageMeters, (float)feature.LateralPositionMeters, visualLateral,
                        placement.Clearance.ClearanceMeters, placement.Resolved));
                }
            }
            var originNumerics = RouteSceneTransformMath.Position(pose.Position, routeTangent,
                visualLateral, visualForward);
            var origin = ProceduralRoad.ToGodot(originNumerics);
            var roadPaint = feature.ObjectType is "yield-triangle" or "give-way-line" or "yellow-kerb-stripe" or "white-lane-dash";
            if (roadPaint)
                origin.Y = ProceduralRoad.ToGodot(pose.Position).Y + .06f;
            else if (terrain is not null && TerrainMeshGeometry.TrySampleGroundY(terrain, origin.X, origin.Z, out var groundY))
                origin.Y = groundY + .025f;
            var node = new Node3D { Name = SafeName(feature.Id), Position = origin };
            node.Rotation = new Vector3(0, RouteSceneTransformMath.YawRadians(routeTangent)
                + (float)(feature.OrientationDegrees * Math.PI / 180), 0);
            AddChild(node);
            switch (feature.ObjectType)
            {
                case "timber-fence": BuildFence(node, feature); TimberFenceFeatures++; break;
                case "picket-fence": BuildFence(node, feature with { Variant = "picket" }); TimberFenceFeatures++; break;
                case "broadleaf-tree": BuildTree(node, feature); break;
                case "conifer-tree": BuildConifer(node, feature); break;
                case "shrub": BuildHedge(node, feature); break;
                case "hedge": BuildHedge(node, feature); break;
                case "grass-verge": BuildGrassVerge(node, feature, corridor); break;
                case "planting-bed": BuildGrassVerge(node, feature, corridor); BuildHedge(node, feature); break;
                case "low-boundary-wall": BuildBoundaryWall(node, feature, false); break;
                case "retaining-wall": BuildBoundaryWall(node, feature, true); break;
                case "utility-pole": BuildUtilityPole(node, feature); break;
                case "give-way-sign": BuildGiveWaySign(node, feature); break;
                case "authored-scene-asset": BuildAuthoredSceneAsset(node, feature); AuthoredAssetFeatures++; break;
                case "yield-triangle": BuildYieldTriangle(node, feature); RoadMarkingFeatures++; break;
                case "give-way-line": BuildGiveWayLine(node, feature); RoadMarkingFeatures++; break;
                case "yellow-kerb-stripe": BuildYellowKerb(node, feature); RoadMarkingFeatures++; break;
                case "white-lane-dash": BuildLaneDash(node, feature); RoadMarkingFeatures++; break;
            }
            _routeFeatures.Add((feature.RouteChainageMeters, node));
            FeaturesRendered++;
        }
        GD.Print($"[RoadCorridor] authored scenery detected={RoadIntrusionsDetected} relocated={RoadIntrusionsRelocated} unresolved={RoadIntrusionsUnresolved}");
        foreach (var correction in _clearanceCorrections)
            GD.Print($"[RoadCorridor] feature={correction.FeatureId} type={correction.ObjectType} " +
                $"chainage={correction.ChainageMeters:F2} sourceLateral={correction.SourceLateralMeters:F2}m " +
                $"visualLateral={correction.VisualLateralMeters:F2}m clearance={correction.ClearanceMeters:F2}m " +
                $"status={(correction.Resolved ? "corrected" : "unresolved")}");
    }


    /// <summary>Keep observations from distant checkpoints out of the current camera view.</summary>
    public void UpdateForRideDistance(double rideDistanceMeters)
    {
        if (_routeLengthMeters <= 0) return;
        var wrapped = _wrapsAtRouteEnd
            ? ((rideDistanceMeters % _routeLengthMeters) + _routeLengthMeters) % _routeLengthMeters
            : Math.Clamp(rideDistanceMeters, 0, _routeLengthMeters);
        var visible = 0;
        foreach (var feature in _routeFeatures)
        {
            var difference = Math.Abs(feature.ChainageMeters - wrapped);
            var along = _wrapsAtRouteEnd ? Math.Min(difference, _routeLengthMeters - difference) : difference;
            feature.Node.Visible = along <= FeatureVisibleDistanceMeters;
            if (feature.Node.Visible) visible++;
        }
        VisibleFeatureCount = visible;
    }

    private void BuildAuthoredSceneAsset(Node3D root, RouteSceneFeature feature)
    {
        if (string.IsNullOrWhiteSpace(feature.Variant) || !feature.Variant.StartsWith("res://", StringComparison.Ordinal))
            throw new InvalidDataException($"Authored scene feature '{feature.Id}' must use a project resource path.");
        var packed = ResourceLoader.Load<PackedScene>(feature.Variant);
        if (packed is null)
            throw new InvalidDataException($"Authored scene feature '{feature.Id}' asset '{feature.Variant}' could not be loaded.");
        var instance = packed.Instantiate<Node3D>();
        instance.Name = SafeName(feature.Id + "_BlenderAsset");
        var meshInstances = instance.FindChildren("*", nameof(MeshInstance3D), true, false)
            .OfType<MeshInstance3D>().ToArray();
        AuthoredAssetMeshInstances += meshInstances.Length;
        root.AddChild(instance);
    }

    private static void BuildFence(Node3D root, RouteSceneFeature feature)
    {
        var length = (float)feature.LengthMeters; var height = (float)feature.HeightMeters;
        if (length <= 0 || height <= 0) return;
        var boards = new List<(Transform3D Transform, Color Color)>();
        var picket = feature.Variant == "picket";
        var boardWidth = picket ? .08f : .105f;
        var spacing = picket ? .22f : .13f;
        var boardHeight = picket ? height * .8f : height;
        var count = Math.Max(2, (int)MathF.Ceiling(length / spacing));
        var pale = ParseColour(feature.Colour, new Color(.56f, .58f, .53f));
        for (var i = 0; i < count; i++)
        {
            var x = -length / 2 + length * i / (count - 1);
            boards.Add((MakeTransform(new Vector3(x, picket ? height*.57f : height/2, 0), new Vector3(boardWidth, boardHeight, .055f)),
                pale * (i % 5 == 0 ? .78f : .92f + (i % 3) * .035f)));
        }
        var rail = new BoxMesh { Size = new Vector3(length, .09f, .11f) };
        AddMesh(root, rail, pale.Darkened(.12f), new Vector3(0, height * .24f, .045f));
        AddMesh(root, rail, pale.Darkened(.12f), new Vector3(0, height * .72f, .045f));
        AddMultiBoxes(root, "WeatheredPalingBoards", boards);
        var postMesh = new BoxMesh { Size = new Vector3(.16f, height + .12f, .17f) };
        for (var x = -length / 2; x <= length / 2 + .01f; x += 2.0f)
            AddMesh(root, postMesh, pale.Darkened(.2f), new Vector3(x, height / 2, -.04f));
    }

    private static void BuildConifer(Node3D root, RouteSceneFeature feature)
    {
        var height = (float)feature.HeightMeters; var radius = MathF.Max(.8f,(float)feature.WidthMeters/2);
        AddMesh(root,new CylinderMesh { TopRadius=.11f,BottomRadius=.18f,Height=height*.78f,RadialSegments=7 },
            new Color(.31f,.23f,.16f),new Vector3(0,height*.39f,0));
        var colors=new[] { new Color(.15f,.31f,.21f),new Color(.19f,.36f,.24f),new Color(.22f,.39f,.27f) };
        for (var tier=0;tier<3;tier++)
        {
            var tierHeight=height*(.48f-tier*.07f); var tierRadius=radius*(1f-tier*.27f);
            AddMesh(root,new CylinderMesh { TopRadius=0,BottomRadius=tierRadius,Height=tierHeight,RadialSegments=14 },
                colors[tier],new Vector3(0,height*(.29f+tier*.22f)+tierHeight*.42f,0));
        }
    }

    private static void BuildBoundaryWall(Node3D root, RouteSceneFeature feature, bool retaining)
    {
        var length=MathF.Max(.5f,(float)feature.LengthMeters); var height=MathF.Max(.25f,(float)feature.HeightMeters);
        var depth=MathF.Max(.15f,(float)feature.WidthMeters);
        var material=retaining?new Color(.47f,.45f,.40f):new Color(.57f,.56f,.50f);
        AddMesh(root,new BoxMesh { Size=new Vector3(length,height,depth) },
            ParseColour(feature.Colour,material),new Vector3(0,height/2,0));
    }

    private static void BuildTree(Node3D root, RouteSceneFeature feature)
    {
        var height = (float)feature.HeightMeters; var radius = MathF.Max(1.2f, (float)feature.WidthMeters / 2);
        var trunkMaterial = Material(new Color(.29f, .20f, .14f));
        root.AddChild(new MeshInstance3D
        {
            Name = "MatureTreeTrunk", Mesh = new CylinderMesh { TopRadius = .18f, BottomRadius = .32f,
                Height = height * .60f, RadialSegments = 8 },
            Position = new Vector3(0, height * .30f, 0), MaterialOverride = trunkMaterial
        });
        for (var side = -1; side <= 1; side += 2)
            root.AddChild(new MeshInstance3D
            {
                Name = $"MatureTreeBranch{side}", Mesh = new CylinderMesh { TopRadius = .045f,
                    BottomRadius = .11f, Height = height * .24f, RadialSegments = 7 },
                Position = new Vector3(side * radius * .20f, height * .56f, 0),
                Rotation = new Vector3(0, 0, side * -.82f), MaterialOverride = trunkMaterial
            });
        // A lightly irregular shared crown mesh avoids the repeated sphere primitive silhouette;
        // small overlapping lobes give the silhouette the dense, layered volume seen in gardens.
        var baseShade = ParseColour(feature.Colour, new Color(.22f,.37f,.23f));
        var shades = new[] { baseShade, baseShade.Lightened(.08f), baseShade.Darkened(.06f), baseShade.Lightened(.035f) };
        root.AddChild(new MeshInstance3D
        {
            Name = "OrganicBroadleafCrown", Mesh = OrganicCanopyMesh(feature.Id),
            Position = new Vector3(0,height*.69f,0), Scale = new Vector3(radius,radius*.82f,radius),
            MaterialOverride = Material(shades[0])
        });
        var lobeMesh = new SphereMesh { Radius = 1, Height = 1.5f, RadialSegments = 24, Rings = 16 };
        var lobes = new[] { new Vector3(radius*.32f,height*.66f,0), new Vector3(-radius*.32f,height*.65f,.04f),
            new Vector3(0,height*.65f,radius*.31f), new Vector3(.02f,height*.64f,-radius*.31f) };
        for (var i = 0; i < lobes.Length; i++)
            root.AddChild(new MeshInstance3D { Name = $"BroadleafCrownLobe{i}", Mesh = lobeMesh,
                Position = lobes[i], Scale = new Vector3(radius*.43f,radius*.34f,radius*.39f),
                MaterialOverride = Material(shades[(i+1)%shades.Length]) });
    }

    private static ArrayMesh OrganicCanopyMesh(string id)
    {
        const int rings = 12, segments = 24;
        var phase = 0f;
        unchecked { foreach (var character in id) phase = (phase * 31 + character) % 997; }
        phase = phase / 997f * MathF.Tau;
        var vertices = new Vector3[rings+1,segments];
        for (var ring = 0; ring <= rings; ring++)
        {
            var phi = MathF.PI * ring / rings;
            var vertical = MathF.Cos(phi);
            var radial = MathF.Sin(phi);
            for (var segment = 0; segment < segments; segment++)
            {
                var angle = MathF.Tau * segment / segments;
                var irregularity = 1 + .055f*MathF.Sin(3*angle+phase)
                    + .025f*MathF.Sin(7*angle-phase*.7f) + .025f*MathF.Cos(2*angle+phi*4+phase);
                vertices[ring,segment] = new Vector3(radial*MathF.Cos(angle)*irregularity,
                    vertical*(.92f+.035f*MathF.Sin(4*angle+phase)),radial*MathF.Sin(angle)*irregularity);
            }
        }
        var tool = new SurfaceTool(); tool.Begin(Mesh.PrimitiveType.Triangles);
        for (var ring = 0; ring < rings; ring++)
        for (var segment = 0; segment < segments; segment++)
        {
            var next = (segment+1)%segments;
            var a=vertices[ring,segment]; var b=vertices[ring,next];
            var c=vertices[ring+1,segment]; var d=vertices[ring+1,next];
            tool.AddVertex(a); tool.AddVertex(c); tool.AddVertex(b);
            tool.AddVertex(b); tool.AddVertex(c); tool.AddVertex(d);
        }
        tool.Index(); tool.GenerateNormals();
        return (ArrayMesh)tool.Commit();
    }

    private static void BuildHedge(Node3D root, RouteSceneFeature feature)
    {
        var length = MathF.Max(1, (float)feature.LengthMeters);
        var height = MathF.Max(.7f, (float)feature.HeightMeters);
        var radius = MathF.Max(.35f, (float)feature.WidthMeters / 2);
        var colour = ParseColour(feature.Colour, new Color(.18f,.34f,.19f));
        // A continuous clipped body prevents a row of separated round blobs reading as lollipops.
        AddMesh(root, new BoxMesh { Size = new Vector3(length, height * .68f, MathF.Max(.25f, radius * 1.35f)) },
            colour.Darkened(.08f), new Vector3(0,height*.34f,0));
        var mesh = new SphereMesh { Radius = 1, Height = 1.5f, RadialSegments = 20, Rings = 12 };
        var material = Material(colour);
        for (var x = -length / 2 + radius*.55f; x <= length / 2; x += MathF.Max(.65f,radius*.72f))
            root.AddChild(new MeshInstance3D { Name = "HedgeCanopy", Mesh = mesh,
                Position = new Vector3(x, height * .62f, 0), Scale = new Vector3(radius*.68f, height*.46f, radius*.72f),
                MaterialOverride = material });
    }

    private void BuildGrassVerge(Node3D root, RouteSceneFeature feature, RoadCorridorGeometry? corridor)
    {
        var width = MathF.Max(.2f,(float)feature.WidthMeters); var length = MathF.Max(.2f,(float)feature.LengthMeters);
        var z0=-length/2; var z1=-length*.30f; var z2=length*.12f; var z3=length/2;
        var vertices = new[] { new Vector3(-width*.50f,.02f,z0), new Vector3(width*.50f,.02f,z0),
            new Vector3(width*.42f,.02f,z1), new Vector3(width*.31f,.02f,z2), new Vector3(width*.47f,.02f,z3),
            new Vector3(-width*.48f,.02f,z3), new Vector3(-width*.36f,.02f,z2), new Vector3(-width*.46f,.02f,z1) };
        var retained = new List<Vector3>();
        for (var i=1;i<vertices.Length-1;i++)
        {
            var a=vertices[0]; var b=vertices[i]; var c=vertices[i+1];
            if (corridor is not null)
            {
                var transform=root.GlobalTransform;
                var wa=transform*a; var wb=transform*b; var wc=transform*c;
                var measured=corridor.MeasurePolygon([new System.Numerics.Vector2(wa.X,wa.Z),
                    new System.Numerics.Vector2(wb.X,wb.Z),new System.Numerics.Vector2(wc.X,wc.Z)]);
                if (measured.Intersects || measured.ClearanceMeters < RoadCorridorGeometry.RoadsideClearanceMeters)
                { GroundTrianglesClippedAgainstRoads++; continue; }
            }
            retained.AddRange([a,b,c]);
        }
        if (retained.Count==0) return;
        var mesh = new ArrayMesh(); var arrays = new Godot.Collections.Array(); arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex]=retained.ToArray();
        arrays[(int)Mesh.ArrayType.Normal]=Enumerable.Repeat(Vector3.Up,retained.Count).ToArray();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles,arrays);
        AddMesh(root, mesh, ParseColour(feature.Colour, new Color(.34f,.47f,.25f)), Vector3.Zero);
    }

    private static void BuildUtilityPole(Node3D root, RouteSceneFeature feature)
    {
        var height = (float)feature.HeightMeters;
        AddMesh(root, new CylinderMesh { TopRadius = .095f, BottomRadius = .15f, Height = height,
            RadialSegments = 8 }, new Color(.52f,.49f,.40f), new Vector3(0,height/2,0));
        AddMesh(root, new BoxMesh { Size = new Vector3(1.25f,.09f,.10f) }, new Color(.39f,.34f,.27f),
            new Vector3(0,height-.5f,0));
    }

    private static void BuildGiveWaySign(Node3D root, RouteSceneFeature feature)
    {
        var height = (float)feature.HeightMeters;
        var signWidth = Math.Clamp((float)feature.LengthMeters,.55f,.88f);
        var signHeight = signWidth * 1.1f;
        var signCenter = height - signHeight / 2;
        var postHeight = Math.Max(.5f,signCenter-signHeight/2+.12f);
        AddMesh(root, new CylinderMesh { TopRadius = .045f, BottomRadius = .065f, Height = postHeight,
            RadialSegments = 8 }, new Color(.50f,.52f,.50f), new Vector3(0,postHeight/2,0));
        var outer = TriangleMesh(signWidth, signHeight, new Color(.78f,.12f,.10f));
        var inner = TriangleMesh(signWidth*.82f, signHeight*.84f, new Color(.96f,.95f,.89f));
        AddMesh(root, outer, new Color(.76f,.10f,.08f), new Vector3(0,signCenter,0));
        AddMesh(root, inner, Colors.White, new Vector3(0,signCenter,.025f));
        foreach (var (text, line) in new[] { ("GIVE", 1), ("WAY", -1) })
            root.AddChild(new Label3D { Name = $"GiveWayLegend{text}", Text = text, FontSize = 32,
                PixelSize = .004f, Modulate = new Color(.16f,.16f,.15f),
                Position = new Vector3(0,signCenter + line*.13f,.045f),
                Billboard = BaseMaterial3D.BillboardModeEnum.Disabled,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
    }

    private static void BuildYieldTriangle(Node3D root, RouteSceneFeature feature)
    {
        var w = Math.Clamp((float)feature.WidthMeters,1.2f,4.0f); var len = Math.Clamp((float)feature.LengthMeters,1.5f,4.0f);
        var white = Material(new Color(.90f,.90f,.86f));
        var marking = RouteSceneTransformMath.YieldTriangle(w, len);
        AddStrip(root, "YieldLeftEdge", ProceduralRoad.ToGodot(marking.LeftBase), ProceduralRoad.ToGodot(marking.Apex), .12f, white);
        AddStrip(root, "YieldRightEdge", ProceduralRoad.ToGodot(marking.RightBase), ProceduralRoad.ToGodot(marking.Apex), .12f, white);
        AddStrip(root, "YieldBaseEdge", ProceduralRoad.ToGodot(marking.LeftBase), ProceduralRoad.ToGodot(marking.RightBase), .12f, white);
    }

    private static void BuildGiveWayLine(Node3D root, RouteSceneFeature feature)
    {
        AddMesh(root, new BoxMesh { Size = new Vector3(MathF.Max(3,(float)feature.LengthMeters),.025f,.16f) },
            new Color(.88f,.89f,.86f), Vector3.Zero);
    }

    private static void BuildYellowKerb(Node3D root, RouteSceneFeature feature)
    {
        var count = Math.Max(1,(int)MathF.Floor((float)feature.LengthMeters / 1.45f));
        for (var i = 0; i < count; i++)
            AddMesh(root, new BoxMesh { Size = new Vector3(.30f,.025f,.12f) }, new Color(.88f,.73f,.20f),
                new Vector3(0,.02f,-(i-count/2f)*1.45f));
    }

    private static void BuildLaneDash(Node3D root, RouteSceneFeature feature)
    {
        AddMesh(root, new BoxMesh { Size = new Vector3(MathF.Max(.08f,(float)feature.WidthMeters),.025f,
                MathF.Max(.4f,(float)feature.LengthMeters)) },
            ParseColour(feature.Colour,new Color(.90f,.90f,.86f)), Vector3.Zero);
    }

    private static void AddStrip(Node3D root, string name, Vector3 a, Vector3 b, float width, Material material)
    {
        var delta = b-a; var mesh = new BoxMesh { Size = new Vector3(width,.022f,delta.Length()) };
        var item = new MeshInstance3D { Name = name, Mesh = mesh, MaterialOverride = material,
            Position = (a+b)/2 };
        item.Rotation = new Vector3(0,MathF.Atan2(delta.X,delta.Z),0); root.AddChild(item);
    }

    private static Mesh TriangleMesh(float width, float height, Color color)
    {
        var mesh = new ArrayMesh();
        var a = new Vector3(-width/2,height/2,0); var b = new Vector3(width/2,height/2,0); var c = new Vector3(0,-height/2,0);
        var vertices = new[] { a,b,c, a,c,b };
        var arrays = new Godot.Collections.Array(); arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = vertices;
        arrays[(int)Mesh.ArrayType.Index] = new int[] {0,1,2,3,4,5};
        arrays[(int)Mesh.ArrayType.Normal] = new[] { Vector3.Back, Vector3.Back, Vector3.Back, Vector3.Forward, Vector3.Forward, Vector3.Forward };
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles,arrays);
        return mesh;
    }

    private static Transform3D MakeTransform(Vector3 position, Vector3 scale) =>
        new(new Basis(Vector3.Right*scale.X,Vector3.Up*scale.Y,Vector3.Back*scale.Z),position);

    private static void AddMultiBoxes(Node3D root, string name, IReadOnlyList<(Transform3D Transform, Color Color)> instances)
    {
        var mesh = new BoxMesh { Size = Vector3.One };
        var multi = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseColors = true, Mesh = mesh, InstanceCount = instances.Count };
        for (var i=0;i<instances.Count;i++) { multi.SetInstanceTransform(i,instances[i].Transform); multi.SetInstanceColor(i,instances[i].Color); }
        root.AddChild(new MultiMeshInstance3D { Name = name, Multimesh = multi,
            MaterialOverride = new StandardMaterial3D { AlbedoColor = Colors.White, VertexColorUseAsAlbedo = true, Roughness = .95f } });
    }

    private static void AddMesh(Node3D root, Mesh mesh, Color color, Vector3 position)
    {
        root.AddChild(new MeshInstance3D { Mesh = mesh, MaterialOverride = Material(color), Position = position });
    }
    private static StandardMaterial3D Material(Color color) => new() { AlbedoColor = color, Roughness = .92f,
        CullMode = BaseMaterial3D.CullModeEnum.Disabled };
    private static Color ParseColour(string? value, Color fallback) => BuildingVisualStyle.TryParseColour(value, out var c)
        ? new Color(c.R,c.G,c.B) : fallback;
    private static string SafeName(string value) => value.Replace(' ','_').Replace('/','_');
}
