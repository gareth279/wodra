using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

/// <summary>
/// Builds light garment shells from the production rider's existing weighted Human surface.
/// The body, skeleton, source GLB, and all non-garment nodes remain untouched.
/// </summary>
internal static class RiderGarmentMeshBuilder
{
    private const float MaskCut = .45f;
    private const float ShellCut = .01f;
    private const float JerseyOffset = .0030f;
    private const float ShortsOffset = .0020f;

    private readonly record struct PointKey(int X, int Y, int Z)
    {
        public static PointKey From(Vector3 point) => new(
            Mathf.RoundToInt(point.X * 100000f),
            Mathf.RoundToInt(point.Y * 100000f),
            Mathf.RoundToInt(point.Z * 100000f));
    }

    private sealed class VertexData
    {
        public Vector3 Position;
        public Vector3 Normal;
        public float Mask;
        public float JerseyMask;
        public float ShortsMask;
        public Color OutfitColor;
        public Vector3 SkeletonPosition;
        public int[] Bones = new int[4];
        public float[] Weights = new float[4];

        public static VertexData Blend(VertexData a, VertexData b, float t)
        {
            var combined = new Dictionary<int, float>();
            for (var i = 0; i < 4; i++)
            {
                AddInfluence(combined, a.Bones[i], a.Weights[i] * (1f - t));
                AddInfluence(combined, b.Bones[i], b.Weights[i] * t);
            }
            var selected = combined.Where(pair => pair.Value > 1e-6f)
                .OrderByDescending(pair => pair.Value).Take(4).ToArray();
            var total = selected.Sum(pair => pair.Value);
            var result = new VertexData
            {
                Position = a.Position.Lerp(b.Position, t),
                Normal = a.Normal.Lerp(b.Normal, t).Normalized(),
                Mask = Mathf.Lerp(a.Mask, b.Mask, t),
                JerseyMask = Mathf.Lerp(a.JerseyMask, b.JerseyMask, t),
                ShortsMask = Mathf.Lerp(a.ShortsMask, b.ShortsMask, t),
                OutfitColor = a.OutfitColor.Lerp(b.OutfitColor, t),
                SkeletonPosition = a.SkeletonPosition.Lerp(b.SkeletonPosition, t)
            };
            if (total > 1e-8f)
                for (var i = 0; i < selected.Length; i++)
                {
                    result.Bones[i] = selected[i].Key;
                    result.Weights[i] = selected[i].Value / total;
                }
            return result;
        }

        private static void AddInfluence(Dictionary<int, float> values, int bone, float weight)
        {
            if (bone < 0 || weight <= 0f) return;
            values[bone] = values.GetValueOrDefault(bone) + weight;
        }
    }

    private sealed class BoundarySegment
    {
        public required VertexData A;
        public required VertexData B;
        public required VertexData Inside;
        public PointKey KeyA => PointKey.From(A.Position);
        public PointKey KeyB => PointKey.From(B.Position);
    }

    private sealed class TriangleData
    {
        public required VertexData A;
        public required VertexData B;
        public required VertexData C;
    }

    private readonly record struct SleeveArm(Vector3 Shoulder, Vector3 Elbow,
        int UpperArm01, int UpperArm02, int LowerArm01, int LowerArm02, int Wrist);

    private sealed class MeshBuilder
    {
        private readonly List<Vector3> _vertices = new();
        private readonly List<Vector3> _normals = new();
        private readonly List<Color> _colors = new();
        private readonly List<int> _bones = new();
        private readonly List<float> _weights = new();

        public int TriangleCount => _vertices.Count / 3;

        public void AddTriangle(VertexData a, VertexData b, VertexData c, Color? color,
            float offset = 0f)
        {
            AddVertex(a, color, offset);
            AddVertex(b, color, offset);
            AddVertex(c, color, offset);
        }

        public void AddQuad(VertexData a, VertexData b, VertexData c, VertexData d,
            Color? color, float offset = 0f)
        {
            AddTriangle(a, b, c, color, offset);
            AddTriangle(a, c, d, color, offset);
        }

        private void AddVertex(VertexData vertex, Color? color, float offset)
        {
            var taperedOffset = offset * Mathf.SmoothStep(0f, .35f, Mathf.Clamp(vertex.Mask, 0f, 1f));
            _vertices.Add(vertex.Position + vertex.Normal * taperedOffset);
            _normals.Add(vertex.Normal);
            _colors.Add(color ?? vertex.OutfitColor);
            for (var i = 0; i < 4; i++)
            {
                _bones.Add(vertex.Bones[i]);
                _weights.Add(vertex.Weights[i]);
            }
        }

        public void Commit(ArrayMesh mesh, Color? defaultColor = null)
        {
            if (_vertices.Count == 0) return;
            var colors = defaultColor is { } color ? Enumerable.Repeat(color, _vertices.Count).ToArray() : _colors.ToArray();
            var arrays = new Godot.Collections.Array();
            for (var i = 0; i < (int)Mesh.ArrayType.Max; i++) arrays.Add(default);
            arrays[(int)Mesh.ArrayType.Vertex] = _vertices.ToArray();
            arrays[(int)Mesh.ArrayType.Normal] = _normals.ToArray();
            arrays[(int)Mesh.ArrayType.Color] = colors;
            arrays[(int)Mesh.ArrayType.Bones] = _bones.ToArray();
            arrays[(int)Mesh.ArrayType.Weights] = _weights.ToArray();
            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        }
    }

