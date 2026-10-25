using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Godot;

/// <summary>Loads the exported XC content and its portable sidecars from the extracted Wodra app.</summary>
public partial class PackagedXcRouteAssetsSmokeTest : Node
{
    private const string RouteId = "wodra-xc-skills-loop";
    private const string ResourcePrefix = "Wodra.GameData.Routes." + RouteId + ".";

    public override void _Ready()
    {
        try
        {
            var userArguments = OS.GetCmdlineUserArgs();
            if (!UserDataIsolationGuard.TryReadRequiredRoot(userArguments, out var requiredRoot, out var isolationError)
                || string.IsNullOrWhiteSpace(requiredRoot))
                throw new InvalidDataException("A disposable user-data root is required before packaged XC assets are loaded. " + isolationError);
            var userDataDirectory = OS.GetUserDataDir();
            if (!UserDataIsolationGuard.IsInsideRequiredRoot(userDataDirectory, requiredRoot))
                throw new InvalidDataException($"Godot resolved user:// outside the disposable test root: {userDataDirectory}");
            GD.Print($"USER DATA ISOLATION VERIFIED: user:// = {userDataDirectory}");

            var route = RouteCatalog.GetById(RouteId)
                ?? throw new InvalidDataException("The packaged application did not load the fictional XC route.");
            if (!route.Capabilities.SupportsTrailFeatures || route.TrailFeatures.Count != 3
                || Math.Abs(route.DistanceMeters - 1600) > 0.1)
                throw new InvalidDataException("The packaged XC route definition or feature sidecar is incomplete.");

            var assembly = typeof(RouteCatalog).Assembly;
            var terrain = RouteTerrainDataLoader.TryLoadEmbedded(assembly, ResourcePrefix + "terrain.json", RouteId)
                ?? throw new InvalidDataException("The exported XC terrain resource was not loadable.");
            var vegetation = RouteVegetationDataLoader.LoadEmbedded(assembly, ResourcePrefix + "vegetation.json", RouteId);
            var features = TrailFeatureDataLoader.Load(assembly, ResourcePrefix + "trail-features.json", RouteId,
                route.DistanceMeters);
            if (terrain.Chunks.Length == 0 || vegetation.Trees.Count == 0
                || features.Features.Length != 3 || features.Berms.Length != 3 || features.Rollers.Length != 2)
                throw new InvalidDataException("An exported XC terrain, vegetation, or feature sidecar was empty or incomplete.");

            var packageDirectory = Path.Combine(RoutePackageFiles.PackagedPackagesDirectory, RouteId);
            var routeFiles = new[] { "route-points", "terrain", "vegetation", "trail-features" }
                .Select(kind => Path.Combine(packageDirectory, $"{RouteId}.{kind}.json")).ToArray();
            if (routeFiles.Any(path => !File.Exists(path)))
                throw new InvalidDataException("The extracted portable Routes folder is missing an XC sidecar.");

            var manifestPath = Path.Combine(RoutePackageFiles.PackagedPackagesDirectory, "builtin-route-manifest.json");
            using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
            var packagedEntry = manifest.RootElement.GetProperty("Routes").EnumerateArray()
                .SingleOrDefault(item => item.GetProperty("Id").GetString() == RouteId);
            if (packagedEntry.ValueKind != JsonValueKind.Object
                || packagedEntry.GetProperty("GeometryFingerprint").GetString() != route.GeometryFingerprint)
                throw new InvalidDataException("The extracted portable route manifest has no matching XC geometry entry.");

            using var routePoints = JsonDocument.Parse(File.ReadAllText(routeFiles[0]));
            if (routePoints.RootElement.GetProperty("id").GetString() != RouteId
                || routePoints.RootElement.GetProperty("geometryFingerprint").GetString() != route.GeometryFingerprint)
                throw new InvalidDataException("The portable XC route-points sidecar does not match its manifest geometry.");

            GD.Print($"[PackagedXcRouteAssetsSmoke] PASS: extracted Wodra.exe loaded XC route geometry, manifest, {terrain.Chunks.Length} terrain chunks, {vegetation.Trees.Count} trees, and {features.Features.Length} line choices.");
            GetTree().Quit(0);
        }
        catch (Exception error)
        {
            GD.PushError($"[PackagedXcRouteAssetsSmoke] FAIL: {error}");
            GetTree().Quit(1);
        }
    }
}
