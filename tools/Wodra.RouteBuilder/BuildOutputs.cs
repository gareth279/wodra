using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wodra.RouteBuilder;

public static class BuildOutputs
{
    private sealed record PreviewAnchor(string Name, double SourceDistanceMeters, GeoPoint Original,
        GeoPoint Projected, double SnapDistanceMeters, long WayId, string Highway, string? RoadName,
        string? RoadRef, double BearingDegrees, double DirectionAlignmentDegrees);
    private sealed record PreviewWarning(int Index, string From, string To, IReadOnlyList<string> Reasons);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static async Task WriteAsync(string outputDirectory, RouteAuthoringConfig config, string pbfPath,
        GraphBuildStats graphStats, RouteBuildResult result, double routeSolveSeconds,
        LandmarkExtractionResult? landmarks = null, BuildingExtractionResult? buildings = null,
        VegetationExtractionResult? vegetation = null, StreetFurnitureExtractionResult? streetFurniture = null,
        BackgroundStreetExtractionResult? backgroundStreets = null, bool bundleBackgroundStreets = true)
    {
        Directory.CreateDirectory(outputDirectory);
        var generatedAt = DateTimeOffset.UtcNow;
        var outputTimer = System.Diagnostics.Stopwatch.StartNew();
        var stem = config.Id;
        var geoJsonPath = Path.Combine(outputDirectory, $"{stem}.geojson");
        var routePointsPath = Path.Combine(outputDirectory, $"{stem}.route-points.json");
        var reportPath = Path.Combine(outputDirectory, $"{stem}.report.json");
        var previewPath = Path.Combine(outputDirectory, $"{stem}.preview.html");
        var landmarksPath = Path.Combine(outputDirectory, $"{stem}.landmarks.json");
        var buildingsPath = Path.Combine(outputDirectory, $"{stem}.buildings.json");
        var vegetationPath = Path.Combine(outputDirectory, $"{stem}.vegetation.json");
        var furniturePath = Path.Combine(outputDirectory, $"{stem}.street-furniture.json");
        var streetsPath = Path.Combine(outputDirectory, $"{stem}.streets.json");

        await File.WriteAllTextAsync(geoJsonPath, SerializeGeoJson(config, result, generatedAt));
        var pointsOutput = new
        {
            id = config.Id,
            name = config.Name,
            source = "OpenStreetMap",
            isValid = result.IsValid,
            errors = result.Errors,
            referenceDistanceMeters = config.ReferenceDistanceMeters,
            actualOsmDistanceMeters = result.RawDistanceMeters,
            actualToReferenceRatio = config.ReferenceDistanceMeters is { } reference && reference > 0
                ? result.RawDistanceMeters / reference : (double?)null,
            candidatePointCount = result.RoutePoints.Count,
            // Failed builds overwrite stale route-point data but never publish a scaled candidate.
            points = result.IsValid ? result.RoutePoints : []
        };
        await File.WriteAllTextAsync(routePointsPath, JsonSerializer.Serialize(pointsOutput, JsonOptions));
        outputTimer.Stop();

        var timings = new BuildTimings(graphStats.NodeScanSeconds, graphStats.WayScanSeconds,
            graphStats.GraphBuildSeconds, routeSolveSeconds, outputTimer.Elapsed.TotalSeconds,
            graphStats.CacheLoadSeconds, graphStats.CacheSaveSeconds, graphStats.CacheHit,
            graphStats.CacheBuildSeconds);
        var report = new BuildReport
        {
            Id = config.Id,
            Name = config.Name,
            SourcePbfFilename = Path.GetFileName(pbfPath),
            RoutingMode = config.RoutingMode,
            PreferredRefs = config.PreferredRefs,
            ExcludedClosureEdges = config.ExcludedClosureEdges,
            BoundingBox = config.Bbox,
            Profile = config.Profile,
            RouteCorridorMeters = config.RouteCorridorMeters,
            DetourWarningRatio = config.DetourWarningRatio,
            DetourFailureRatio = config.DetourFailureRatio,
            MaxOutputSpacingMeters = config.MaxOutputSpacingMeters,
            RetainedOsmNodeCount = graphStats.RetainedNodeCount,
            EligibleWayCount = graphStats.EligibleWayCount,
            GraphNodeCount = graphStats.GraphNodeCount,
            GraphEdgeCount = graphStats.GraphEdgeCount,
            Anchors = result.Snaps,
            Legs = result.Legs,
            RouteRefSummary = result.RouteRefSummary,
            RoadSegments = result.RoadSegments ?? [],
            ActualOsmDistanceMeters = result.RawDistanceMeters,
            ReferenceDistanceMeters = config.ReferenceDistanceMeters,
            ReferenceDifferenceMeters = config.ReferenceDistanceMeters is { } reportReference
                ? result.RawDistanceMeters - reportReference : null,
            ActualToReferenceRatio = config.ReferenceDistanceMeters is { } referenceLength && referenceLength > 0
                ? result.RawDistanceMeters / referenceLength : null,
            FinalPointCount = result.Centreline.Count,
            SourceOsmNodeCount = result.SourceOsmNodeCount,
            MaximumAdjacentPointDistanceMeters = result.MaximumAdjacentDistanceMeters,
            MaximumSourceOsmNodeSpacingMeters = result.MaximumSourceNodeSpacingMeters,
            MaximumCorridorDeviationMeters = result.MaximumCorridorDeviationMeters,
            IsValid = result.IsValid,
            Errors = result.Errors,
            Warnings = result.Warnings,
            NearbyLandmarkCandidatesConsidered = landmarks?.NearbyCandidatesConsidered ?? 0,
            SelectedLandmarkCount = landmarks?.Landmarks.Count ?? 0,
            Buildings = buildings is null ? null : ToBuildingReport(buildings, pbfPath, result.RawDistanceMeters, result.RoutePoints[0]),
            Vegetation = vegetation is null ? null : ToVegetationReport(vegetation, pbfPath, result.RawDistanceMeters),
            StreetFurniture = streetFurniture is null ? null : ToStreetFurnitureReport(streetFurniture, pbfPath, result.RawDistanceMeters),
            Timings = timings,
            GeneratedAtUtc = generatedAt
        };
        await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, JsonOptions));
        if (result.IsValid && landmarks is not null)
        {
            var landmarkData = new
            {
                id = config.Id, routeName = config.Name, searchRadiusMeters = LandmarkExtractor.DefaultSearchRadiusMeters,
                nearbyCandidatesConsidered = landmarks.NearbyCandidatesConsidered,
                deduplicatedCandidateCount = landmarks.DeduplicatedCandidateCount,
                selectedCount = landmarks.Landmarks.Count, source = "OpenStreetMap",
                attribution = "© OpenStreetMap contributors, ODbL 1.0",
                landmarks = landmarks.Landmarks
            };
            await File.WriteAllTextAsync(landmarksPath, JsonSerializer.Serialize(landmarkData, JsonOptions));
        }
        if (result.IsValid && buildings is not null)
        {
            var buildingData = new
            {
                id = config.Id, routeName = config.Name, source = "OpenStreetMap",
                sourcePbfFilename = Path.GetFileName(pbfPath), attribution = "© OpenStreetMap contributors, ODbL 1.0",
                searchRadiusMeters = config.Buildings?.SearchRadiusMeters ?? BuildingExtractor.DefaultSearchRadiusMeters,
                simplificationToleranceMeters = config.Buildings?.SimplificationToleranceMeters ?? BuildingExtractor.DefaultSimplificationToleranceMeters,
                statistics = ToBuildingReport(buildings, pbfPath, result.RawDistanceMeters, result.RoutePoints[0]), buildings = buildings.Buildings
            };
            await File.WriteAllTextAsync(buildingsPath, JsonSerializer.Serialize(buildingData, JsonOptions));
        }
        if (result.IsValid && vegetation is not null)
        {
            var vegetationData = new
            {
                id = config.Id, routeName = config.Name, source = "OpenStreetMap",
                sourcePbfFilename = Path.GetFileName(pbfPath), attribution = "© OpenStreetMap contributors, ODbL 1.0",
                searchRadiusMeters = config.Vegetation?.SearchRadiusMeters ?? VegetationExtractor.DefaultSearchRadiusMeters,
                statistics = ToVegetationReport(vegetation, pbfPath, result.RawDistanceMeters),
                areas = vegetation.Areas.Select(area => new
                {
                    osmType = area.OsmType, osmId = area.OsmId, name = area.Name, category = area.Category,
                    baseElevationMeters = area.BaseElevationMeters, elevationVariationMeters = area.ElevationVariationMeters,
                    renderGround = area.RenderGround, areaSquareMeters = area.AreaSquareMeters,
                    distanceFromRouteMeters = area.DistanceFromRouteMeters, nearestRouteDistanceMeters = area.NearestRouteDistanceMeters,
                    generatedTreeCount = area.GeneratedTreeCount, originalVertexCount = area.OriginalVertexCount,
                    elevationSampleCount = area.ElevationSampleCount, polygon = area.Polygon
                }),
                trees = vegetation.Trees.Select(tree => new
                {
                    source = tree.Source, osmId = tree.OsmId, latitude = tree.Latitude, longitude = tree.Longitude,
                    elevationMeters = tree.ElevationMeters, treeType = tree.TreeType, heightMeters = tree.HeightMeters,
                    canopyRadiusMeters = tree.CanopyRadiusMeters, nearestRouteDistanceMeters = tree.NearestRouteDistanceMeters
                })
            };
            await File.WriteAllTextAsync(vegetationPath, JsonSerializer.Serialize(vegetationData, JsonOptions));
        }
        if (result.IsValid && streetFurniture is not null)
        {
            var furnitureData = new
            {
                id = config.Id, routeName = config.Name, source = "OpenStreetMap",
                sourcePbfFilename = Path.GetFileName(pbfPath), attribution = "© OpenStreetMap contributors, ODbL 1.0",
                searchRadiusMeters = config.StreetFurniture?.SearchRadiusMeters ?? StreetFurnitureExtractor.DefaultSearchRadiusMeters,
                statistics = ToStreetFurnitureReport(streetFurniture, pbfPath, result.RawDistanceMeters),
                items = streetFurniture.Items
            };
            await File.WriteAllTextAsync(furniturePath, JsonSerializer.Serialize(furnitureData, JsonOptions));
        }
        if (result.IsValid && backgroundStreets is not null)
        {
            var streetData = new { id=config.Id, routeName=config.Name, source="OpenStreetMap", sourcePbfFilename=Path.GetFileName(pbfPath),
                attribution="© OpenStreetMap contributors, ODbL 1.0", sampleSpacingMeters=config.BackgroundStreets?.SampleSpacingMeters,
                statistics=new { waysConsidered=backgroundStreets.WaysConsidered, waysIncluded=backgroundStreets.WaysIncluded,
                    highwayClassCounts=backgroundStreets.HighwayClassCounts, widthSourceCounts=backgroundStreets.WidthSourceCounts,
                    namedStreetCount=backgroundStreets.NamedStreetCount,totalLengthMeters=backgroundStreets.TotalLengthMeters,
                    suppressedOverlapMeters=backgroundStreets.SuppressedOverlapMeters,runtimeSampleCount=backgroundStreets.RuntimeSampleCount },
                streets=backgroundStreets.Streets };
            var streetJson=JsonSerializer.Serialize(streetData,JsonOptions);
            await File.WriteAllTextAsync(streetsPath,streetJson);
            if (bundleBackgroundStreets)
            {
                var bundlePath=Path.GetFullPath(Path.Combine("src","IndoorCycling.Game","Data","Routes",$"{stem}.streets.json"));
                Directory.CreateDirectory(Path.GetDirectoryName(bundlePath)!); await File.WriteAllTextAsync(bundlePath,streetJson);
            }
        }
        if (result.IsValid)
            await File.WriteAllTextAsync(previewPath, SerializePreviewHtml(config, result, landmarks, buildings, vegetation, streetFurniture, backgroundStreets));
        else if (File.Exists(previewPath))
            File.Delete(previewPath);
        if (!result.IsValid && File.Exists(landmarksPath))
            File.Delete(landmarksPath);
        if (!result.IsValid && File.Exists(buildingsPath)) File.Delete(buildingsPath);
        if (!result.IsValid && File.Exists(vegetationPath)) File.Delete(vegetationPath);
        if (!result.IsValid && File.Exists(furniturePath)) File.Delete(furniturePath);
        Console.WriteLine($"GeoJSON: {Path.GetFullPath(geoJsonPath)}{(result.IsValid ? "" : " (diagnostic candidate; isValid=false)")}");
        Console.WriteLine($"Route points: {Path.GetFullPath(routePointsPath)}{(result.IsValid ? "" : " (invalid build; points are empty)")}");
        Console.WriteLine($"Report: {Path.GetFullPath(reportPath)}");
        if (result.IsValid) Console.WriteLine($"Review preview: {Path.GetFullPath(previewPath)}");
        if (result.IsValid && landmarks is not null)
            Console.WriteLine($"Landmarks: {Path.GetFullPath(landmarksPath)} ({landmarks.Landmarks.Count} selected from {landmarks.NearbyCandidatesConsidered} nearby candidates)");
        if (result.IsValid && buildings is not null)
            Console.WriteLine($"Buildings: {Path.GetFullPath(buildingsPath)} ({buildings.SelectedCount} selected from {buildings.NearbyCandidates} nearby; ways {buildings.OrdinaryWays}, relations {buildings.MultipolygonRelationsIncluded}, skipped relations {buildings.MultipolygonRelationsSkipped})");
        if (result.IsValid && vegetation is not null)
            Console.WriteLine($"Vegetation: {Path.GetFullPath(vegetationPath)} (areas {vegetation.PolygonsIncluded}/{vegetation.PolygonsConsidered}, trees {vegetation.Trees.Count}, generated {vegetation.GeneratedTreeCount})");
        if (result.IsValid && streetFurniture is not null)
            Console.WriteLine($"Street furniture: {Path.GetFullPath(furniturePath)} ({streetFurniture.TotalCandidates} candidates, {streetFurniture.Items.Count} selected)");
        Console.WriteLine($"Actual OSM length: {result.RawDistanceMeters:F1} m; reference: {config.ReferenceDistanceMeters?.ToString("F1") ?? "none"} m; difference: {(config.ReferenceDistanceMeters is { } refLength ? result.RawDistanceMeters - refLength : 0):F1} m");
        Console.WriteLine($"Points: OSM {result.SourceOsmNodeCount}, output {result.Centreline.Count}; max output spacing {result.MaximumAdjacentDistanceMeters:F1} m; max source gap {result.MaximumSourceNodeSpacingMeters:F1} m; max corridor deviation {result.MaximumCorridorDeviationMeters:F1} m; max snap {result.Snaps.Max(s => s.SnapDistanceMeters):F1} m");
        Console.WriteLine($"Graph: retained nodes {graphStats.RetainedNodeCount}, eligible ways {graphStats.EligibleWayCount}, graph nodes {graphStats.GraphNodeCount}, directed edges {graphStats.GraphEdgeCount}");
        Console.WriteLine($"Timings seconds: node scan {timings.NodeScanSeconds:F2}, way scan {timings.WayScanSeconds:F2}, graph {timings.GraphBuildSeconds:F2}, route solve {timings.RouteSolveSeconds:F2}, output {timings.OutputGenerationSeconds:F2}, cache build {timings.CacheBuildSeconds:F2}, cache load {timings.CacheLoadSeconds:F2}, cache save {timings.CacheSaveSeconds:F2} (hit={timings.CacheHit})");
        foreach (var snap in result.Snaps)
        Console.WriteLine($"Anchor {snap.Anchor.Name}: way {snap.WayId}, snap {snap.SnapDistanceMeters:F1} m, bearing {snap.BearingDegrees:F1}°, alignment {snap.DirectionAlignmentDegrees:F1}°, road {snap.RoadName ?? snap.Highway} {snap.RoadRef}");
        Console.WriteLine("Leg                          direct    routed   ratio  corridor  status");
        foreach (var leg in result.Legs)
            Console.WriteLine($"{leg.FromAnchor,8} -> {leg.ToAnchor,-8} {leg.StraightLineDistanceMeters,8:F1} {leg.DistanceMeters,9:F1} {leg.RoadDirectRatio,7:F2} {leg.MaximumCorridorDeviationMeters,8:F1}m {leg.Status}");
        foreach (var warning in result.Warnings) Console.WriteLine($"WARNING: {warning}");
        foreach (var error in result.Errors) Console.WriteLine($"ERROR: {error}");
        if (result.RouteRefSummary is { } routeRef)
        {
            Console.WriteLine($"Preferred refs: {string.Join(", ", routeRef.PreferredRefs)}; northbound {routeRef.NorthboundPreferredDistanceMeters:F1} m; southbound {routeRef.SouthboundPreferredDistanceMeters:F1} m; all connectors {routeRef.ConnectorDistanceMeters:F1} m; preferred-ref coverage {routeRef.PreferredRefPercent:F1}%");
            foreach (var connector in routeRef.NonPreferredConnectors)
                Console.WriteLine($"CONNECTOR way {connector.WayId}: {connector.Name ?? connector.Highway}, {connector.DistanceMeters:F1} m ({connector.Reason})");
        }
    }

    public static string SerializeGeoJson(RouteAuthoringConfig config, RouteBuildResult result, DateTimeOffset generatedAt)
    {
        var feature = new
        {
            type = "Feature",
            properties = new
            {
                id = config.Id,
                name = config.Name,
                isValid = result.IsValid,
                errors = result.Errors,
                source = "OpenStreetMap",
                attribution = "© OpenStreetMap contributors",
                license = "ODbL 1.0",
                generatedAtUtc = generatedAt,
                actualOsmDistanceMeters = result.RawDistanceMeters,
                referenceDistanceMeters = config.ReferenceDistanceMeters,
                actualToReferenceRatio = config.ReferenceDistanceMeters is { } reference && reference > 0
                    ? result.RawDistanceMeters / reference : (double?)null,
                pointCount = result.Centreline.Count,
                sourceOsmNodeCount = result.SourceOsmNodeCount,
                maximumOutputSpacingMeters = result.MaximumAdjacentDistanceMeters,
                maximumCorridorDeviationMeters = result.MaximumCorridorDeviationMeters
            },
            geometry = new
            {
                type = "LineString",
                coordinates = result.Centreline.Select(point => new[] { point.Longitude, point.Latitude }).ToArray()
            }
        };
        return JsonSerializer.Serialize(feature, JsonOptions);
    }

    public static string SerializePreviewHtml(RouteAuthoringConfig config, RouteBuildResult result,
        LandmarkExtractionResult? landmarks = null, BuildingExtractionResult? buildings = null,
        VegetationExtractionResult? vegetation = null, StreetFurnitureExtractionResult? streetFurniture = null,
        BackgroundStreetExtractionResult? backgroundStreets = null)
    {
        var candidateWarnings = result.Legs.Select((leg, index) => new PreviewWarning(index,
            leg.FromAnchor, leg.ToAnchor, BuildReviewReasons(leg, config)))
            .Where(item => item.Reasons.Count > 0).ToList();
        if (result.RouteRefSummary is { IsSuspicious: true } routeSummary)
            candidateWarnings.Add(new PreviewWarning(-1, "Route", "SH1", [$"only {routeSummary.PreferredRefPercent:F1}% of the route follows preferred refs"]));
        var previewAnchors = config.RoutingMode == "roadRef"
            ? result.Snaps.Select(snap => new PreviewAnchor(snap.Anchor.Name, snap.Anchor.SourceDistanceMeters,
                new GeoPoint(snap.Anchor.Latitude, snap.Anchor.Longitude), snap.SnappedCoordinate,
                snap.SnapDistanceMeters, snap.WayId, snap.Highway, snap.RoadName, snap.RoadRef,
                snap.BearingDegrees, snap.DirectionAlignmentDegrees)).ToArray()
            : config.Anchors.Select((anchor, index) =>
            {
                var snap = result.Snaps[index];
                return new PreviewAnchor(anchor.Name, anchor.SourceDistanceMeters,
                    new GeoPoint(anchor.Latitude, anchor.Longitude), snap.SnappedCoordinate,
                    snap.SnapDistanceMeters, snap.WayId, snap.Highway, snap.RoadName, snap.RoadRef,
                    snap.BearingDegrees, snap.DirectionAlignmentDegrees);
            }).ToArray();
        var data = new
        {
            id = config.Id,
            routeName = config.Name,
            routingMode = config.RoutingMode,
            actualOsmDistanceMeters = result.RawDistanceMeters,
            referenceDistanceMeters = config.ReferenceDistanceMeters,
            pointCount = result.Centreline.Count,
            largestSnapMeters = result.Snaps.Max(snap => snap.SnapDistanceMeters),
            largestCorridorDeviationMeters = result.MaximumCorridorDeviationMeters,
            detourWarningRatio = config.DetourWarningRatio,
            routeCorridorMeters = config.RouteCorridorMeters,
            routeRefSummary = result.RouteRefSummary,
            warnings = candidateWarnings,
            anchors = previewAnchors,
            legacyReferencePoints = config.LegacyReferencePoints.Select(point => new
                { name = point.Name, latitude = point.Latitude, longitude = point.Longitude }),
            legs = result.Legs.Select(leg => new
            {
                from = leg.FromAnchor,
                to = leg.ToAnchor,
                directDistanceMeters = leg.StraightLineDistanceMeters,
                routedDistanceMeters = leg.DistanceMeters,
                roadDirectRatio = leg.RoadDirectRatio,
                maximumCorridorDeviationMeters = leg.MaximumCorridorDeviationMeters,
                status = leg.Status,
                sectionType = leg.SectionType,
                ways = leg.Ways,
                startSnap = new { latitude = leg.StartSnap.SnappedCoordinate.Latitude, longitude = leg.StartSnap.SnappedCoordinate.Longitude },
                endSnap = new { latitude = leg.EndSnap.SnappedCoordinate.Latitude, longitude = leg.EndSnap.SnappedCoordinate.Longitude },
                route = leg.Centreline.Select(point => new[] { point.Latitude, point.Longitude })
            }),
            nearbyLandmarkCandidatesConsidered = landmarks?.NearbyCandidatesConsidered ?? 0,
            landmarks = landmarks?.Landmarks ?? [],
            buildingCount = buildings?.Buildings.Count ?? 0,
            buildings = buildings?.Buildings ?? [],
            vegetationAreaCount = vegetation?.Areas.Count ?? 0,
            vegetationAreas = vegetation?.Areas ?? [],
            trees = vegetation?.Trees ?? [],
            streetFurnitureCount = streetFurniture?.Items.Count ?? 0,
            streetFurniture = streetFurniture?.Items ?? [],
            streetCount = backgroundStreets?.Streets.Count ?? 0,
            streets = backgroundStreets?.Streets ?? []
        };
        var json = JsonSerializer.Serialize(data, JsonOptions);
        return PreviewHtmlTemplate.Replace("__ROUTE_REVIEW_DATA__", json, StringComparison.Ordinal);
    }

    private static BuildingReport ToBuildingReport(BuildingExtractionResult buildings, string pbfPath, double routeLength,
        RoutePointOutput routeOrigin)
    {
        var instances = buildings.Buildings.Select(item => Math.Clamp((int)(item.NearestRouteDistanceMeters / 350),
            0, Math.Max(0, (int)Math.Ceiling(routeLength / 350) - 1))).Distinct().Count();
        var triangles = buildings.Buildings.Sum(item => Math.Max(0, (item.Footprint.Count - 1) * 3 - 2));
        var categoryCounts = buildings.Buildings.GroupBy(item => item.VisualCategory)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var roofShapeCounts = buildings.Buildings.GroupBy(item => item.SourceTags.GetValueOrDefault("roof:shape")?.ToLowerInvariant() ?? "untagged")
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var style = AnalyzeBuildingStyle(buildings.Buildings, routeLength, routeOrigin);
        return new BuildingReport(buildings.NearbyCandidates, buildings.SelectedCount, buildings.OrdinaryWays,
            buildings.MultipolygonRelationsIncluded, buildings.MultipolygonRelationsSkipped, buildings.NamedBuildings,
            buildings.ExplicitHeightCount, buildings.LevelsHeightCount, buildings.FallbackHeightCount,
            buildings.MalformedHeights, buildings.MalformedOrExcludedRecords, buildings.OriginalVertices,
            buildings.SimplifiedVertices, buildings.LargestFootprintVertices, buildings.MinimumHeightMeters,
            buildings.MaximumHeightMeters, buildings.MedianHeightMeters, triangles, instances, instances,
            buildings.ExtractionSeconds, Path.GetFileName(pbfPath))
        {
            VisualCategoryCounts = categoryCounts,
            RoofShapeTagCounts = roofShapeCounts,
            BuildingsWithRoofMetadata = buildings.Buildings.Count(item => item.SourceTags.Keys.Any(key => key.StartsWith("roof:", StringComparison.OrdinalIgnoreCase))),
            ExplicitBuildingColourCount = buildings.Buildings.Count(item => item.SourceTags.ContainsKey("building:colour")),
            ExplicitRoofColourCount = buildings.Buildings.Count(item => item.SourceTags.ContainsKey("roof:colour")),
            ExplicitMaterialTagCount = buildings.Buildings.Count(item => item.SourceTags.ContainsKey("building:material") || item.SourceTags.ContainsKey("roof:material")),
            EffectiveFlatRoofCount = style.FlatRoofs, RoofedBuildingCount = buildings.SelectedCount,
            EffectiveGabledRoofCount = style.GabledRoofs,
            EffectiveHippedRoofCount = style.HippedRoofs, FlatRoofFallbackCount = style.FlatFallbacks,
            WindowDetailedBuildingCount = style.WindowedBuildings, VisualFacadeTriangleCount = style.FacadeTriangles,
            VisualRoofTriangleCount = style.RoofTriangles, VisualWindowTriangleCount = style.WindowTriangles,
            VisualTriangleCount = style.FacadeTriangles + style.RoofTriangles + style.WindowTriangles,
            FacadePaletteVariantCount = BuildingVisualStyle.PaletteCount, RuntimeSharedMaterialCount = 3,
            VisualRuntimeMeshChunkCount = instances, VisualRuntimeMeshInstanceCount = instances,
            VisualRuntimeSurfaceCount = instances * 2 + style.WindowSurfaces
        };
    }

    private static (int FlatRoofs, int GabledRoofs, int HippedRoofs, int FlatFallbacks,
        int WindowedBuildings, int FacadeTriangles, int RoofTriangles, int WindowTriangles, int WindowSurfaces) AnalyzeBuildingStyle(
        IReadOnlyList<RouteBuilding> buildings, double routeLength, RoutePointOutput routeOrigin)
    {
        var flat=0;var gabled=0;var hipped=0;var fallback=0;var windowed=0;var facade=0;var roof=0;var windows=0;
        var windowChunks=new HashSet<int>();
        foreach(var building in buildings)
        {
            const double radians=Math.PI/180;const double earth=6371000;
            var scaleX=earth*Math.Cos(routeOrigin.Latitude*radians)*radians;var scaleZ=earth*radians;
            var ring=building.Footprint.Select(point=>new System.Numerics.Vector3(
                (float)((point.Longitude-routeOrigin.Longitude)*scaleX),0,(float)(-(point.Latitude-routeOrigin.Latitude)*scaleZ))).ToArray();
            var tags=building.SourceTags;
            var rawShape=tags.GetValueOrDefault("roof:shape");var shape=BuildingVisualStyle.NormalizeRoofShape(rawShape);
            var width=Math.Min(ring.Max(p=>p.X)-ring.Min(p=>p.X),ring.Max(p=>p.Z)-ring.Min(p=>p.Z));
            var roofHeight=BuildingVisualStyle.ResolveRoofHeight((float)building.HeightMeters,width,
                tags.GetValueOrDefault("roof:height"),tags.GetValueOrDefault("roof:levels"));
            BuildingMeshGeometry.StyledMesh styled;
            try
            {
                styled=BuildingMeshGeometry.BuildStyled(ring,(float)building.HeightMeters,shape,roofHeight,
                    BuildingVisualStyle.ResolveStoreys(tags.GetValueOrDefault("building:levels"),(float)building.HeightMeters),
                    BuildingVisualStyle.ShouldAddFacadeDetail(building.DistanceFromRouteMeters));
            }
            catch (ArgumentException)
            {
                // Match BuildingLayer's handling of degenerate footprints so route authoring can still report
                // the valid meshes and retain the source footprint for diagnostics.
                continue;
            }
            facade+=styled.FacadeTriangleCount;roof+=styled.RoofTriangleCount;windows+=styled.WindowTriangleCount;
            if(styled.WindowCount>0)
            {
                windowed++;
                windowChunks.Add(Math.Clamp((int)(building.NearestRouteDistanceMeters/350),0,
                    Math.Max(0,(int)Math.Ceiling(routeLength/350)-1)));
            }
            if(styled.EffectiveRoofShape=="gabled")gabled++;
            else if(styled.EffectiveRoofShape=="hipped")hipped++;
            else {flat++;if(!string.IsNullOrWhiteSpace(rawShape)&&!rawShape.Equals("flat",StringComparison.OrdinalIgnoreCase))fallback++;}
        }
        return(flat,gabled,hipped,fallback,windowed,facade,roof,windows,windowChunks.Count);
    }

    private static VegetationReport ToVegetationReport(VegetationExtractionResult vegetation, string pbfPath, double routeLength)
    {
        const double chunkLength = 400;
        var areaChunks = vegetation.Areas.Where(area => area.RenderGround).Select(area => Math.Clamp(
            (int)(area.NearestRouteDistanceMeters / chunkLength), 0, Math.Max(0, (int)Math.Ceiling(routeLength / chunkLength) - 1))).Distinct().Count();
        var treeGroups = vegetation.Trees.GroupBy(tree => Math.Clamp((int)(tree.NearestRouteDistanceMeters / chunkLength),
            0, Math.Max(0, (int)Math.Ceiling(routeLength / chunkLength) - 1))).ToArray();
        var areaTriangles = vegetation.Areas.Where(area => area.RenderGround).Sum(area => Math.Max(0, area.Polygon.Count - 3));
        var multiMeshes = treeGroups.Sum(group => 1 + group.Select(tree => tree.TreeType).Distinct(StringComparer.Ordinal).Count());
        return new VegetationReport(vegetation.PolygonsConsidered, vegetation.PolygonsIncluded,
            vegetation.CategoryCounts, vegetation.NamedAreas, vegetation.ExplicitTreeCount, vegetation.TreeRowCount,
            vegetation.TreeRowInstances, vegetation.GeneratedTreeCount, vegetation.Trees.Count,
            vegetation.TreesRejectedForRoadProximity, vegetation.TreesRejectedForBuildingIntersection,
            vegetation.TreesRejectedAsDuplicates, vegetation.TreesRejectedNearLandmarks,
            vegetation.OriginalPolygonVertices, vegetation.SimplifiedPolygonVertices, vegetation.LargestPolygonVertices,
            vegetation.DemSamples, vegetation.DemCacheHits, vegetation.MalformedOrSkippedGeometries,
            vegetation.Areas.Count(area => area.RenderGround), areaTriangles, multiMeshes, areaChunks,
            1 + multiMeshes + areaChunks, vegetation.GenerationSeconds, Path.GetFileName(pbfPath));
    }

    private static StreetFurnitureReport ToStreetFurnitureReport(StreetFurnitureExtractionResult result, string pbfPath, double routeLength)
    {
        var categories = result.Items.GroupBy(item => item.Category, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        int Count(string category) => categories.GetValueOrDefault(category);
        var multiMeshes = CountRuntimeMultiMeshes(result.Items, routeLength);
        return new StreetFurnitureReport(result.TotalCandidates, result.Items.Count, categories, Count("street-lamp"),
            Count("traffic-signal"), Count("stop-sign") + Count("give-way-sign"), Count("bollard"), Count("crossing"),
            Count("bus-stop"), Count("bench"), result.MalformedOrSkippedItems, result.ItemsInsideRoadRibbon,
            result.ElevationSamples, multiMeshes, result.Items.Count,
            result.Items.Sum(item => StreetFurnitureExtractor.MeshPartCount(item.Category)),
            1 + multiMeshes, result.GenerationSeconds, Path.GetFileName(pbfPath));
    }

    private static int CountRuntimeMultiMeshes(IReadOnlyList<StreetFurnitureItem> items, double routeLength) =>
        items.GroupBy(item => Math.Clamp((int)(item.NearestRouteDistanceMeters / 400),
            0, Math.Max(0, (int)Math.Ceiling(routeLength / 400) - 1)))
            .Sum(group => group.Select(item => item.Category).Distinct(StringComparer.Ordinal)
                .Sum(StreetFurnitureExtractor.MeshPartCount));

    private static IReadOnlyList<string> BuildReviewReasons(RoutedLeg leg, RouteAuthoringConfig config)
    {
        var reasons = new List<string>();
        if (config.RoutingMode == "roadRef")
        {
            if (leg.SectionType == "connector") reasons.Add("turnaround connector; inspect non-preferred road use");
            return reasons;
        }
        if (leg.RoadDirectRatio > config.DetourWarningRatio) reasons.Add($"road/direct ratio {leg.RoadDirectRatio:F2} exceeds {config.DetourWarningRatio:F2}");
        if (leg.RoadDirectRatio < 0.75) reasons.Add($"road/direct ratio {leg.RoadDirectRatio:F2} is below 0.75");
        if (leg.StartSnap.SnapDistanceMeters > 30 || leg.EndSnap.SnapDistanceMeters > 30) reasons.Add("large anchor projection over 30 m");
        if (leg.MaximumCorridorDeviationMeters >= Math.Max(100, config.RouteCorridorMeters * 0.75)) reasons.Add("large corridor deviation");
        return reasons;
    }

    private const string PreviewHtmlTemplate = """
<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>Wodra route review</title>
  <link rel="stylesheet" href="https://unpkg.com/leaflet@1.9.4/dist/leaflet.css">
  <style>
    html,body{height:100%;margin:0;font:14px/1.4 system-ui,sans-serif;color:#18212b}#layout{height:100%;display:grid;grid-template-columns:minmax(290px,360px) 1fr}#panel{overflow:auto;padding:16px;background:#f8fafc;border-right:1px solid #cbd5e1}#map{height:100%;min-height:420px}.metric{display:flex;justify-content:space-between;gap:12px;padding:4px 0;border-bottom:1px solid #e2e8f0}.muted{color:#64748b;font-size:12px}.key{display:flex;gap:7px;align-items:center;margin:6px 0}.swatch{width:20px;height:4px;background:#2563eb}.warning{color:#9a3412}.leg-list{padding:0;list-style:none}.leg-list button{width:100%;text-align:left;margin:3px 0;padding:7px;border:1px solid #cbd5e1;border-radius:5px;background:white;cursor:pointer}.leg-list button.suspicious{border-left:5px solid #dc2626;background:#fff7ed}.leaflet-popup-content{line-height:1.45}.popup-title{font-weight:700}.direction-arrow{background:transparent;border:0;color:#102a43;font-size:22px;text-shadow:0 1px 3px white,1px 0 3px white,-1px 0 3px white;line-height:22px}.start-marker{background:#15803d}.finish-marker{background:#b91c1c}.anchor-marker{background:#7c3aed}.projected-marker{background:#111827}.landmark-marker{background:#ea580c;border:2px solid white;border-radius:50%;box-shadow:0 1px 4px #334155}.leaflet-div-icon{border:0}.tile-note{font-size:11px;color:#64748b}@media(max-width:760px){#layout{grid-template-columns:1fr;grid-template-rows:minmax(240px,38vh) 1fr}#panel{grid-row:2;border:0}#map{grid-row:1;min-height:240px}}
  </style>
</head>
<body>
<div id="layout"><aside id="panel"><h2 id="route-title">Route review</h2><div id="summary"></div><div id="legend"></div><h3>Map layers</h3><label><input id="buildings-toggle" type="checkbox"> Buildings</label><br><label><input id="vegetation-toggle" type="checkbox"> Vegetation</label><br><label><input id="trees-toggle" type="checkbox"> Trees</label><br><label><input id="street-furniture-toggle" type="checkbox"> Street furniture</label><br><label><input id="streets-toggle" type="checkbox"> Streets</label><h3>Landmark candidates</h3><p id="landmark-summary"></p><p class="muted">Click an orange marker to inspect name, category, OSM tags, ID, and route distance. Tree markers appear at zoom 15+.</p><h3>Review notes</h3><div id="warnings"></div><h3>Route sections</h3><ol id="leg-list" class="leg-list"></ol><p class="tile-note">Map tiles load from OpenStreetMap when online. © OpenStreetMap contributors.</p></aside><main id="map" aria-label="Route review map"></main></div>
<script type="application/json" id="route-review-data">__ROUTE_REVIEW_DATA__</script>
<script src="https://unpkg.com/leaflet@1.9.4/dist/leaflet.js"></script>
<script>
(() => {
  const data = JSON.parse(document.getElementById('route-review-data').textContent);
  const esc = value => String(value ?? '').replace(/[&<>"']/g, ch => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[ch]));
  const fmt = (value, digits=1) => Number(value).toFixed(digits);
  const map = L.map('map');
  L.tileLayer('https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png', {maxZoom:19, attribution:'© OpenStreetMap contributors'}).addTo(map);
  document.getElementById('route-title').textContent = `Route: ${data.routeName}`;
  const summary = [
    ['Actual OSM length', `${fmt(data.actualOsmDistanceMeters)} m`],
    ['Legacy/reference length', data.referenceDistanceMeters == null ? '—' : `${fmt(data.referenceDistanceMeters)} m`],
    ['Points', data.pointCount],
    ['Largest snap', `${fmt(data.largestSnapMeters)} m`],
    ['Largest corridor deviation', `${fmt(data.largestCorridorDeviationMeters)} m`],
    ['Nearby named candidates', data.nearbyLandmarkCandidatesConsidered],
    ['Selected landmarks', data.landmarks.length],
    ['Warnings/questionable legs', data.warnings.length ? data.warnings.map(w => `${w.from} → ${w.to}`).join(', ') : 'None']
  ];
  if (data.routeRefSummary) {
    summary.push(['Northbound SH1', `${fmt(data.routeRefSummary.northboundPreferredDistanceMeters)} m`]);
    summary.push(['Southbound SH1', `${fmt(data.routeRefSummary.southboundPreferredDistanceMeters)} m`]);
    summary.push(['All connectors', `${fmt(data.routeRefSummary.connectorDistanceMeters)} m`]);
    summary.push(['Preferred-ref coverage', `${fmt(data.routeRefSummary.preferredRefPercent)}%`]);
  }
  document.getElementById('summary').innerHTML = summary.map(([label,value]) => `<div class="metric"><span>${esc(label)}</span><strong>${esc(value)}</strong></div>`).join('');
  const isRoadRef = data.routingMode === 'roadRef';
  document.getElementById('legend').innerHTML = isRoadRef
    ? '<div class="key"><span class="swatch" style="background:#dc2626"></span><span>Northbound SH1</span></div><div class="key"><span class="swatch" style="background:#2563eb"></span><span>Southbound SH1</span></div><div class="key"><span class="swatch" style="background:#f97316"></span><span>Connectors</span></div><div class="key"><span class="swatch" style="background:#94a3b8"></span><span>Legacy inaccurate reference (dashed)</span></div>'
    : '<div class="key"><span class="swatch" style="background:#2563eb"></span><span>Authored anchor reference line (dashed)</span></div><div class="key"><span class="swatch" style="background:#64748b"></span><span>Anchor to projected road position</span></div>';
  const bounds = [];
  const reference = isRoadRef ? data.legacyReferencePoints.map(a => [a.latitude,a.longitude])
    : data.anchors.map(a => [a.original.latitude,a.original.longitude]);
  if (reference.length > 1) L.polyline(reference,{color:isRoadRef?'#94a3b8':'#2563eb',weight:2,dashArray:'7 7',opacity:isRoadRef ? .42 : .75}).bindPopup(isRoadRef?'Legacy inaccurate reference':'Original authored anchor polyline').addTo(map);
  reference.forEach(p => bounds.push(p));
  const palette = ['#2563eb','#0891b2','#7c3aed','#16a34a','#d97706','#db2777','#4f46e5','#0f766e'];
  const warningIndexes = new Set(data.warnings.map(w => w.index));
  const legLayers = [];
  const legList = document.getElementById('leg-list');
  data.legs.forEach((leg,index) => {
    const route = leg.route;
    route.forEach(p => bounds.push(p));
    const flagged = warningIndexes.has(index);
    const sectionColor = leg.sectionType === 'northbound' ? '#dc2626' : leg.sectionType === 'southbound' ? '#2563eb' : leg.sectionType === 'connector' ? '#f97316' : palette[index % palette.length];
    const color = flagged && !isRoadRef ? '#dc2626' : sectionColor;
    const line = L.polyline(route,{color,weight:flagged?7:5,opacity:.9}).addTo(map);
    const wayText = leg.ways.map(w => `#${w.wayId} ${esc(w.name || w.highway)}${w.ref ? ` (${esc(w.ref)})` : ''} · oneway=${esc(w.oneway || 'no')} · access=${esc(w.access || 'default')}${w.bicycle?` · bicycle=${esc(w.bicycle)}`:''}`).join('<br>') || 'No way metadata';
    const popup = `<div class="popup-title">${esc(leg.from)} → ${esc(leg.to)}</div><div>Section: ${esc(leg.sectionType)}</div><div>Direct anchor distance: ${fmt(leg.directDistanceMeters)} m</div><div>Routed OSM distance: ${fmt(leg.routedDistanceMeters)} m</div><div>Road/direct ratio: ${fmt(leg.roadDirectRatio,2)}</div><div>Maximum corridor deviation: ${fmt(leg.maximumCorridorDeviationMeters)} m</div><div>Status: ${esc(leg.status)}${flagged?' · questionable':''}</div><hr>${wayText}`;
    line.bindPopup(popup);
    legLayers.push(line);
    const mid = route[Math.floor(route.length/2)];
    if (mid) {
      const after = route[Math.min(route.length-1,Math.floor(route.length/2)+1)] || mid;
      const before = route[Math.max(0,Math.floor(route.length/2)-1)] || mid;
      const bearing = (Math.atan2(after[1]-before[1],after[0]-before[0]) * 180 / Math.PI + 360) % 360;
      const arrow = L.marker(mid,{icon:L.divIcon({className:'direction-arrow',html:`<span style="display:block;transform:rotate(${bearing-90}deg)">➤</span>`,iconSize:[22,22],iconAnchor:[11,11]}),interactive:false}).addTo(map);
    }
    const button = document.createElement('li');
    button.innerHTML = `<button class="${flagged?'suspicious':''}">${esc(leg.from)} → ${esc(leg.to)} · ${fmt(leg.routedDistanceMeters)} m · ratio ${fmt(leg.roadDirectRatio,2)}</button>`;
    button.querySelector('button').addEventListener('click',() => { map.fitBounds(line.getBounds().pad(.25)); line.openPopup(); });
    legList.appendChild(button);
  });
  data.anchors.forEach((anchor,index) => {
    const original = [anchor.original.latitude,anchor.original.longitude];
    const projected = [anchor.projected.latitude,anchor.projected.longitude];
    bounds.push(projected);
    L.polyline([original,projected],{color:'#64748b',weight:2,dashArray:'3 5',opacity:.8}).addTo(map);
    const isStart = index === 0;
    const isFinish = index === data.anchors.length-1;
    const anchorPopup = `<div class="popup-title">${esc(anchor.name)}</div><div>${isRoadRef?'Control area':'Source/reference distance'}: ${isRoadRef?'':`${fmt(anchor.sourceDistanceMeters)} m`}</div><div>Original: ${fmt(anchor.original.latitude,6)}, ${fmt(anchor.original.longitude,6)}</div><div>Projected: ${fmt(anchor.projected.latitude,6)}, ${fmt(anchor.projected.longitude,6)}</div><div>Snap distance: ${fmt(anchor.snapDistanceMeters)} m</div><div>Way ID: ${anchor.wayId}</div><div>Road: ${esc(anchor.roadName || anchor.highway)}${anchor.roadRef?` (${esc(anchor.roadRef)})`:''}</div><div>Bearing/alignment: ${fmt(anchor.bearingDegrees)}° / ${fmt(anchor.directionAlignmentDegrees)}° · direction ${anchor.directionAlignmentDegrees<=90?'compatible':'opposed'}</div>`;
    const originalMarker = L.circleMarker(original,{radius:isStart?10:6,color:isFinish?'#15803d':'#fff',weight:2,fillColor:isStart?'#15803d':isFinish?'#b91c1c':'#7c3aed',fillOpacity:1}).bindTooltip(isStart?'START':isFinish?'FINISH':anchor.name).bindPopup(anchorPopup).addTo(map);
    L.circleMarker(projected,{radius:5,color:'#fff',weight:2,fillColor:'#111827',fillOpacity:1}).bindPopup(anchorPopup).addTo(map);
  });
  document.getElementById('landmark-summary').textContent = `${data.landmarks.length} ranked unique features from ${data.nearbyLandmarkCandidatesConsidered} nearby named candidates.`;
  data.landmarks.forEach(item => {
    const point = [item.latitude, item.longitude]; bounds.push(point);
    const tags = Object.entries(item.tags || {}).map(([key,value]) => `<div><b>${esc(key)}</b>: ${esc(value)}</div>`).join('');
    const popup = `<div class="popup-title">${esc(item.name)}</div><div>Category: ${esc(item.category)}</div><div>Distance from route: ${fmt(item.distanceFromRouteMeters)} m</div><div>Nearest route distance: ${fmt(item.nearestRouteDistanceMeters)} m</div><div>OSM ${esc(item.osmType)} ${esc(item.osmId)}</div><hr>${tags}`;
    L.marker(point,{icon:L.divIcon({className:'landmark-marker',html:'',iconSize:[14,14],iconAnchor:[7,7]})})
      .bindTooltip(esc(item.name)).bindPopup(popup).addTo(map);
  });
  const buildingLayer = L.layerGroup();
  data.buildings.forEach(item => {
    const ring = item.footprint.map(point => [point.latitude, point.longitude]);
    const tags = item.sourceTags || {};
    const popup = `<div class="popup-title">${esc(item.name || 'Unnamed building')}</div><div>OSM ${esc(item.osmType)} ${esc(item.osmId)}</div><div>Type: ${esc(item.buildingType)}</div><div>Height: ${fmt(item.heightMeters)} m (${esc(item.heightSource)})</div><div>Levels: ${esc(tags['building:levels'] || 'not tagged')}</div><div>Distance from route: ${fmt(item.distanceFromRouteMeters)} m</div>`;
    L.polygon(ring,{color:'#564437',weight:1,fillColor:'#a98261',fillOpacity:.48}).bindPopup(popup).addTo(buildingLayer);
  });
  const buildingToggle = document.getElementById('buildings-toggle');
  buildingToggle.parentElement.append(` (${data.buildingCount} available)`);
  buildingToggle.addEventListener('change',() => buildingToggle.checked ? buildingLayer.addTo(map) : map.removeLayer(buildingLayer));
  const vegetationLayer = L.layerGroup();
  data.vegetationAreas.forEach(area => {
    if (!area.renderGround) return;
    const ring = area.polygon.map(point => [point[1], point[0]]);
    const fill = area.category === 'wood' || area.category === 'forest' || area.category === 'nature-reserve' ? '#47734b'
      : area.category === 'grass' || area.category === 'grassland' || area.category === 'meadow' ? '#83a85a' : '#5e9a68';
    const popup = `<div class="popup-title">${esc(area.name || 'Unnamed green area')}</div><div>Category: ${esc(area.category)}</div><div>OSM ${esc(area.osmType)} ${esc(area.osmId)}</div><div>Distance from route: ${fmt(area.distanceFromRouteMeters)} m</div><div>Generated trees: ${area.generatedTreeCount}</div>`;
    L.polygon(ring,{color:fill,weight:1,fillColor:fill,fillOpacity:.42}).bindPopup(popup).addTo(vegetationLayer);
  });
  const treeLayer = L.layerGroup();
  data.trees.forEach(tree => {
    const color = tree.source === 'generated' ? '#176b32' : tree.source === 'tree-row' ? '#e67e22' : '#315a2b';
    L.circleMarker([tree.latitude,tree.longitude],{radius:2,color,weight:1,fillOpacity:.8})
      .bindPopup(`<div>${esc(tree.source)} tree</div><div>OSM ID: ${esc(tree.osmId)}</div><div>Elevation: ${fmt(tree.elevationMeters)} m</div>`).addTo(treeLayer);
  });
  const vegetationToggle = document.getElementById('vegetation-toggle');
  vegetationToggle.parentElement.append(` (${data.vegetationAreaCount} areas)`);
  vegetationToggle.addEventListener('change',() => vegetationToggle.checked ? vegetationLayer.addTo(map) : map.removeLayer(vegetationLayer));
  const treesToggle = document.getElementById('trees-toggle');
  treesToggle.parentElement.append(` (${data.trees.length} trees)`);
  const updateTreePreview = () => {
    if (treesToggle.checked && map.getZoom() >= 15) treeLayer.addTo(map); else map.removeLayer(treeLayer);
  };
  treesToggle.addEventListener('change',updateTreePreview);
  map.on('zoomend',updateTreePreview);
  const furnitureLayer = L.layerGroup();
  const furnitureColors = {'street-lamp':'#f5b700','traffic-signal':'#d62828','stop-sign':'#bc2f2f','give-way-sign':'#f77f00','traffic-sign':'#7c3aed','bollard':'#343a40','crossing':'#0077b6','bus-stop':'#2a9d8f','bench':'#795548'};
  const furnitureSymbols = {'street-lamp':'L','traffic-signal':'T','stop-sign':'S','give-way-sign':'▽','traffic-sign':'S','bollard':'B','crossing':'X','bus-stop':'BUS','bench':'=','unknown':'?'};
  data.streetFurniture.forEach(item => {
    const point=[item.latitude,item.longitude], color=furnitureColors[item.category]||'#555', symbol=furnitureSymbols[item.category]||furnitureSymbols.unknown;
    const tags=Object.entries(item.tags||{}).map(([key,value])=>`<div><b>${esc(key)}</b>: ${esc(value)}</div>`).join('');
    const popup=`<div class="popup-title">${esc(item.category)}</div><div>OSM ${esc(item.osmType)} ${esc(item.osmId)}</div><div>Route chainage: ${fmt(item.nearestRouteDistanceMeters)} m</div><div>Distance from route: ${fmt(item.distanceFromRouteMeters)} m</div><div>Elevation: ${fmt(item.elevationMeters)} m</div><div>Direction: ${item.directionDegrees==null?'route tangent '+fmt(item.routeTangentDegrees)+'°':fmt(item.directionDegrees)+'°'}</div>${item.name?`<div>Name: ${esc(item.name)}</div>`:''}<hr>${tags}`;
    L.marker(point,{icon:L.divIcon({className:'furniture-icon',html:`<span style="display:grid;place-items:center;width:20px;height:20px;border-radius:50%;background:${color};color:white;border:1px solid white;font-size:9px;font-weight:bold">${symbol}</span>`,iconSize:[20,20],iconAnchor:[10,10]})}).bindPopup(popup).addTo(furnitureLayer);
  });
  const streetLayer=L.layerGroup();
  const streetColors={motorway:'#30343b',trunk:'#414750',primary:'#525a63',secondary:'#646c75',tertiary:'#777f87',unclassified:'#858d94',residential:'#969da2',service:'#a3a9ad',living_street:'#858b91'};
  data.streets.forEach(road=>{
    const line=road.points.map(p=>[p.latitude,p.longitude]);
    L.polyline(line,{color:streetColors[road.highway]||'#777',weight:Math.max(1,Math.min(8,road.widthMeters)),opacity:.72})
      .bindPopup(`<b>${esc(road.name||'Unnamed road')}</b><br>Highway: ${esc(road.highway)}<br>OSM way: ${esc(road.osmId)}<br>Width: ${fmt(road.widthMeters)} m (${esc(road.widthSource)})<br>Oneway: ${road.oneway?'yes':'no'}<br>Distance from route: ${fmt(road.distanceFromRouteMeters)} m`).addTo(streetLayer);
  });
  const streetsToggle=document.getElementById('streets-toggle');
  streetsToggle.parentElement.append(` (${data.streetCount} segments)`);
  streetsToggle.addEventListener('change',()=>streetsToggle.checked?streetLayer.addTo(map):map.removeLayer(streetLayer));
  const furnitureToggle=document.getElementById('street-furniture-toggle');
  furnitureToggle.parentElement.append(` (${data.streetFurnitureCount} items)`);
  furnitureToggle.addEventListener('change',()=>furnitureToggle.checked?furnitureLayer.addTo(map):map.removeLayer(furnitureLayer));
  if (bounds.length) map.fitBounds(bounds,{padding:[24,24]}); else map.setView([-45.88,170.5],13);
  const warnings = document.getElementById('warnings');
  warnings.innerHTML = data.warnings.length ? `<ul>${data.warnings.map(w=>`<li class="warning">${w.index>=0?`<button data-index="${w.index}">`:''}${esc(w.from)} → ${esc(w.to)}: ${w.reasons.map(esc).join('; ')}${w.index>=0?'</button>':''}</li>`).join('')}</ul>` : '<p>None</p>';
  warnings.querySelectorAll('button').forEach(button => button.addEventListener('click',() => { const line=legLayers[Number(button.dataset.index)]; if(line){ map.fitBounds(line.getBounds().pad(.3)); line.openPopup(); } }));
})();
</script>
</body>
</html>
""";
}