    public static void Attach(MeshInstance3D human, Skeleton3D skeleton, Vector3 bikeForwardInModel, Node modelRoot)
    {
        if (human.Mesh is null || human.Skin is null)
            throw new InvalidOperationException("Production Human mesh and Skin are required to fit rider garments.");
        if (human.Mesh.GetSurfaceCount() < 1)
            throw new InvalidOperationException("Production Human outfit surface is missing.");

        if (human.Mesh is not ArrayMesh sourceMesh)
            throw new InvalidOperationException("The production Human surface must be an ArrayMesh to preserve its imported rig while correcting the sleeve mask.");
        var arrays = sourceMesh.SurfaceGetArrays(0);
        var positions = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
        var normals = arrays[(int)Mesh.ArrayType.Normal].AsVector3Array();
        var colors = arrays[(int)Mesh.ArrayType.Color].AsColorArray();
        var bones = arrays[(int)Mesh.ArrayType.Bones].AsInt32Array();
        var weights = arrays[(int)Mesh.ArrayType.Weights].AsFloat32Array();
        var indices = arrays[(int)Mesh.ArrayType.Index].AsInt32Array();
        if (positions.Length < 1000 || normals.Length != positions.Length || colors.Length != positions.Length ||
            bones.Length != positions.Length * 4 || weights.Length != bones.Length)
            throw new InvalidOperationException($"Production Human outfit arrays are incomplete: vertices={positions.Length}, normals={normals.Length}, colors={colors.Length}, bones={bones.Length}, weights={weights.Length}.");

        var vertexData = new VertexData[positions.Length];
        for (var i = 0; i < positions.Length; i++)
        {
            var influences = new int[4];
            var factors = new float[4];
            for (var j = 0; j < 4; j++)
            {
                influences[j] = bones[i * 4 + j];
                factors[j] = weights[i * 4 + j];
            }
            vertexData[i] = new VertexData
            {
                Position = positions[i],
                Normal = normals[i].LengthSquared() > 1e-8f ? normals[i].Normalized() : Vector3.Up,
                OutfitColor = colors[i],
                JerseyMask = colors[i].R,
                ShortsMask = colors[i].G,
                Bones = influences,
                Weights = factors
            };
        }
        for (var i = 0; i < vertexData.Length; i++)
            vertexData[i].SkeletonPosition = SkinToSkeletonSpace(vertexData[i], skeleton, human.Skin);

        var neckIndex = skeleton.FindBone("neck01");
        if (neckIndex < 0) throw new InvalidOperationException("Production rider is missing neck01 for the fitted jersey collar.");
        var neckInSkeleton = skeleton.GetBoneGlobalPose(neckIndex).Origin;
        var skeletonToModel = TransformToAncestor(skeleton, modelRoot);
        var neckAxisSkeleton = (skeletonToModel.Basis.Inverse() * Vector3.Up).Normalized();
        if (neckAxisSkeleton.LengthSquared() < .5f)
            throw new InvalidOperationException("Production skeleton has no stable up axis for the fitted jersey neckline.");
        var neckCoverage = new float[vertexData.Length];
        for (var i = 0; i < vertexData.Length; i++)
        {
            neckCoverage[i] = NeckJerseyCoverage(vertexData[i].SkeletonPosition, neckInSkeleton, neckAxisSkeleton);
            vertexData[i].JerseyMask *= neckCoverage[i];
            vertexData[i].OutfitColor = new Color(vertexData[i].OutfitColor.R * neckCoverage[i],
                vertexData[i].OutfitColor.G, vertexData[i].OutfitColor.B, vertexData[i].OutfitColor.A);
        }

        var sleeveArms = new[] { BuildSleeveArm(skeleton, "L"), BuildSleeveArm(skeleton, "R") };
        var sleeveCoverage = new float[vertexData.Length];
        for (var i = 0; i < vertexData.Length; i++)
        {
            sleeveCoverage[i] = ShortSleeveCoverage(vertexData[i], sleeveArms);
            vertexData[i].JerseyMask *= sleeveCoverage[i];
            vertexData[i].OutfitColor = new Color(vertexData[i].OutfitColor.R * sleeveCoverage[i],
                vertexData[i].OutfitColor.G, vertexData[i].OutfitColor.B, vertexData[i].OutfitColor.A);
        }
        human.Mesh = CopyHumanWithSleeveMask(sourceMesh, sleeveCoverage, neckCoverage);

        var triangles = new List<TriangleData>(indices.Length > 0 ? indices.Length / 3 : positions.Length / 3);
        var sourceIndices = indices.Length > 0 ? indices : Enumerable.Range(0, positions.Length).ToArray();
        for (var offset = 0; offset + 2 < sourceIndices.Length; offset += 3)
        {
            var ia = sourceIndices[offset];
            var ib = sourceIndices[offset + 1];
            var ic = sourceIndices[offset + 2];
            if ((uint)ia >= positions.Length || (uint)ib >= positions.Length || (uint)ic >= positions.Length)
                throw new InvalidOperationException("Production Human outfit index references an invalid vertex.");
            triangles.Add(new TriangleData { A = vertexData[ia], B = vertexData[ib], C = vertexData[ic] });
        }

        var jerseyBuilder = new MeshBuilder();
        var collarBuilder = new MeshBuilder();
        var sleeveHemBuilder = new MeshBuilder();
        var shortsBuilder = new MeshBuilder();
        var gripperBuilder = new MeshBuilder();
        foreach (var triangle in triangles)
        {
            var jerseyPolygon = Clip(triangle, true, ShellCut);
            AddPolygon(jerseyBuilder, jerseyPolygon, null, JerseyOffset);
            var shortsPolygon = Clip(triangle, false, ShellCut);
            AddPolygon(shortsBuilder, shortsPolygon, null, ShortsOffset);
        }

        var shortsBoundaries = TraceLoops(CollectBoundarySegments(triangles, false));
        var shortsDiagnostics = string.Join("; ", shortsBoundaries.Select(loop =>
        {
            var points = loop.Select(segment => segment.A.Position).ToArray();
            var center = points.Aggregate(Vector3.Zero, (sum, point) => sum + point) / points.Length;
            var minY = points.Min(point => point.Y);
            var maxY = points.Max(point => point.Y);
            var minX = points.Min(point => point.X);
            var maxX = points.Max(point => point.X);
            return $"n={loop.Count},center={center},x={minX:F3}..{maxX:F3},y={minY:F3}..{maxY:F3}";
        }));
        GD.Print($"[RiderGarments] bib-short mask contours: {shortsDiagnostics}");
        var legGripperLoops = shortsBoundaries.Where(loop =>
        {
            var center = LoopCenterLocal(loop);
            return loop.Count >= 24 && Math.Abs(center.X) is > .06f and < .18f && center.Y is > .70f and < 1.05f;
        }).ToArray();
        if (legGripperLoops.Length != 2)
            throw new InvalidOperationException($"Expected two anatomical bib-short thigh hems for grippers; found {legGripperLoops.Length}: {shortsDiagnostics}.");
        foreach (var loop in legGripperLoops)
            AddHem(loop, gripperBuilder, .007f, ShortsOffset + .0005f);

        var neck02Index = skeleton.FindBone("neck02");
        var headIndex = skeleton.FindBone("head");
        var neck02InSkeleton = neck02Index >= 0 ? skeleton.GetBoneGlobalPose(neck02Index).Origin : Vector3.Zero;
        var headInSkeleton = headIndex >= 0 ? skeleton.GetBoneGlobalPose(headIndex).Origin : Vector3.Zero;
        if (neck02Index < 0)
            throw new InvalidOperationException("Production rider is missing neck02 to define the fitted collar axis.");
        // The torso garment's neck opening follows the rider's upright body frame.
        // Using the short neck01-to-neck02 bone segment as a plane normal tilted
        // the back half of the opening up the nape and made a 10 mm band read as
        // a long vertical tab in side view.
        var neckPlaneSegments = CollectPlaneBoundarySegments(triangles, neckInSkeleton, neckAxisSkeleton);
        var neckPlaneLoops = TraceLoops(neckPlaneSegments);
        var loopDiagnostics = string.Join("; ", neckPlaneLoops.Select(loop =>
        {
            var center = LoopCenterInSkeleton(loop, skeleton, human.Skin);
            var radius = LoopRadiusFromAxis(loop, skeleton, human.Skin, neckInSkeleton, neckAxisSkeleton);
            return $"n={loop.Count},center={center},residual={center.DistanceTo(neckInSkeleton):F3}m,radius={radius:F3}m";
        }));
        GD.Print($"[RiderGarments] neck01 plane contours: {loopDiagnostics}; neck01={neckInSkeleton}, neck02={neck02InSkeleton}, head={headInSkeleton}");
        var neckLoop = neckPlaneLoops.Where(loop => loop.Count >= 10)
            .OrderBy(loop => LoopRadiusFromAxis(loop, skeleton, human.Skin, neckInSkeleton, neckAxisSkeleton))
            .FirstOrDefault();
        if (neckLoop is null)
            throw new InvalidOperationException($"The production Human mesh did not produce a closed contour at the neck01 plane. segments={neckPlaneSegments.Count}, loops={neckPlaneLoops.Count}.");
        var neckDistance = LoopCenterInSkeleton(neckLoop, skeleton, human.Skin).DistanceTo(neckInSkeleton);
        var neckRadius = LoopRadiusFromAxis(neckLoop, skeleton, human.Skin, neckInSkeleton, neckAxisSkeleton);
        if (neckDistance > .06f || neckRadius > .13f)
            throw new InvalidOperationException($"The collar contour is not at the anatomical neck base: center residual={neckDistance:F3}m, radial radius={neckRadius:F3}m.");

        var humanToModel = TransformToAncestor(human, modelRoot);
        var neckOriginInMesh = humanToModel.AffineInverse() * (skeletonToModel * neckInSkeleton);
        var neckAxisInMesh = (humanToModel.Basis.Inverse() * skeletonToModel.Basis * neckAxisSkeleton).Normalized();
        AddCollar(neckLoop, collarBuilder, .010f, JerseyOffset, neckAxisInMesh);
        var localForward = humanToModel.Basis.Inverse() * bikeForwardInModel.Normalized();
        if (localForward.LengthSquared() < .5f)
            throw new InvalidOperationException("Could not transform the bicycle forward axis into the Human mesh frame.");
        localForward = localForward.Normalized();
        var jerseyBoundaries = TraceLoops(CollectBoundarySegments(triangles, true));
        var sleeveContours = jerseyBoundaries.Where(loop =>
        {
            var center = LoopCenterLocal(loop);
            return loop.Count >= 20 && Math.Abs(center.X) > .16f && center.Y is > 1.0f and < 1.5f;
        }).ToArray();
        var sleeveDiagnostics = string.Join("; ", jerseyBoundaries.Select(loop =>
        {
            var center = LoopCenterLocal(loop);
            return $"n={loop.Count},center={center}";
        }));
        GD.Print($"[RiderGarments] short-sleeve mask contours: {sleeveDiagnostics}; selected={sleeveContours.Length}");
        if (sleeveContours.Length != 2)
            throw new InvalidOperationException($"Expected two fitted upper-arm sleeve cuffs; found {sleeveContours.Length}: {sleeveDiagnostics}.");
        foreach (var loop in sleeveContours)
            AddHem(loop, sleeveHemBuilder, .005f, JerseyOffset + .0005f);
        var zipperBuilder = new MeshBuilder();
        AddZipper(neckLoop, triangles, localForward, zipperBuilder);

        var jerseyMesh = new ArrayMesh();
        jerseyBuilder.Commit(jerseyMesh);
        if (jerseyMesh.GetSurfaceCount() == 0)
            throw new InvalidOperationException("The jersey outfit mask generated no continuous fabric surface.");
        collarBuilder.Commit(jerseyMesh, new Color(1f, 0f, 0f, 1f));
        if (collarBuilder.TriangleCount == 0)
            throw new InvalidOperationException("The neck01 contour generated no fitted collar surface.");
        sleeveHemBuilder.Commit(jerseyMesh);
        if (sleeveHemBuilder.TriangleCount == 0)
            throw new InvalidOperationException("The two fitted short-sleeve cuffs generated no garment surface.");
        zipperBuilder.Commit(jerseyMesh, Colors.White);
        jerseyMesh.SurfaceSetName(0, "Jersey fabric shell");
        jerseyMesh.SurfaceSetName(1, "Anatomically attached 10mm collar");
        jerseyMesh.SurfaceSetName(2, "Fitted sleeve cuff hems");
        jerseyMesh.SurfaceSetName(3, "Center front zipper seam");

        var shortsMesh = new ArrayMesh();
        shortsBuilder.Commit(shortsMesh);
        if (shortsMesh.GetSurfaceCount() == 0)
            throw new InvalidOperationException("The shorts outfit mask generated no continuous fabric surface.");
        gripperBuilder.Commit(shortsMesh);
        if (gripperBuilder.TriangleCount == 0)
            throw new InvalidOperationException("The two fitted bib-short leg grippers generated no garment surface.");
        shortsMesh.SurfaceSetName(0, "Bib shorts fabric");
        shortsMesh.SurfaceSetName(1, "Fitted thigh grippers");

        AttachGarment(human, skeleton, "RiderJerseyShell", jerseyMesh);
        AttachGarment(human, skeleton, "RiderBibShortsShell", shortsMesh);

        var jerseySourceTriangles = jerseyBuilder.TriangleCount;
        var shortsSourceTriangles = shortsBuilder.TriangleCount;
        GD.Print($"[RiderGarments] generated jersey={jerseySourceTriangles} fabric triangles, collar={collarBuilder.TriangleCount} at 10mm, sleeve-hems={sleeveHemBuilder.TriangleCount}, zipTriangles={zipperBuilder.TriangleCount}; bib-shorts={shortsSourceTriangles} fabric triangles, grippers={gripperBuilder.TriangleCount}; collar residual={neckDistance:F3}m, radius={neckRadius:F3}m, mesh origin={neckOriginInMesh}, axis={neckAxisInMesh}; bikeForwardLocal={localForward}.");
    }

