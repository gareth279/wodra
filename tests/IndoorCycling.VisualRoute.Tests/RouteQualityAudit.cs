using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Text.Json;

public sealed record RouteQualityFinding(string RouteId, string? FeatureId, long? OsmId,
    double? ChainageMeters, string Category, string Severity, double? MeasuredMeters,
    string Status, string Evidence);
public sealed record RouteQualityRouteSummary(string RouteId, string Environment, bool RoadRulesApplicable,
    int GeometrySamples, int IndexedRoadQuads, int SceneryObjectsChecked,
    int SceneryIntrusions, int SceneryCorrectable, int UnresolvedIntrusions,
    int CrossingsChecked, int ZebraCrossingsGenerated, int DuplicateMarkingsSuppressed,
    int SidecarsValidated);
public sealed record RouteQualityReport(int SchemaVersion, string OfflineSource,
    IReadOnlyList<RouteQualityRouteSummary> Routes, IReadOnlyList<RouteQualityFinding> Findings,
    IReadOnlyList<RouteQualityFinding> Corrections,
    IReadOnlyDictionary<string, string> SeverityOverrides);

/// <summary>Deterministic, offline route package audit using the production route geometry and clearance policy.</summary>
public static class RouteQualityAudit
{
    private const double AuthoredSceneReplacementRadiusMeters = 36;
    private const double AuthoredSceneBackdropChainageRadiusMeters = 110;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static int WriteReport(string outputPath)
    {
        var routes = new List<RouteQualityRouteSummary>();
        var findings = new List<RouteQualityFinding>();
        var corrections = new List<RouteQualityFinding>();
        var severityOverrides = ReadSeverityOverrides();
        var assembly = typeof(RouteCatalog).Assembly;
        foreach (var route in RouteCatalog.Routes.OrderBy(item => item.Id, StringComparer.Ordinal))
        {
            var isRoad = route.Environment == RouteEnvironment.Road;
            VisualRoute visual;
            try { visual = new VisualRoute(route); }
            catch (Exception ex)
            {
                findings.Add(new(route.Id, null, null, null, "invalid-route-geometry", "critical", null,
                    "unresolved", ex.GetType().Name + ": " + ex.Message));
                continue;
            }

            var geometrySamples = visual.Samples.Count;
            AuditGeometry(route, visual, findings);
            var corridor = isRoad
                ? new RoadCorridorGeometry(visual, RoadRibbonMath.RoadWidthMeters,
                    RoadRibbonMath.RoadWidthMeters / 2 - (float)IndoorCycling.Core.Simulation.SteeringLateralController.RiderMarginMeters,
                    (float)IndoorCycling.Core.Simulation.SteeringLateralController.RiderMarginMeters)
                : null;
            var sidecarsValidated = 0;
            if (isRoad)
            {
                foreach (var sidecar in new[] { "buildings", "vegetation", "streets", "street-furniture" })
                {
                    var resource = $"Wodra.GameData.Routes.{route.Id}.{sidecar}.json";
                    if (!RoutePackageFiles.Exists(assembly, resource, route.Id))
                        SidecarError(route.Id, sidecar, new FileNotFoundException($"Required road-route sidecar '{resource}' is missing."), findings);
                }
            }
            TerrainRoadCorridor[] backgroundRoads = [];
            var streetsName = $"Wodra.GameData.Routes.{route.Id}.streets.json";
            if (RoutePackageFiles.Exists(assembly, streetsName, route.Id))
            {
                try
                {
                    var streets = RouteBackgroundStreetDataLoader.LoadEmbedded(assembly, streetsName, route.Id);
                    var routeIndex = new BackgroundStreetGeometry.RouteSegmentSpatialIndex(visual.Samples);
                    backgroundRoads = BackgroundStreetGeometry.BuildTerrainCorridors(visual, streets, routeIndex);
                    corridor?.AddBackgroundRoads(backgroundRoads);
                    sidecarsValidated++;
                }
                catch (Exception ex) { SidecarError(route.Id, "streets", ex, findings); }
            }

            if (route.Id == "pine-hill-test" && corridor is not null)
            {
                var hedgeSceneName = $"Wodra.GameData.Routes.{route.Id}.observations.json";
                var scene = RouteSceneDataLoader.Load(assembly, hedgeSceneName, route.Id, route.DistanceMeters);
                var hedge = scene.Features.Single(item => item.Id == "395-right-hedge");
                var footprint = RoadCorridorGeometry.AuthoredFeatureFootprint(hedge.ObjectType,
                    hedge.LengthMeters, hedge.WidthMeters)!.Value;
                var clearance = corridor.Measure(visual, hedge.RouteChainageMeters,
                    (float)hedge.LateralPositionMeters, (float)hedge.ForwardOffsetMeters,
                    footprint.LengthMeters, footprint.WidthMeters, (float)hedge.OrientationDegrees);
                corrections.Add(new(route.Id, hedge.Id, null, hedge.RouteChainageMeters,
                    "authored-hedge-footprint", "info", clearance.ClearanceMeters, "measured-sidecar-placement",
                    "Prior placement 2.421814m / 4.0850516deg / 21x1.4m intersected the curved protected ribbon. " +
                    $"Current sidecar placement {hedge.LateralPositionMeters:F3}m / {hedge.OrientationDegrees:F4}deg; " +
                    $"BuildHedge conservative occupied bounds={footprint.LengthMeters:F2}x{footprint.WidthMeters:F2}m; " +
                    $"full route and background-street clearance={clearance.ClearanceMeters:F3}m."));
            }

            var sceneryChecked = 0; var intrusions = 0; var correctable = 0; var unresolved = 0;
            var sceneName = $"Wodra.GameData.Routes.{route.Id}.observations.json";
            if (isRoad && corridor is not null && RoutePackageFiles.Exists(assembly, sceneName, route.Id))
            {
                try
                {
                    var scene = RouteSceneDataLoader.Load(assembly, sceneName, route.Id, route.DistanceMeters);
                    sidecarsValidated++;
                    var authoredAsset = scene.Features.FirstOrDefault(item => item.ObjectType == "authored-scene-asset");
                    foreach (var feature in scene.Features)
                    {
                        if (authoredAsset is not null && feature != authoredAsset
                            && Math.Abs(feature.RouteChainageMeters - authoredAsset.RouteChainageMeters)
                                <= AuthoredSceneReplacementRadiusMeters
                            && feature.ObjectType is "timber-fence" or "picket-fence" or "broadleaf-tree" or "conifer-tree"
                                or "shrub" or "hedge" or "grass-verge" or "planting-bed" or "utility-pole")
                        {
                            findings.Add(new(route.Id, feature.Id, null, feature.RouteChainageMeters,
                                "authored-scenery-replaced-by-scene", "info", null, "intentionally-replaced",
                                $"same replacement policy as production RideScreen authored scene at {authoredAsset.RouteChainageMeters:F2}m"));
                            continue;
                        }
                        if (feature.ObjectType == "grass-verge")
                        {
                            var verts = GrassVergeFootprint(feature, visual);
                            var clearance = corridor.MeasurePolygon(verts);
                            if (clearance.Intersects || clearance.ClearanceMeters < RoadCorridorGeometry.RoadsideClearanceMeters)
                                findings.Add(new(route.Id, feature.Id, null, feature.RouteChainageMeters,
                                    "ground-cover-road-overlap", "info", clearance.ClearanceMeters, "clipped-at-load",
                                    "ground-cover triangles inside the shared protected corridor are clipped; no physical obstruction"));
                            continue;
                        }
                        var dimensions = RoadCorridorGeometry.AuthoredFeatureFootprint(feature.ObjectType,
                            feature.LengthMeters, feature.WidthMeters);
                        if (dimensions is not { } size) continue;
                        sceneryChecked++;
                        var placement = corridor.FindSafeRoutePlacement(visual, feature.RouteChainageMeters,
                            (float)feature.LateralPositionMeters, (float)feature.ForwardOffsetMeters,
                            size.LengthMeters, size.WidthMeters, (float)feature.OrientationDegrees, maximumShiftMeters: 24);
                        if (!placement.Adjusted && placement.Resolved) continue;
                        intrusions++;
                        if (placement.Resolved) correctable++; else unresolved++;
                        var status = placement.Resolved ? "corrected-at-load" : "unresolved";
                        var severity = "review";
                        findings.Add(new(route.Id, feature.Id, null, feature.RouteChainageMeters,
                            "authored-scenery-road-corridor", severity, placement.Clearance.ClearanceMeters,
                            status, $"type={feature.ObjectType}; sourceLateral={feature.LateralPositionMeters:F3}m; " +
                                $"sourceForward={feature.ForwardOffsetMeters:F3}m; visualLateral={placement.VisualLateralMeters:F3}m; " +
                                $"visualForward={placement.VisualForwardMeters:F3}m; rotation={feature.OrientationDegrees:F2}deg; " +
                                $"footprint={size.LengthMeters:F2}x{size.WidthMeters:F2}m"));
                    }
                }
                catch (Exception ex) { SidecarError(route.Id, "observations", ex, findings); }
            }

            var crossingsChecked = 0; var zebraGenerated = 0; var duplicates = 0;
            var furnitureName = $"Wodra.GameData.Routes.{route.Id}.street-furniture.json";
            if (isRoad && RoutePackageFiles.Exists(assembly, furnitureName, route.Id))
            {
                try
                {
                    var furniture = RouteStreetFurnitureDataLoader.LoadEmbedded(assembly, furnitureName, route.Id);
                    sidecarsValidated++;
                    var controls = furniture.Items.Select(item => new RoadMarkingControl(item.Category, item.OsmId,
                        item.NearestRouteDistanceMeters, item.DistanceFromRouteMeters, item.Tags)).ToArray();
                    var markings = RoadMarkingGeometry.Build(visual, controls, RoadRibbonMath.RoadWidthMeters);
                    crossingsChecked = markings.CrossingSourcesConsidered;
                    zebraGenerated = markings.ZebraCrossings;
                    duplicates = markings.CrossingDuplicatesSuppressed;
                    foreach (var crossing in controls.Where(item => item.Category == "crossing"))
                    {
                        if (crossing.DistanceFromRouteMeters > RoadMarkingGeometry.MaximumCrossingDistanceFromRouteMeters
                            || RoadMarkingGeometry.IsExplicitlyZebraCrossing(crossing)) continue;
                        var mark = crossing.Tags?.GetValueOrDefault("crossing") ?? "unknown";
                        findings.Add(new(route.Id, null, crossing.OsmId, crossing.NearestRouteDistanceMeters,
                            "unsupported-zebra-evidence", "info", null, "suppressed",
                            $"OSM tags crossing={mark}; zebra rendering requires explicit zebra evidence"));
                    }
                    if (markings.CrossingDuplicatesSuppressed > 0)
                        findings.Add(new(route.Id, null, null, null, "duplicate-crossings", "info", null,
                            "deduplicated", $"suppressed={markings.CrossingDuplicatesSuppressed}"));
                    if (route.RenderRouteReferenceMarkings)
                        findings.Add(new(route.Id, null, null, null, "generic-reference-paint", "info", null,
                            "explicitly-enabled", "route package opts into continuous reference paint"));
                }
                catch (Exception ex) { SidecarError(route.Id, "street-furniture", ex, findings); }
            }

            if (isRoad && corridor is not null)
            {
                AuditTrees(route, visual, corridor, assembly, findings, ref sidecarsValidated,
                    ref sceneryChecked, ref intrusions, ref correctable, ref unresolved);
                AuditBuildings(route, visual, corridor, assembly, findings, ref sidecarsValidated,
                    ref sceneryChecked, ref intrusions);
                AuditFurniture(route, visual, corridor, assembly, findings, ref sidecarsValidated,
                    ref sceneryChecked, ref intrusions, ref correctable, ref unresolved);
            }

            routes.Add(new(route.Id, route.Environment.ToString(), isRoad, geometrySamples,
                corridor?.IndexedQuadCount ?? 0, sceneryChecked, intrusions, correctable, unresolved,
                crossingsChecked, zebraGenerated, duplicates, sidecarsValidated));
        }

        if (severityOverrides.Count > 0)
            findings = findings.Select(item => severityOverrides.TryGetValue(item.Category, out var severity)
                ? item with { Severity = severity } : item).ToList();
        foreach (var packageError in RouteCatalog.PackageErrors)
        {
            var separator = packageError.IndexOf(':');
            var routeId = separator < 0 ? packageError : packageError[..separator];
            findings.Add(new(routeId, null, null, null, "route-package-invalid", "critical", null,
                "unresolved", packageError));
        }
        findings.Sort(CompareFinding);
        corrections.Sort(CompareFinding);
        findings = findings.Select(item => item.MeasuredMeters is { } value && !double.IsFinite(value)
            ? item with { MeasuredMeters = null } : item).ToList();
        var report = new RouteQualityReport(1, "committed route packages; no network access",
            routes.OrderBy(item => item.RouteId, StringComparer.Ordinal).ToArray(), findings, corrections,
            new SortedDictionary<string, string>(severityOverrides, StringComparer.Ordinal));
        var fullPath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, JsonSerializer.Serialize(report, JsonOptions) + Environment.NewLine);
        var roadRoutes = routes.Count(item => item.RoadRulesApplicable);
        Console.WriteLine($"Route quality audit: routes={routes.Count} roadRoutes={roadRoutes} findings={findings.Count} " +
            $"unresolved={findings.Count(item => item.Status == "unresolved")} corrected={findings.Count(item => item.Status == "corrected-at-load")} " +
            $"report={fullPath}");
        foreach (var route in report.Routes)
            Console.WriteLine($"  {route.RouteId,-38} scenery={route.SceneryObjectsChecked,5} intrusions={route.SceneryIntrusions,4} " +
                $"unresolved={route.UnresolvedIntrusions,3} crossings={route.CrossingsChecked,4} zebra={route.ZebraCrossingsGenerated,3}");
        return findings.Any(item => item.Severity == "critical" && item.Status == "unresolved") ? 1 : 0;
    }

    private static Dictionary<string, string> ReadSeverityOverrides()
    {
        var json = Environment.GetEnvironmentVariable("WODRA_ROUTE_QUALITY_SEVERITY_OVERRIDES");
        if (string.IsNullOrWhiteSpace(json)) return new(StringComparer.Ordinal);
        var values = JsonSerializer.Deserialize<Dictionary<string, string>>(json)
            ?? throw new InvalidDataException("Severity override JSON must be an object mapping violation categories to severities.");
        var allowed = new HashSet<string>(["info", "review", "error", "critical"], StringComparer.Ordinal);
        foreach (var pair in values)
            if (string.IsNullOrWhiteSpace(pair.Key) || !allowed.Contains(pair.Value))
                throw new InvalidDataException($"Invalid route audit severity override '{pair.Key}': '{pair.Value}'.");
        return new Dictionary<string, string>(values, StringComparer.Ordinal);
    }

    private static Vector2[] GrassVergeFootprint(RouteSceneFeature feature, VisualRoute route)
    {
        var pose=route.GetPoseAtDistance(feature.RouteChainageMeters);
        var forward=Vector3.Normalize(pose.Forward);
        var side=RiderLaneMath.LocalLeft(forward);
        var origin=pose.Position+side*(float)feature.LateralPositionMeters+forward*(float)feature.ForwardOffsetMeters;
        var radians=(float)feature.OrientationDegrees*MathF.PI/180;
        var c=MathF.Cos(radians); var s=MathF.Sin(radians);
        var w=(float)feature.WidthMeters; var l=(float)feature.LengthMeters;
        var local=new[]{new Vector2(-w*.5f,-l*.5f),new Vector2(w*.5f,-l*.5f),
            new Vector2(w*.42f,-l*.3f),new Vector2(w*.31f,l*.12f),new Vector2(w*.47f,l*.5f),
            new Vector2(-w*.48f,l*.5f),new Vector2(-w*.36f,l*.12f),new Vector2(-w*.46f,-l*.3f)};
        return local.Select(p=>{
            var lateral=p.X*c+p.Y*s; var along=-p.X*s+p.Y*c;
            var world=origin+side*lateral+forward*along; return new Vector2(world.X,world.Z);
        }).ToArray();
    }

    private static void AuditTrees(RouteDefinition route, VisualRoute visual, RoadCorridorGeometry corridor,
        Assembly assembly, List<RouteQualityFinding> findings, ref int sidecars, ref int checkedCount,
        ref int intrusions, ref int correctable, ref int unresolved)
    {
        var name = $"Wodra.GameData.Routes.{route.Id}.vegetation.json";
        if (!RoutePackageFiles.Exists(assembly, name, route.Id)) return;
        try
        {
            var data = RouteVegetationDataLoader.LoadEmbedded(assembly, name, route.Id); sidecars++;
            var originElevation = route.Points?.FirstOrDefault()?.ElevationMeters ?? 0;
            var sceneName = $"Wodra.GameData.Routes.{route.Id}.observations.json";
            var authored = RoutePackageFiles.Exists(assembly, sceneName, route.Id)
                ? RouteSceneDataLoader.Load(assembly, sceneName, route.Id, route.DistanceMeters) : null;
            var authoredDistances = authored?.Features.Where(item => item.ObjectType == "authored-scene-asset")
                .Select(item => item.RouteChainageMeters).ToArray() ?? [];
            foreach (var tree in data.Trees)
            {
                if (authoredDistances.Any(distance => Math.Abs(tree.NearestRouteDistanceMeters-distance)
                        <= AuthoredSceneBackdropChainageRadiusMeters)) continue;
                var p = tree.LocalXMeters is { } x && tree.LocalZMeters is { } z
                    ? visual.GetPositionForLocalCoordinate(x, z, tree.ElevationMeters ?? originElevation)
                    : visual.GetPositionForCoordinate(tree.Latitude!.Value, tree.Longitude!.Value,
                        tree.ElevationMeters ?? originElevation);
                var pose = visual.GetPoseAtDistance(tree.NearestRouteDistanceMeters);
                var side = RiderLaneMath.LocalLeft(pose.Forward);
                var lateral = Vector3.Dot(p - pose.Position, side);
                var forward = Vector3.Dot(p - pose.Position, Vector3.Normalize(pose.Forward));
                checkedCount++;
                var placement = corridor.FindSafeRoutePlacement(visual, tree.NearestRouteDistanceMeters,
                    lateral, forward, .38f, .38f, maximumShiftMeters: 16);
                if (!placement.Adjusted && placement.Resolved) continue;
                intrusions++;
                if (placement.Resolved) correctable++; else unresolved++;
                findings.Add(new(route.Id, tree.Source, tree.OsmId, tree.NearestRouteDistanceMeters,
                    "tree-trunk-road-corridor", placement.Resolved ? "review" : "critical",
                    placement.Clearance.ClearanceMeters, placement.Resolved ? "corrected-at-load" : "unresolved",
                    $"treeHeight={tree.HeightMeters:F1}m canopyRadius={tree.CanopyRadiusMeters:F1}m; trunk radius=.19m; canopy is visual overhang"));
            }
        }
        catch (Exception ex) { SidecarError(route.Id, "vegetation", ex, findings); }
    }

    private static void AuditBuildings(RouteDefinition route, VisualRoute visual, RoadCorridorGeometry corridor,
        Assembly assembly, List<RouteQualityFinding> findings, ref int sidecars, ref int checkedCount,
        ref int intrusions)
    {
        var name = $"Wodra.GameData.Routes.{route.Id}.buildings.json";
        if (!RoutePackageFiles.Exists(assembly, name, route.Id)) return;
        try
        {
            var buildings = RouteBuildingDataLoader.LoadEmbedded(assembly, name, route.Id); sidecars++;
            foreach (var building in buildings)
            {
                var elevation = building.BaseElevationMeters ?? 0;
                var ring = building.Footprint.Select(point => visual.GetPositionForCoordinate(
                    point.Latitude, point.Longitude, elevation)).Select(point => new Vector2(point.X, point.Z)).ToArray();
                checkedCount++;
                var backgroundClearance = corridor.MeasureBackgroundPolygon(ring);
                if (backgroundClearance.Intersects)
                    findings.Add(new(route.Id, "background-street", building.OsmId,
                        building.NearestRouteDistanceMeters, "building-background-road-intersection", "review",
                        backgroundClearance.ClearanceMeters, "retained-for-visual-review",
                        $"source background street osm={backgroundClearance.SourceOsmId}; building is not auto-removed"));
                var clearance = corridor.MeasureRoutePolygon(ring);
                if (!clearance.Intersects)
                {
                    if (clearance.ClearanceMeters < RoadCorridorGeometry.RoadsideClearanceMeters)
                        findings.Add(new(route.Id, null, building.OsmId, building.NearestRouteDistanceMeters,
                            "building-roadside-clearance", "info", clearance.ClearanceMeters, "near-clearance",
                            "building footprints are retained near kerbs; roadside gap does not delete buildings"));
                    continue;
                }
                intrusions++;
                var originElevation = route.Points?.FirstOrDefault()?.ElevationMeters ?? 0;
                var localBase = elevation - originElevation;
                var routeElevation = visual.GetPoseAtDistance(building.NearestRouteDistanceMeters).Position.Y;
                var physical = routeElevation >= localBase - 1 && routeElevation <= localBase + building.HeightMeters;
                var status = physical ? "suppressed-by-building-layer" : "elevated-overhang-permitted";
                findings.Add(new(route.Id, null, building.OsmId, building.NearestRouteDistanceMeters,
                    "building-footprint-road-corridor", physical ? "review" : "info", clearance.ClearanceMeters,
                    physical ? "suppressed-at-load" : status,
                    $"height={building.HeightMeters:F1}m base={elevation:F2}m footprintVertices={ring.Length}"));
            }
        }
        catch (Exception ex) { SidecarError(route.Id, "buildings", ex, findings); }
    }

    private static void AuditFurniture(RouteDefinition route, VisualRoute visual, RoadCorridorGeometry corridor,
        Assembly assembly, List<RouteQualityFinding> findings, ref int sidecars, ref int checkedCount,
        ref int intrusions, ref int correctable, ref int unresolved)
    {
        var name = $"Wodra.GameData.Routes.{route.Id}.street-furniture.json";
        if (!RoutePackageFiles.Exists(assembly, name, route.Id)) return;
        try
        {
            var data = RouteStreetFurnitureDataLoader.LoadEmbedded(assembly, name, route.Id); sidecars++;
            foreach (var item in data.Items)
            {
                if (item.Category == "crossing") continue;
                var pose = visual.GetPoseAtDistance(item.NearestRouteDistanceMeters);
                var source = visual.GetPositionForCoordinate(item.Latitude, item.Longitude, item.ElevationMeters ?? 0);
                var side = RiderLaneMath.LocalLeft(pose.Forward);
                var lateral = Vector3.Dot(source - pose.Position, side);
                var forward = Vector3.Dot(source - pose.Position, Vector3.Normalize(pose.Forward));
                var bounds = StreetFurniturePlacementMath.FootprintMetersForCategory(item.Category);
                checkedCount++;
                var placement = corridor.FindSafeRoutePlacement(visual, item.NearestRouteDistanceMeters,
                    lateral, forward, bounds.LengthMeters, bounds.WidthMeters,
                    maximumShiftMeters: item.Category == "bus-stop" ? 24 : 12);
                if (!placement.Adjusted && placement.Resolved) continue;
                intrusions++;
                if (placement.Resolved) correctable++; else unresolved++;
                findings.Add(new(route.Id, item.Category, item.OsmId, item.NearestRouteDistanceMeters,
                    "street-furniture-road-corridor", "review",
                    placement.Clearance.ClearanceMeters, placement.Resolved ? "relocated-at-load" : "unresolved",
                    $"source=({lateral:F2},{forward:F2})m; visual=({placement.VisualLateralMeters:F2}," +
                    $"{placement.VisualForwardMeters:F2})m from route pose"));
            }
        }
        catch (Exception ex) { SidecarError(route.Id, "street-furniture", ex, findings); }
    }

    private static void AuditGeometry(RouteDefinition route, VisualRoute visual, List<RouteQualityFinding> findings)
    {
        for (var i = 1; i < visual.Samples.Count; i++)
        {
            var prior = visual.Samples[i - 1]; var current = visual.Samples[i];
            var distance = Vector3.Distance(prior.Position, current.Position);
            if (!float.IsFinite(distance) || distance <= 0 || distance > 100)
                findings.Add(new(route.Id, $"segment-{i - 1}-{i}", null, prior.DistanceMeters,
                    "invalid-road-segment", "critical", distance, "unresolved", "segment length is non-finite, zero, or exceeds 100m"));
            else if (distance < .01f)
                findings.Add(new(route.Id, $"segment-{i - 1}-{i}", null, prior.DistanceMeters,
                    "short-road-segment", "review", distance, "retained",
                    "segment is sub-centimetre-scale after world-coordinate float conversion"));
        }
        var samples = visual.Samples;
        for (var i = 1; i < samples.Count - 1; i++)
        {
            var incoming = Vector3.Normalize(samples[i].Position - samples[i - 1].Position);
            var outgoing = Vector3.Normalize(samples[i + 1].Position - samples[i].Position);
            var turn = MathF.Acos(Math.Clamp(Vector3.Dot(incoming, outgoing), -1, 1)) * 180 / MathF.PI;
            if (turn > 150)
                findings.Add(new(route.Id, $"sample-{i}", null, samples[i].DistanceMeters,
                    "abrupt-heading-change", "error", turn, "review", "consecutive sampled route segments reverse by over 150 degrees"));
        }
        if (route.RouteType != "Point-to-Point")
        {
            var start = visual.GetPoseAtDistance(0); var seam = visual.GetPoseAtDistance(visual.LengthMeters);
            var positionGap = Vector3.Distance(start.Position, seam.Position);
            var headingDot = Vector3.Dot(Vector3.Normalize(start.Forward), Vector3.Normalize(seam.Forward));
            if (positionGap > .05f || headingDot < .9f)
                findings.Add(new(route.Id, "lap-seam", null, 0, "loop-seam-discontinuity", "critical",
                    Math.Max(positionGap, 1 - headingDot), "unresolved", $"positionGap={positionGap:F3}m headingDot={headingDot:F4}"));
        }
    }

    private static void SidecarError(string routeId, string sidecar, Exception exception,
        List<RouteQualityFinding> findings) => findings.Add(new(routeId, sidecar, null, null,
            "sidecar-invalid", "critical", null, "unresolved", exception.GetType().Name + ": " + exception.Message));

    private static int CompareFinding(RouteQualityFinding a, RouteQualityFinding b)
    {
        var c = string.CompareOrdinal(a.RouteId, b.RouteId); if (c != 0) return c;
        c = Nullable.Compare(a.ChainageMeters, b.ChainageMeters); if (c != 0) return c;
        c = string.CompareOrdinal(a.Category, b.Category); if (c != 0) return c;
        return Nullable.Compare(a.OsmId, b.OsmId);
    }
}
