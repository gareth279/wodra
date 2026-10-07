using System.Diagnostics;
using System.Text.Json;
using GWift.RouteBuilder;

if (args.Length == 0 || args.Contains("--help") || args.Contains("-h"))
{
    PrintHelp();
    return args.Length == 0 ? 1 : 0;
}

try
{
    if (!args[0].Equals("build", StringComparison.OrdinalIgnoreCase))
        throw new ArgumentException($"Unknown command '{args[0]}'.");
    var options = ParseOptions(args.Skip(1).ToArray());
    if (!options.TryGetValue("--pbf", out var pbf)) throw new ArgumentException("--pbf is required.");
    if (!options.TryGetValue("--config", out var configPath)) throw new ArgumentException("--config is required.");
    if (!options.TryGetValue("--output", out var outputDirectory)) throw new ArgumentException("--output is required.");
    var cacheDirectory = options.TryGetValue("--cache-dir", out var cacheArg)
        ? cacheArg.Equals("none", StringComparison.OrdinalIgnoreCase) ? null : cacheArg
        : Path.Combine("routes", "cache");

    var config = JsonSerializer.Deserialize<RouteAuthoringConfig>(await File.ReadAllTextAsync(configPath),
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
        ?? throw new InvalidDataException("Could not read route config JSON.");
    if (options.TryGetValue("--max-snap-meters", out var snapText))
        config = new RouteAuthoringConfig
        {
            Id = config.Id, Name = config.Name, Loop = config.Loop,
            RoutingMode = config.RoutingMode, PreferredRefs = config.PreferredRefs,
            StartArea = config.StartArea, TurnaroundArea = config.TurnaroundArea,
            StartAreaRadiusMeters = config.StartAreaRadiusMeters,
            TurnaroundAreaRadiusMeters = config.TurnaroundAreaRadiusMeters,
            MaximumConnectorDistanceMeters = config.MaximumConnectorDistanceMeters,
            MinimumPreferredRefPercent = config.MinimumPreferredRefPercent,
            Elevation = config.Elevation,
            Buildings = config.Buildings,
            Vegetation = config.Vegetation,
            StreetFurniture = config.StreetFurniture,
            BackgroundStreets = config.BackgroundStreets,
            LegacyReferencePoints = config.LegacyReferencePoints,
            ReferenceDistanceMeters = config.ReferenceDistanceMeters, Bbox = config.Bbox,
            MarginMeters = config.MarginMeters, MaxSnapDistanceMeters = double.Parse(snapText),
            RouteCorridorMeters = config.RouteCorridorMeters, DetourWarningRatio = config.DetourWarningRatio,
            DetourFailureRatio = config.DetourFailureRatio, MaxOutputSpacingMeters = config.MaxOutputSpacingMeters,
            Profile = config.Profile,
            RoadCostMultipliers = config.RoadCostMultipliers, Anchors = config.Anchors
        };
    config.Validate();

    Console.WriteLine($"Reading local OSM PBF: {Path.GetFileName(pbf)}");
    var graph = PbfRoadGraphLoader.Load(pbf, config, cacheDirectory);
    var solveTimer = Stopwatch.StartNew();
    var result = RouteSolver.Build(graph.Graph, config);
    solveTimer.Stop();
    var landmarks = result.IsValid ? LandmarkExtractor.Extract(pbf, result.RoutePoints) : null;
    BuildingExtractionResult? buildings = result.IsValid && config.Buildings is { Enabled: true } buildingOptions
        ? BuildingExtractor.Extract(pbf, result.RoutePoints, buildingOptions.SearchRadiusMeters,
            buildingOptions.SimplificationToleranceMeters, buildingOptions.MetersPerLevel,
            buildingOptions.OrdinaryHeightMeters, buildingOptions.SmallBuildingHeightMeters)
        : null;
    if (buildings is not null)
        buildings = await BuildingElevationSampler.SampleAsync(buildings, config.Buildings!.ElevationCacheDirectory);
    VegetationExtractionResult? vegetation = result.IsValid && config.Vegetation is { Enabled: true } vegetationOptions
        ? VegetationExtractor.Extract(pbf, result.RoutePoints, buildings?.Buildings ?? [], landmarks?.Landmarks,
            vegetationOptions.SearchRadiusMeters, vegetationOptions.SimplificationToleranceMeters,
            vegetationOptions.TreeRowSpacingMeters, vegetationOptions.GeneratedTreeCapPerArea,
            vegetationOptions.TotalTreeInstanceCap, config.Id)
        : null;
    if (vegetation is not null)
    {
        Console.WriteLine($"Vegetation extraction: polygons {vegetation.PolygonsIncluded}/{vegetation.PolygonsConsidered}; explicit trees {vegetation.ExplicitTreeCount}; tree rows {vegetation.TreeRowCount} ({vegetation.TreeRowInstances} instances); generated {vegetation.GeneratedTreeCount}; rejected road/building/duplicate/landmark {vegetation.TreesRejectedForRoadProximity}/{vegetation.TreesRejectedForBuildingIntersection}/{vegetation.TreesRejectedAsDuplicates}/{vegetation.TreesRejectedNearLandmarks}");
        vegetation = await VegetationElevationSampler.SampleAsync(vegetation, config.Vegetation!.ElevationCacheDirectory);
    }
    StreetFurnitureExtractionResult? streetFurniture = result.IsValid && config.StreetFurniture is { Enabled: true } furnitureOptions
        ? StreetFurnitureExtractor.Extract(pbf, result.RoutePoints, furnitureOptions.SearchRadiusMeters)
        : null;
    if (streetFurniture is not null)
    {
        Console.WriteLine($"Street furniture extraction: candidates {streetFurniture.TotalCandidates}, selected {streetFurniture.Items.Count}, inside road ribbon {streetFurniture.ItemsInsideRoadRibbon}");
        streetFurniture = await StreetFurnitureElevationSampler.SampleAsync(streetFurniture, config.StreetFurniture!.ElevationCacheDirectory);
    }
    BackgroundStreetExtractionResult? backgroundStreets = result.IsValid && config.BackgroundStreets is { Enabled: true } streetOptions
        ? BackgroundStreetExtractor.Extract(pbf, result.RoutePoints, streetOptions)
        : null;
    if (backgroundStreets is not null)
    {
        Console.WriteLine($"Background streets: candidate ways {backgroundStreets.WaysConsidered}, included pieces {backgroundStreets.WaysIncluded}, total {backgroundStreets.TotalLengthMeters:F1} m, overlap suppressed {backgroundStreets.SuppressedOverlapMeters:F1} m");
        Console.WriteLine($"Background street classes: {string.Join(", ", backgroundStreets.HighwayClassCounts.Select(p=>$"{p.Key}={p.Value}"))}; named {backgroundStreets.NamedStreetCount}; width sources {string.Join(", ", backgroundStreets.WidthSourceCounts.Select(p=>$"{p.Key}={p.Value}"))}; samples {backgroundStreets.RuntimeSampleCount}; extraction/elevation seconds {backgroundStreets.GenerationSeconds:F2}");
        backgroundStreets = await BackgroundStreetElevationSampler.SampleAsync(backgroundStreets, config.BackgroundStreets!.ElevationCacheDirectory);
    }
    await BuildOutputs.WriteAsync(outputDirectory, config, pbf, graph.Stats, result,
        solveTimer.Elapsed.TotalSeconds, landmarks, buildings, vegetation, streetFurniture, backgroundStreets);
    if (result.IsValid && config.Elevation is { Enabled: true } elevation)
        await ElevationPipelineRunner.RunAsync(outputDirectory, config.Id, elevation);
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

static void PrintHelp() => Console.WriteLine("""
GWift.RouteBuilder builds offline route centreline files from a local OpenStreetMap PBF.

Usage:
  dotnet run --project tools/GWift.RouteBuilder -- build --pbf <file.osm.pbf> --config <route.json> --output <directory>
       [--max-snap-meters <metres>] [--cache-dir <directory|none>]
  dotnet run --project tools/GWift.RouteBuilder -- --help
""");