    private static List<VertexData> Clip(TriangleData triangle, bool jersey, float threshold)
    {
        var input = new[] { triangle.A, triangle.B, triangle.C };
        var polygon = new List<VertexData>(4);
        for (var i = 0; i < 3; i++)
        {
            var current = input[i];
            var next = input[(i + 1) % 3];
            current.Mask = jersey ? current.JerseyMask : current.ShortsMask;
            next.Mask = jersey ? next.JerseyMask : next.ShortsMask;
            var currentInside = current.Mask >= threshold;
            var nextInside = next.Mask >= threshold;
            if (currentInside && nextInside) polygon.Add(next);
            else if (currentInside && !nextInside) polygon.Add(InterpolateMask(current, next, threshold));
            else if (!currentInside && nextInside)
            {
                polygon.Add(InterpolateMask(current, next, threshold));
                polygon.Add(next);
            }
        }
        return polygon;
    }

    private static SleeveArm BuildSleeveArm(Skeleton3D skeleton, string side)
    {
        var upper01 = skeleton.FindBone($"upperarm01.{side}");
        var upper02 = skeleton.FindBone($"upperarm02.{side}");
        var lower01 = skeleton.FindBone($"lowerarm01.{side}");
        var lower02 = skeleton.FindBone($"lowerarm02.{side}");
        var wrist = skeleton.FindBone($"wrist.{side}");
        if (upper01 < 0 || upper02 < 0 || lower01 < 0 || lower02 < 0 || wrist < 0)
            throw new InvalidOperationException($"The imported {side} arm chain is incomplete; cannot form a fitted short sleeve.");
        return new SleeveArm(skeleton.GetBoneGlobalPose(upper01).Origin,
            skeleton.GetBoneGlobalPose(lower01).Origin, upper01, upper02, lower01, lower02, wrist);
    }

