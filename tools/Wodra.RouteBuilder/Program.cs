using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Wodra.RouteBuilder;

if (args.Length == 0 || args.Contains("--help") || args.Contains("-h"))
{
    PrintHelp();
    return args.Length == 0 ? 1 : 0;
}

try
{
    if (args[0].Equals("refresh-package", StringComparison.OrdinalIgnoreCase))
    {
        var refreshOptions = ParseOptions(args.Skip(1).ToArray());
        if (!refreshOptions.TryGetValue("--config", out var refreshConfigPath))
            throw new ArgumentException("--config is required.");
        if (!refreshOptions.TryGetValue("--output", out var refreshOutputDirectory))
            throw new ArgumentException("--output is required.");
        var refreshConfig = JsonSerializer.Deserialize<RouteAuthoringConfig>(
            await File.ReadAllTextAsync(refreshConfigPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("Could not read route config JSON.");
        refreshConfig.Validate();
        await RoutePackageMetadata.RefreshAsync(refreshOutputDirectory, refreshConfig);
        Console.WriteLine($"Refreshed route package metadata for {refreshConfig.Id}.");
        return 0;
    }
    if (args[0].Equals("refresh-street-directions", StringComparison.OrdinalIgnoreCase))
    {
        var directionOptions = ParseOptions(args.Skip(1).ToArray());
        if (!directionOptions.TryGetValue("--pbf", out var directionPbf)
            || !directionOptions.TryGetValue("--files", out var directionFiles))
            throw new ArgumentException("refresh-street-directions requires --pbf and --files (semicolon-separated sidecar paths).");
        var files = directionFiles.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (files.Length == 0) throw new ArgumentException("At least one street sidecar path is required.");
        var documents = files.Select(path => (Path: path, Json: JsonNode.Parse(File.ReadAllText(path))
            ?? throw new InvalidDataException($"Could not parse street sidecar '{path}'."))).ToArray();
        var wantedWayIds = documents.SelectMany(document => document.Json["streets"]?.AsArray()
                ?? throw new InvalidDataException($"Street array missing in '{document.Path}'."))
            .Select(street => street?["osmId"]?.GetValue<long>()
                ?? throw new InvalidDataException("Street record has no OSM way ID."))
            .ToHashSet();
        var reverseOnlyWayIds = BackgroundStreetExtractor.FindReverseOnewayWays(directionPbf, wantedWayIds);
        var directionsJsonOptions = new JsonSerializerOptions { WriteIndented = true };
        foreach (var document in documents)
        {
            if (document.Json["sourcePbfFilename"]?.GetValue<string>() is { } sourcePbf
                && !string.Equals(sourcePbf, Path.GetFileName(directionPbf), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Street sidecar '{document.Path}' was generated from '{sourcePbf}', not '{Path.GetFileName(directionPbf)}'.");
            foreach (var street in document.Json["streets"]!.AsArray())
            {
                var id = street!["osmId"]!.GetValue<long>();
                street["onewayDirection"] = JsonValue.Create(reverseOnlyWayIds.Contains(id) ? -1 : 1);
            }
            await File.WriteAllTextAsync(document.Path, document.Json.ToJsonString(directionsJsonOptions));
        }
        Console.WriteLine($"Updated one-way direction metadata for {wantedWayIds.Count} street ways ({reverseOnlyWayIds.Count} reverse-only) across {files.Length} sidecars.");
        return 0;
    }
    if (args[0].Equals("refresh-elevation", StringComparison.OrdinalIgnoreCase))
    {
        var elevationOptions = ParseOptions(args.Skip(1).ToArray());
        if (!elevationOptions.TryGetValue("--config", out var elevationConfigPath))
            throw new ArgumentException("--config is required.");
        if (!elevationOptions.TryGetValue("--output", out var elevationOutputDirectory))
            throw new ArgumentException("--output is required.");
        var elevationConfig = JsonSerializer.Deserialize<RouteAuthoringConfig>(
            await File.ReadAllTextAsync(elevationConfigPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("Could not read route config JSON.");
        elevationConfig.Validate();
        if (elevationConfig.Elevation is not { Enabled: true } elevationBuildOptions)
            throw new InvalidDataException("The route config does not enable elevation generation.");
        await ElevationPipelineRunner.RunAsync(elevationOutputDirectory, elevationConfig.Id, elevationBuildOptions);
        await RoutePackageMetadata.RefreshAsync(elevationOutputDirectory, elevationConfig);
        Console.WriteLine($"Refreshed elevation profile for {elevationConfig.Id} without rebuilding route geometry.");
        return 0;
    }
    if (!args[0].Equals("build", StringComparison.OrdinalIgnoreCase))
        throw new ArgumentException($"Unknown command '{args[0]}'.");
    var options = ParseOptions(args.Skip(1).ToArray());
    if (!options.TryGetValue("--pbf", out var pbf)) throw new ArgumentException("--pbf is required (PBF or .osm XML extract).");
    if (!options.TryGetValue("--config", out var configPath)) throw new ArgumentException("--config is required.");
    if (!options.TryGetValue("--output", out var outputDirectory)) throw new ArgumentException("--output is required.");
    var cacheDirectory = options.TryGetValue("--cache-dir", out var cacheArg)
        ? cacheArg.Equals("none", StringComparison.OrdinalIgnoreCase) ? null : cacheArg
        : Path.Combine("routes", "cache");
    var routeOnly = options.TryGetValue("--route-only", out var routeOnlyArg)
        && bool.TryParse(routeOnlyArg, out var parsedRouteOnly) && parsedRouteOnly;

    var config = JsonSerializer.Deserialize<RouteAuthoringConfig>(await File.ReadAllTextAsync(configPath),
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
        ?? throw new InvalidDataException("Could not read route config JSON.");
    if (options.TryGetValue("--max-snap-meters", out var snapText))
        config = new RouteAuthoringConfig
        {
            Id = config.Id, Name = config.Name, Loop = config.Loop, RouteType = config.RouteType,
            Difficulty = config.Difficulty,
            RouteDataVersion = config.RouteDataVersion,
            Environment = config.Environment,
            Discipline = config.Discipline,
            SurfaceSegments = config.SurfaceSegments,
            ApprovedTrailWayIds = config.ApprovedTrailWayIds,
            RestrictToApprovedTrailWayIds = config.RestrictToApprovedTrailWayIds,
            BridgeSections = config.BridgeSections,
            TurnaroundWidthMeters = config.TurnaroundWidthMeters,
            TurnaroundWidthSource = config.TurnaroundWidthSource,
            RoutingMode = config.RoutingMode, PreferredRefs = config.PreferredRefs,
            StartArea = config.StartArea, TurnaroundArea = config.TurnaroundArea,
            StartAreaRadiusMeters = config.StartAreaRadiusMeters,
            TurnaroundAreaRadiusMeters = config.TurnaroundAreaRadiusMeters,
            MaximumConnectorDistanceMeters = config.MaximumConnectorDistanceMeters,
            MinimumPreferredRefPercent = config.MinimumPreferredRefPercent,
            Elevation = config.Elevation,
            Terrain = config.Terrain,
            Buildings = config.Buildings,
            Vegetation = config.Vegetation,
            StreetFurniture = config.StreetFurniture,
            BackgroundStreets = config.BackgroundStreets,
            LegacyReferencePoints = config.LegacyReferencePoints,
            ReferenceDistanceMeters = config.ReferenceDistanceMeters, TargetDistanceMeters = config.TargetDistanceMeters, Bbox = config.Bbox,
            MarginMeters = config.MarginMeters, MaxSnapDistanceMeters = double.Parse(snapText),
            RouteCorridorMeters = config.RouteCorridorMeters, DetourWarningRatio = config.DetourWarningRatio,
            DetourFailureRatio = config.DetourFailureRatio, MaxOutputSpacingMeters = config.MaxOutputSpacingMeters,
            Profile = config.Profile,
            RoadCostMultipliers = config.RoadCostMultipliers, Anchors = config.Anchors
        };
    config.Validate();

    var hasApprovedPoints = options.TryGetValue("--approved-route-points", out var approvedPointsPath);
    var hasApprovedReport = options.TryGetValue("--approved-report", out var approvedReportPath);
    if (hasApprovedPoints != hasApprovedReport)
        throw new ArgumentException("--approved-route-points and --approved-report must be supplied together.");

    Console.WriteLine($"Reading local OSM PBF: {Path.GetFileName(pbf)}");
    GraphBuildStats graphStats;
    RouteBuildResult result;
    double routeSolveSeconds;
    if (hasApprovedPoints)
    {
        (result, graphStats, routeSolveSeconds) = await LoadApprovedRouteAsync(
            approvedPointsPath!, approvedReportPath!, config);
        Console.WriteLine("Using approved route points and report directly; route optimization was skipped.");
    }
    else
    {
        var graph = PbfRoadGraphLoader.Load(pbf, config, cacheDirectory);
        var solveTimer = Stopwatch.StartNew();
        result = RouteSolver.Build(graph.Graph, config);
        solveTimer.Stop();
        routeSolveSeconds = solveTimer.Elapsed.TotalSeconds;
        graphStats = graph.Stats;
    }
    var landmarks = result.IsValid && !routeOnly ? LandmarkExtractor.Extract(pbf, result.RoutePoints) : null;
    BuildingExtractionResult? buildings = result.IsValid && !routeOnly && config.Buildings is { Enabled: true } buildingOptions
        ? BuildingExtractor.Extract(pbf, result.RoutePoints, buildingOptions.SearchRadiusMeters,
            buildingOptions.SimplificationToleranceMeters, buildingOptions.MetersPerLevel,
            buildingOptions.OrdinaryHeightMeters, buildingOptions.SmallBuildingHeightMeters)
        : null;
    if (buildings is not null)
        buildings = await BuildingElevationSampler.SampleAsync(buildings, config.Buildings!.ElevationCacheDirectory);
    var hasVegetationSource = options.TryGetValue("--vegetation-source", out var vegetationSourcePath);
    var vegetationSource = hasVegetationSource ? vegetationSourcePath! : pbf;
    VegetationExtractionResult? vegetation = result.IsValid && (!routeOnly || hasVegetationSource)
        && config.Vegetation is { Enabled: true } vegetationOptions
        ? VegetationExtractor.Extract(vegetationSource, result.RoutePoints, buildings?.Buildings ?? [], landmarks?.Landmarks,
            vegetationOptions.SearchRadiusMeters, vegetationOptions.SimplificationToleranceMeters,
            vegetationOptions.TreeRowSpacingMeters, vegetationOptions.GeneratedTreeCapPerArea,
            vegetationOptions.TotalTreeInstanceCap, config.Id)
        : null;
    if (vegetation is not null)
    {
        Console.WriteLine($"Vegetation extraction: polygons {vegetation.PolygonsIncluded}/{vegetation.PolygonsConsidered}; explicit trees {vegetation.ExplicitTreeCount}; tree rows {vegetation.TreeRowCount} ({vegetation.TreeRowInstances} instances); generated {vegetation.GeneratedTreeCount}; rejected road/building/duplicate/landmark {vegetation.TreesRejectedForRoadProximity}/{vegetation.TreesRejectedForBuildingIntersection}/{vegetation.TreesRejectedAsDuplicates}/{vegetation.TreesRejectedNearLandmarks}");
        vegetation = await VegetationElevationSampler.SampleAsync(vegetation, config.Vegetation!.ElevationCacheDirectory);
    }
    StreetFurnitureExtractionResult? streetFurniture = result.IsValid && !routeOnly && config.StreetFurniture is { Enabled: true } furnitureOptions
        ? StreetFurnitureExtractor.Extract(pbf, result.RoutePoints, furnitureOptions.SearchRadiusMeters)
        : null;
    if (streetFurniture is not null)
    {
        Console.WriteLine($"Street furniture extraction: candidates {streetFurniture.TotalCandidates}, selected {streetFurniture.Items.Count}, inside road ribbon {streetFurniture.ItemsInsideRoadRibbon}");
        streetFurniture = await StreetFurnitureElevationSampler.SampleAsync(streetFurniture, config.StreetFurniture!.ElevationCacheDirectory);
        if (streetFurniture.ElevationSkippedCount > 0)
            Console.WriteLine($"Street furniture omitted {streetFurniture.ElevationSkippedCount} features without cached DEM coverage; no elevations were fabricated.");
    }
    BackgroundStreetExtractionResult? backgroundStreets = result.IsValid && !routeOnly && config.BackgroundStreets is { Enabled: true } streetOptions
        ? BackgroundStreetExtractor.Extract(pbf, result.RoutePoints, streetOptions)
        : null;
    if (result.IsValid && routeOnly && options.TryGetValue("--streets-json", out var streetsJsonPath))
    {
        using var streetsDocument = JsonDocument.Parse(await File.ReadAllTextAsync(streetsJsonPath));
        var root = streetsDocument.RootElement;
        var statistics = root.GetProperty("statistics");
        var streets = root.GetProperty("streets").Deserialize<List<BackgroundStreet>>(
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
        backgroundStreets = new BackgroundStreetExtractionResult(
            statistics.GetProperty("waysConsidered").GetInt32(),
            statistics.GetProperty("waysIncluded").GetInt32(),
            statistics.GetProperty("highwayClassCounts").Deserialize<Dictionary<string, int>>() ?? [],
            statistics.GetProperty("widthSourceCounts").Deserialize<Dictionary<string, int>>() ?? [],
            statistics.GetProperty("namedStreetCount").GetInt32(),
            statistics.GetProperty("totalLengthMeters").GetDouble(),
            statistics.GetProperty("suppressedOverlapMeters").GetDouble(),
            statistics.GetProperty("runtimeSampleCount").GetInt32(), streets);
    }
    if (backgroundStreets is not null)
    {
        Console.WriteLine($"Background streets: candidate ways {backgroundStreets.WaysConsidered}, included pieces {backgroundStreets.WaysIncluded}, total {backgroundStreets.TotalLengthMeters:F1} m, overlap suppressed {backgroundStreets.SuppressedOverlapMeters:F1} m");
        Console.WriteLine($"Background street classes: {string.Join(", ", backgroundStreets.HighwayClassCounts.Select(p=>$"{p.Key}={p.Value}"))}; named {backgroundStreets.NamedStreetCount}; width sources {string.Join(", ", backgroundStreets.WidthSourceCounts.Select(p=>$"{p.Key}={p.Value}"))}; samples {backgroundStreets.RuntimeSampleCount}; extraction/elevation seconds {backgroundStreets.GenerationSeconds:F2}");
        backgroundStreets = await BackgroundStreetElevationSampler.SampleAsync(backgroundStreets, config.BackgroundStreets!.ElevationCacheDirectory);
        if (backgroundStreets.ElevationSkippedStreetCount > 0)
            Console.WriteLine($"Background streets omitted {backgroundStreets.ElevationSkippedStreetCount} fragments containing {backgroundStreets.ElevationSkippedPointCount} unavailable/isolated elevation samples; no heights were fabricated.");
    }
    await BuildOutputs.WriteAsync(outputDirectory, config, pbf, graphStats, result,
        routeSolveSeconds, landmarks, buildings, vegetation, streetFurniture, backgroundStreets,
        bundleBackgroundStreets: !options.ContainsKey("--no-bundle-streets"),
        vegetationSourcePath: vegetation is null ? null : vegetationSource);
    var includeTerrainAssets = !routeOnly || options.ContainsKey("--with-elevation");
    if (includeTerrainAssets && result.IsValid && config.Elevation is { Enabled: true } elevation)
        await ElevationPipelineRunner.RunAsync(outputDirectory, config.Id, elevation);
    if (includeTerrainAssets && result.IsValid && config.Terrain is { Enabled: true } terrain)
        await TerrainPipelineRunner.RunAsync(outputDirectory, config.Id, terrain);
    if (result.IsValid)
        await RoutePackageMetadata.RefreshAsync(outputDirectory, config);
    return result.IsValid ? 0 : 2;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Route build failed: {ex.Message}");
    return 2;
}

static Dictionary<string, string> ParseOptions(string[] args)
{
    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (var i = 0; i < args.Length; i++)
    {
        if (!args[i].StartsWith("--", StringComparison.Ordinal) || i + 1 >= args.Length)
            throw new ArgumentException($"Expected an option and value near '{args[i]}'.");
        result[args[i]] = args[++i];
    }
    return result;
}

static async Task<(RouteBuildResult Result, GraphBuildStats GraphStats, double RouteSolveSeconds)>
    LoadApprovedRouteAsync(string routePointsPath, string reportPath, RouteAuthoringConfig config)
{
    var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
    using var pointsDocument = JsonDocument.Parse(await File.ReadAllTextAsync(routePointsPath));
    var pointsRoot = pointsDocument.RootElement;
    if (!string.Equals(pointsRoot.GetProperty("id").GetString(), config.Id, StringComparison.Ordinal)
        || !pointsRoot.GetProperty("isValid").GetBoolean())
        throw new InvalidDataException("Approved route-points file is invalid or belongs to another route.");
    var points = pointsRoot.GetProperty("points").Deserialize<RoutePointOutput[]>(jsonOptions) ?? [];
    var report = JsonSerializer.Deserialize<BuildReport>(await File.ReadAllTextAsync(reportPath), jsonOptions)
        ?? throw new InvalidDataException("Approved route report is empty.");
    if (!report.IsValid || report.Id != config.Id || points.Length < 2
        || points.Length != report.FinalPointCount
        || Math.Abs(points[^1].DistanceMeters - report.ActualOsmDistanceMeters) > 0.001
        || Math.Abs(pointsRoot.GetProperty("actualOsmDistanceMeters").GetDouble() - report.ActualOsmDistanceMeters) > 0.001)
        throw new InvalidDataException("Approved route-points and report metadata do not agree.");
    if (points[0].DistanceMeters != 0)
        throw new InvalidDataException("Approved route must start at route chainage zero.");
    var sameEndpoints = Math.Abs(points[0].Latitude - points[^1].Latitude) <= 1e-9
        && Math.Abs(points[0].Longitude - points[^1].Longitude) <= 1e-9;
    if (config.RouteType == "pointToPoint" ? sameEndpoints : !sameEndpoints)
        throw new InvalidDataException(config.RouteType == "pointToPoint"
            ? "Approved point-to-point route must have distinct natural endpoints."
            : "Approved route is not closed at its lap seam.");
    if (points.Any(point => !double.IsFinite(point.DistanceMeters + point.Latitude + point.Longitude))
        || points.Zip(points.Skip(1), (a, b) => b.DistanceMeters - a.DistanceMeters)
            .Any(gap => gap <= 0 || gap > config.MaxOutputSpacingMeters + 0.02))
        throw new InvalidDataException("Approved route contains invalid points or exceeds configured sample spacing.");

    var closure = report.Legs.SingleOrDefault(leg => leg.FromAnchor == "Oval closure");
    if (closure is null || !closure.WayIds.SequenceEqual([116726397L, 508089766L]))
        throw new InvalidDataException("Approved route report does not contain the reviewed Cumberland/Wolseley closure.");
    foreach (var exclusion in config.ExcludedClosureEdges)
        if (report.RoadSegments.Any(segment => segment.WayId == exclusion.WayId
            && ((segment.FromNodeId == exclusion.FromNodeId && segment.ToNodeId == exclusion.ToNodeId)
                || (segment.FromNodeId == exclusion.ToNodeId && segment.ToNodeId == exclusion.FromNodeId))))
            throw new InvalidDataException($"Approved route contains excluded OSM edge {exclusion.WayId}:{exclusion.FromNodeId}-{exclusion.ToNodeId}.");

    var centreline = points.Select(point => new GeoPoint(point.Latitude, point.Longitude)).ToArray();
    var result = new RouteBuildResult(centreline, points, report.Anchors, report.Legs,
        report.ActualOsmDistanceMeters, report.MaximumAdjacentPointDistanceMeters,
        report.MaximumSourceOsmNodeSpacingMeters, report.MaximumCorridorDeviationMeters,
        report.SourceOsmNodeCount, Math.Max(0, report.FinalPointCount - report.SourceOsmNodeCount),
        true, report.Warnings, report.Errors, report.RouteRefSummary, report.RoadSegments);
    var stats = new GraphBuildStats(report.RetainedOsmNodeCount, report.EligibleWayCount,
        report.GraphNodeCount, report.GraphEdgeCount, report.Timings.NodeScanSeconds,
        report.Timings.WayScanSeconds, report.Timings.GraphBuildSeconds, report.Timings.CacheHit,
        report.Timings.CacheLoadSeconds, report.Timings.CacheSaveSeconds, report.Timings.CacheBuildSeconds);
    return (result, stats, report.Timings.RouteSolveSeconds);
}

static void PrintHelp() => Console.WriteLine("""
Wodra.RouteBuilder builds offline route centreline files from a local OpenStreetMap PBF.

Usage:
  dotnet run --project tools/Wodra.RouteBuilder -- build --pbf <file.osm.pbf> --config <route.json> --output <directory>
       [--max-snap-meters <metres>] [--cache-dir <directory|none>] [--route-only true] [--streets-json <existing-file>]
       [--approved-route-points <file> --approved-report <file>] [--no-bundle-streets true]
  dotnet run --project tools/Wodra.RouteBuilder -- refresh-package --config <route.json> --output <directory>
  dotnet run --project tools/Wodra.RouteBuilder -- refresh-elevation --config <route.json> --output <directory>
  dotnet run --project tools/Wodra.RouteBuilder -- --help
""");