    private static float ShortSleeveCoverage(VertexData vertex, IReadOnlyList<SleeveArm> arms)
    {
        var bestCoverage = 1f;
        var bestRadial = float.PositiveInfinity;
        foreach (var arm in arms)
        {
            var upperWeight = vertex.Weights.Where((_, index) =>
                    vertex.Bones[index] == arm.UpperArm01 || vertex.Bones[index] == arm.UpperArm02)
                .Sum();
            var forearmWeight = vertex.Weights.Where((_, index) =>
                    vertex.Bones[index] == arm.LowerArm01 || vertex.Bones[index] == arm.LowerArm02 ||
                    vertex.Bones[index] == arm.Wrist)
                .Sum();
            if (upperWeight + forearmWeight < .08f) continue;
            if (forearmWeight > .24f)
            {
                bestCoverage = 0f;
                bestRadial = 0f;
                continue;
            }

            var segment = arm.Elbow - arm.Shoulder;
            var lengthSquared = segment.LengthSquared();
            if (lengthSquared < 1e-6f) continue;
            var along = (vertex.SkeletonPosition - arm.Shoulder).Dot(segment) / lengthSquared;
            var closest = arm.Shoulder + segment * Mathf.Clamp(along, 0f, 1f);
            var radial = vertex.SkeletonPosition.DistanceTo(closest);
            if (radial > .17f || radial >= bestRadial) continue;
            bestRadial = radial;
            // End the sleeve at 62% of the shoulder-to-elbow length, a typical
            // upper-arm road-jersey cut. The short fade gives the fitted cuff a
            // clean boundary without an abrupt color-mask step.
            var normalized = Mathf.Clamp((along - .56f) / .08f, 0f, 1f);
            bestCoverage = 1f - normalized * normalized * (3f - 2f * normalized);
        }
        return bestCoverage;
    }

    private static float NeckJerseyCoverage(Vector3 point, Vector3 neckOrigin, Vector3 neckAxis)
    {
        var relative = point - neckOrigin;
        var height = relative.Dot(neckAxis);
        var radial = (relative - neckAxis * height).Length();
        if (radial > .105f) return 1f;
        // End the Human's painted torso mask at the actual collar base. The
        // generated collar shell supplies the short fabric rise above it.
        return 1f - Mathf.SmoothStep(-.004f, .002f, height);
    }

    private static ArrayMesh CopyHumanWithSleeveMask(ArrayMesh source, float[] sleeveCoverage, float[] neckCoverage)
    {
        var copy = new ArrayMesh { ResourceName = $"{source.ResourceName} (short sleeve mask)" };
        for (var surface = 0; surface < source.GetSurfaceCount(); surface++)
        {
            var surfaceArrays = source.SurfaceGetArrays(surface);
            if (surface == 0)
            {
                var colors = surfaceArrays[(int)Mesh.ArrayType.Color].AsColorArray();
                if (colors.Length != sleeveCoverage.Length || colors.Length != neckCoverage.Length)
                    throw new InvalidOperationException("Human clothing-color mask no longer matches its skinned surface.");
                for (var i = 0; i < colors.Length; i++)
                    colors[i] = new Color(colors[i].R * sleeveCoverage[i] * neckCoverage[i], colors[i].G,
                        colors[i].B, colors[i].A);
                surfaceArrays[(int)Mesh.ArrayType.Color] = colors;
            }
            copy.AddSurfaceFromArrays(source.SurfaceGetPrimitiveType(surface), surfaceArrays);
            copy.SurfaceSetName(surface, source.SurfaceGetName(surface));
            if (source.SurfaceGetMaterial(surface) is { } material)
                copy.SurfaceSetMaterial(surface, material);
        }
        return copy;
    }

    private static VertexData InterpolateMask(VertexData a, VertexData b, float threshold)
    {
        var denominator = b.Mask - a.Mask;
        var t = Math.Abs(denominator) < 1e-8f ? .5f : Mathf.Clamp((threshold - a.Mask) / denominator, 0f, 1f);
        var result = VertexData.Blend(a, b, t);
        result.Mask = threshold;
        return result;
    }

    private static void AddPolygon(MeshBuilder builder, IReadOnlyList<VertexData> polygon, Color? color, float offset)
    {
        if (polygon.Count < 3) return;
        for (var i = 1; i < polygon.Count - 1; i++)
            builder.AddTriangle(polygon[0], polygon[i], polygon[i + 1], color, offset);
    }

    private static List<BoundarySegment> CollectBoundarySegments(List<TriangleData> triangles, bool jersey)
    {
        var result = new List<BoundarySegment>();
        foreach (var triangle in triangles)
        {
            var source = new[] { triangle.A, triangle.B, triangle.C };
            foreach (var vertex in source) vertex.Mask = jersey ? vertex.JerseyMask : vertex.ShortsMask;
            var intersections = new List<VertexData>(2);
            var inside = new List<VertexData>(3);
            for (var i = 0; i < 3; i++)
            {
                var a = source[i];
                var b = source[(i + 1) % 3];
                var aInside = a.Mask >= MaskCut;
                var bInside = b.Mask >= MaskCut;
                if (aInside) inside.Add(a);
                if (aInside != bInside) intersections.Add(InterpolateMask(a, b, MaskCut));
            }
            if (intersections.Count != 2 || inside.Count == 0) continue;
            var center = inside.Aggregate(Vector3.Zero, (sum, vertex) => sum + vertex.Position) / inside.Count;
            var interior = inside[0];
            for (var i = 1; i < inside.Count; i++)
                interior = VertexData.Blend(interior, inside[i], 1f / (i + 1f));
            interior.Position = center;
            interior.Mask = MaskCut + .1f;
            result.Add(new BoundarySegment { A = intersections[0], B = intersections[1], Inside = interior });
        }
        return result;
    }

    private static List<BoundarySegment> CollectPlaneBoundarySegments(List<TriangleData> triangles,
        Vector3 planeOrigin, Vector3 planeNormal)
    {
        var segments = new List<BoundarySegment>();
        foreach (var triangle in triangles)
        {
            var vertices = new[] { triangle.A, triangle.B, triangle.C };
            var intersections = new List<VertexData>(2);
            for (var i = 0; i < 3; i++)
            {
                var a = vertices[i];
                var b = vertices[(i + 1) % 3];
                var distanceA = planeNormal.Dot(a.SkeletonPosition - planeOrigin);
                var distanceB = planeNormal.Dot(b.SkeletonPosition - planeOrigin);
                if ((distanceA < -1e-6f && distanceB > 1e-6f) ||
                    (distanceA > 1e-6f && distanceB < -1e-6f))
                {
                    var t = distanceA / (distanceA - distanceB);
                    var point = VertexData.Blend(a, b, t);
                    point.SkeletonPosition = a.SkeletonPosition.Lerp(b.SkeletonPosition, t);
                    point.Mask = 1f;
                    intersections.Add(point);
                }
            }
            if (intersections.Count != 2) continue;
            var inside = VertexData.Blend(triangle.A, triangle.B, .5f);
            inside = VertexData.Blend(inside, triangle.C, 1f / 3f);
            inside.Mask = 1f;
            segments.Add(new BoundarySegment { A = intersections[0], B = intersections[1], Inside = inside });
        }
        return segments;
    }

    private static List<List<BoundarySegment>> TraceLoops(List<BoundarySegment> segments)
    {
        var adjacency = new Dictionary<PointKey, List<int>>();
        for (var i = 0; i < segments.Count; i++)
        {
            AddAdjacency(adjacency, segments[i].KeyA, i);
            AddAdjacency(adjacency, segments[i].KeyB, i);
        }
        var remaining = new HashSet<int>(Enumerable.Range(0, segments.Count));
        var loops = new List<List<BoundarySegment>>();
        while (remaining.Count > 0)
        {
            var firstIndex = remaining.First();
            var first = segments[firstIndex];
            var start = first.KeyA;
            var currentKey = first.KeyB;
            var ordered = new List<BoundarySegment> { first };
            remaining.Remove(firstIndex);
            var guard = 0;
            while (currentKey != start && guard++ <= segments.Count)
            {
                if (!adjacency.TryGetValue(currentKey, out var edges)) break;
                var nextIndex = edges.FirstOrDefault(remaining.Contains, -1);
                if (nextIndex < 0) break;
                remaining.Remove(nextIndex);
                var next = segments[nextIndex];
                if (next.KeyB == currentKey)
                    (next.A, next.B) = (next.B, next.A);
                ordered.Add(next);
                currentKey = next.KeyB;
            }
            if (currentKey == start && ordered.Count >= 6) loops.Add(ordered);
        }
        return loops;
    }

    private static void AddAdjacency(Dictionary<PointKey, List<int>> adjacency, PointKey key, int edge)
    {
        if (!adjacency.TryGetValue(key, out var list)) adjacency[key] = list = new List<int>();
        list.Add(edge);
    }

    private static void AddCollar(List<BoundarySegment> loop, MeshBuilder builder, float height,
        float offset, Vector3 neckAxisInMesh)
    {
        foreach (var segment in loop)
        {
            var aBase = WithFabricMask(segment.A);
            var bBase = WithFabricMask(segment.B);
            var aTop = OffsetAlongAxis(aBase, neckAxisInMesh, height);
            var bTop = OffsetAlongAxis(bBase, neckAxisInMesh, height);
            builder.AddQuad(aBase, bBase, bTop, aTop, new Color(1f, 0f, 0f, 1f), offset);
        }
    }

    private static VertexData WithFabricMask(VertexData vertex)
    {
        var result = VertexData.Blend(vertex, vertex, 0f);
        result.Mask = 1f;
        return result;
    }

    private static VertexData OffsetAlongAxis(VertexData vertex, Vector3 axis, float amount)
    {
        var result = VertexData.Blend(vertex, vertex, 0f);
        result.Position += axis * amount;
        result.Mask = 1f;
        return result;
    }

    private static Vector3 LoopCenterLocal(List<BoundarySegment> loop)
    {
        var points = loop.Select(segment => segment.A.Position).ToArray();
        return points.Aggregate(Vector3.Zero, (sum, point) => sum + point) / points.Length;
    }

    private static HashSet<PointKey> LoopKeys(List<BoundarySegment> loop) =>
        loop.SelectMany(segment => new[] { segment.KeyA, segment.KeyB }).ToHashSet();

    private static Vector3 LoopCenterInSkeleton(List<BoundarySegment> loop, Skeleton3D skeleton, Skin skin)
    {
        var points = loop.Select(segment => segment.A.SkeletonPosition).ToArray();
        return points.Aggregate(Vector3.Zero, (sum, point) => sum + point) / points.Length;
    }

    private static float LoopRadiusFromAxis(List<BoundarySegment> loop, Skeleton3D skeleton, Skin skin,
        Vector3 axisOrigin, Vector3 axisDirection)
    {
        var squaredRadius = loop.Select(segment =>
        {
            var relative = segment.A.SkeletonPosition - axisOrigin;
            var radial = relative - axisDirection * relative.Dot(axisDirection);
            return radial.LengthSquared();
        }).ToArray();
        return Mathf.Sqrt(squaredRadius.Average());
    }

    private static Vector3 SkinToSkeletonSpace(VertexData vertex, Skeleton3D skeleton, Skin skin)
    {
        var result = Vector3.Zero;
        for (var i = 0; i < 4; i++)
        {
            if (vertex.Weights[i] <= 0f) continue;
            var bone = vertex.Bones[i];
            if ((uint)bone >= (uint)skeleton.GetBoneCount()) continue;
            var transform = skeleton.GetBoneGlobalPose(bone) * skin.GetBindPose(bone);
            result += transform * vertex.Position * vertex.Weights[i];
        }
        return result;
    }

    private static Transform3D TransformToAncestor(Node3D node, Node ancestor)
    {
        var transform = Transform3D.Identity;
        Node? current = node;
        while (current != ancestor)
        {
            if (current is not Node3D current3D || current.GetParent() is null)
                throw new InvalidOperationException($"{node.Name} is not beneath imported model root {ancestor.Name}.");
            transform = current3D.Transform * transform;
            current = current.GetParent();
        }
        return transform;
    }

    private static void AddHem(List<BoundarySegment> loop, MeshBuilder builder, float width, float shellOffset)
    {
        var inner = new VertexData[loop.Count];
        for (var i = 0; i < loop.Count; i++)
        {
            var previous = loop[(i + loop.Count - 1) % loop.Count];
            var current = loop[i];
            var edge = WithFabricMask(current.A);
            var previousInward = previous.Inside.Position - previous.B.Position;
            var currentInward = current.Inside.Position - current.A.Position;
            var direction = previousInward + currentInward;
            var normal = (previous.B.Normal + current.A.Normal).Normalized();
            direction -= normal * direction.Dot(normal);
            if (direction.LengthSquared() < 1e-8f)
                direction = currentInward - normal * currentInward.Dot(normal);
            if (direction.LengthSquared() < 1e-8f)
                throw new InvalidOperationException("A garment hem contour has no inward tangent direction.");
            edge.Position += direction.Normalized() * width;
            edge.Normal = current.A.Normal;
            inner[i] = edge;
        }

        for (var i = 0; i < loop.Count; i++)
        {
            var next = (i + 1) % loop.Count;
            builder.AddQuad(WithFabricMask(loop[i].A), WithFabricMask(loop[i].B),
                inner[next], inner[i], null, shellOffset + .0005f);
        }
    }

    private static void AddZipper(List<BoundarySegment> neckLoop, List<TriangleData> triangles,
        Vector3 localForward, MeshBuilder zipper)
    {
        var neckCenter = LoopCenterLocal(neckLoop);
        var jerseyPoints = triangles.SelectMany(triangle => new[] { triangle.A, triangle.B, triangle.C })
            .Where(vertex => vertex.JerseyMask >= .8f && Math.Abs(vertex.Position.X - neckCenter.X) < .09f).ToArray();
        if (jerseyPoints.Length == 0) return;
        var lowY = jerseyPoints.Min(vertex => vertex.Position.Y) + .025f;
        var highY = neckCenter.Y - .004f;
        if (highY - lowY < .12f) return;
        var side = Vector3.Up.Cross(localForward).Normalized();
        if (side.LengthSquared() < .5f) return;

        var samples = new List<VertexData>();
        const int count = 40;
        for (var i = 0; i <= count; i++)
        {
            var y = Mathf.Lerp(highY, lowY, (float)i / count);
            var target = new Vector3(neckCenter.X, y, neckCenter.Z);
            var projected = ClosestFrontSurface(target, triangles, localForward);
            if (projected is null) continue;
            samples.Add(projected);
        }
        for (var i = 0; i + 1 < samples.Count; i++)
        {
            var a = samples[i]; var b = samples[i + 1];
            var aLeft = OffsetLateral(a, side, -.003f);
            var aRight = OffsetLateral(a, side, .003f);
            var bRight = OffsetLateral(b, side, .003f);
            var bLeft = OffsetLateral(b, side, -.003f);
            zipper.AddQuad(aLeft, bLeft, bRight, aRight, Colors.White, JerseyOffset + .001f);
        }
    }

    private static VertexData OffsetLateral(VertexData vertex, Vector3 lateral, float amount)
    {
        var result = VertexData.Blend(vertex, vertex, 0f);
        result.Position += lateral * amount;
        return result;
    }

    private static VertexData? ClosestFrontSurface(Vector3 target, List<TriangleData> triangles,
        Vector3 localForward)
    {
        VertexData? best = null;
        var bestScore = float.PositiveInfinity;
        foreach (var triangle in triangles)
        {
            if (triangle.A.JerseyMask < .4f && triangle.B.JerseyMask < .4f && triangle.C.JerseyMask < .4f) continue;
            var geometricNormal = (triangle.B.Position - triangle.A.Position).Cross(triangle.C.Position - triangle.A.Position).Normalized();
            if (geometricNormal.Dot(localForward) < .18f) continue;
            var projectionTarget = target;
            projectionTarget.Z = (triangle.A.Position.Z + triangle.B.Position.Z + triangle.C.Position.Z) / 3f;
            if (!TryProjectToTriangle(projectionTarget, triangle, out var projected, out var bary)) continue;
            var score = new Vector2(projected.X - target.X, projected.Y - target.Y).LengthSquared();
            if (score >= bestScore) continue;
            bestScore = score;
            best = BlendTriangle(triangle, bary);
            best.Position = projected;
        best.Mask = best.JerseyMask;
        }
        return bestScore <= .04f ? best : null;
    }

    private static VertexData BlendTriangle(TriangleData triangle, Vector3 bary)
    {
        var a = VertexData.Blend(triangle.A, triangle.B, bary.Y / Math.Max(1e-8f, bary.X + bary.Y));
        var result = VertexData.Blend(a, triangle.C, bary.Z);
        result.JerseyMask = bary.X * triangle.A.JerseyMask + bary.Y * triangle.B.JerseyMask + bary.Z * triangle.C.JerseyMask;
        result.ShortsMask = bary.X * triangle.A.ShortsMask + bary.Y * triangle.B.ShortsMask + bary.Z * triangle.C.ShortsMask;
        return result;
    }

    private static bool TryProjectToTriangle(Vector3 point, TriangleData triangle,
        out Vector3 projected, out Vector3 bary)
    {
        var a = triangle.A.Position;
        var b = triangle.B.Position;
        var c = triangle.C.Position;
        var normal = (b - a).Cross(c - a);
        if (normal.LengthSquared() < 1e-10f)
        {
            projected = default; bary = default; return false;
        }
        normal = normal.Normalized();
        projected = point - normal * normal.Dot(point - a);
        var v0 = b - a; var v1 = c - a; var v2 = projected - a;
        var d00 = v0.Dot(v0); var d01 = v0.Dot(v1); var d11 = v1.Dot(v1);
        var d20 = v2.Dot(v0); var d21 = v2.Dot(v1);
        var denominator = d00 * d11 - d01 * d01;
        if (Math.Abs(denominator) < 1e-10f) { bary = default; return false; }
        var v = (d11 * d20 - d01 * d21) / denominator;
        var w = (d00 * d21 - d01 * d20) / denominator;
        var u = 1f - v - w;
        bary = new Vector3(u, v, w);
        return u >= -.001f && v >= -.001f && w >= -.001f;
    }

    private static void AttachGarment(MeshInstance3D human, Skeleton3D skeleton,
        string name, ArrayMesh mesh)
    {
        var garment = new MeshInstance3D
        {
            Name = name,
            Mesh = mesh,
            Skin = human.Skin,
            Transform = human.Transform
        };
        var parent = human.GetParent() ?? throw new InvalidOperationException("Production Human mesh has no imported parent.");
        parent.AddChild(garment);
        garment.Skeleton = garment.GetPathTo(skeleton);
    }
}
