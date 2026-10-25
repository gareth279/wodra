using System.Text.Json.Serialization;

namespace Wodra.RouteBuilder;

public sealed record GeoPoint(double Latitude, double Longitude);
public sealed record RouteAnchor(string Name, double SourceDistanceMeters, double Latitude, double Longitude);
public sealed record BoundingBox(double MinLatitude, double MinLongitude, double MaxLatitude, double MaxLongitude);
public sealed record ClosureEdgeExclusion(long WayId, long FromNodeId, long ToNodeId, string Reason)
{
    public bool Matches(RoadEdge edge) => edge.WayId == WayId
        && ((edge.FromNodeId == FromNodeId && edge.ToNodeId == ToNodeId)
            || (edge.FromNodeId == ToNodeId && edge.ToNodeId == FromNodeId));
}
public sealed record RouteSurfaceAuthoringSegment(double StartMeters, double EndMeters, string Surface, long SourceOsmWayId);
public sealed record RouteBridgeAuthoringSegment(double StartMeters, double EndMeters, long SourceOsmWayId);

public sealed class RouteAuthoringConfig
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public bool Loop { get; init; }
    /// <summary>Optional explicit catalogue type: loop or outAndBack. Blank preserves legacy Loop behaviour.</summary>
    public string RouteType { get; init; } = "";
    public string Difficulty { get; init; } = "";
    public int RouteDataVersion { get; init; } = 1;
    /// <summary>Environment policy and riding discipline are independent authoring concepts.</summary>
    public string Environment { get; init; } = "Road";
    public string Discipline { get; init; } = "Road";
    public List<RouteSurfaceAuthoringSegment> SurfaceSegments { get; init; } = [];
    public List<RouteBridgeAuthoringSegment> BridgeSections { get; init; } = [];
    /// <summary>Optional explicit usable road width for generated U-turns; never inferred from GPS data.</summary>
    public double? TurnaroundWidthMeters { get; init; }
    public string TurnaroundWidthSource { get; init; } = "";
    [JsonIgnore]
    public bool IsClosedRoute => Loop || RouteType is "loop" or "outAndBack";
    public string RoutingMode { get; init; } = "anchors";
    public List<string> PreferredRefs { get; init; } = [];
    public List<ClosureEdgeExclusion> ExcludedClosureEdges { get; init; } = [];
    public GeoPoint? StartArea { get; init; }
    public GeoPoint? TurnaroundArea { get; init; }
    public double StartAreaRadiusMeters { get; init; } = 250;
    public double TurnaroundAreaRadiusMeters { get; init; } = 300;
    public double MaximumConnectorDistanceMeters { get; init; } = 350;
    public double MinimumPreferredRefPercent { get; init; } = 90;
    public ElevationBuildOptions? Elevation { get; init; }
    public TerrainBuildOptions? Terrain { get; init; }
    public BuildingBuildOptions? Buildings { get; init; } = new();
    public VegetationBuildOptions? Vegetation { get; init; } = new();
    public StreetFurnitureBuildOptions? StreetFurniture { get; init; } = new();
    public BackgroundStreetBuildOptions? BackgroundStreets { get; init; } = new();
    public List<RouteAnchor> LegacyReferencePoints { get; init; } = [];
    public double? ReferenceDistanceMeters { get; init; }
    /// <summary>Optional target for ranking valid loop candidates; it never rescales route geometry.</summary>
    public double? TargetDistanceMeters { get; init; }
    public BoundingBox Bbox { get; init; } = new(0, 0, 0, 0);
    public double MarginMeters { get; init; } = 300;
    public double MaxSnapDistanceMeters { get; init; } = 150;
    public double RouteCorridorMeters { get; init; } = 100;
    public double DetourWarningRatio { get; init; } = 1.6;
    public double DetourFailureRatio { get; init; } = 2.2;
    public double MaxOutputSpacingMeters { get; init; } = 30;
    public string Profile { get; init; } = "road";
    public Dictionary<string, double> RoadCostMultipliers { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public List<RouteAnchor> Anchors { get; init; } = [];

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Id)) throw new InvalidDataException("Config id is required.");
        if (Id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || Id.Contains('/') || Id.Contains('\\'))
            throw new InvalidDataException("Config id must be a simple filename-safe identifier.");
        if (string.IsNullOrWhiteSpace(Name)) throw new InvalidDataException("Config name is required.");
        if (RouteDataVersion <= 0) throw new InvalidDataException("routeDataVersion must be positive.");
        if (Environment is not ("Road" or "Trail" or "Velodrome" or "MountainBike"))
            throw new InvalidDataException("environment must be Road, Trail, Velodrome, or MountainBike.");
        if (Discipline is not ("Road" or "Gravel" or "MountainBike" or "Track"))
            throw new InvalidDataException("discipline must be Road, Gravel, MountainBike, or Track.");
        if (SurfaceSegments.Any(segment => !double.IsFinite(segment.StartMeters + segment.EndMeters)
            || segment.StartMeters < 0 || segment.EndMeters <= segment.StartMeters
            || string.IsNullOrWhiteSpace(segment.Surface) || segment.SourceOsmWayId <= 0))
            throw new InvalidDataException("Surface sections need finite positive chainage, a surface label, and a source OSM way ID.");
        for (var i = 1; i < SurfaceSegments.Count; i++)
            if (Math.Abs(SurfaceSegments[i].StartMeters - SurfaceSegments[i - 1].EndMeters) > 0.05)
                throw new InvalidDataException("Surface sections must be ordered and contiguous.");
        if (BridgeSections.Any(section => !double.IsFinite(section.StartMeters + section.EndMeters)
            || section.StartMeters < 0 || section.EndMeters <= section.StartMeters || section.SourceOsmWayId <= 0))
            throw new InvalidDataException("Bridge sections need finite route chainage and a positive source OSM way ID.");
        if (TurnaroundWidthMeters is { } turnaroundWidth
            && (!double.IsFinite(turnaroundWidth) || turnaroundWidth < 3 || turnaroundWidth > 40
                || string.IsNullOrWhiteSpace(TurnaroundWidthSource)))
            throw new InvalidDataException("A turnaround width must be 3..40 m and include explicit width provenance.");
        if (TurnaroundWidthMeters is null && !string.IsNullOrWhiteSpace(TurnaroundWidthSource))
            throw new InvalidDataException("turnaroundWidthSource requires turnaroundWidthMeters.");
        if (Bbox.MinLatitude >= Bbox.MaxLatitude || Bbox.MinLongitude >= Bbox.MaxLongitude)
            throw new InvalidDataException("bbox minimum values must be less than maximum values.");
        if (MarginMeters < 0 || MaxSnapDistanceMeters <= 0 || RouteCorridorMeters <= 0 || MaxOutputSpacingMeters <= 0)
            throw new InvalidDataException("Margin must be non-negative and snap, corridor, and output spacing must be positive.");
        if (ReferenceDistanceMeters is <= 0) throw new InvalidDataException("referenceDistanceMeters must be positive when provided.");
        if (TargetDistanceMeters is { } target && (!double.IsFinite(target) || target <= 0))
            throw new InvalidDataException("targetDistanceMeters must be finite and positive when provided.");
        if (!double.IsFinite(DetourWarningRatio) || !double.IsFinite(DetourFailureRatio)
            || DetourWarningRatio < 1 || DetourFailureRatio <= DetourWarningRatio)
            throw new InvalidDataException("Detour thresholds must satisfy 1 <= warning < failure.");
        if (Profile is not ("road" or "cycling")) throw new InvalidDataException("profile must be 'road' or 'cycling'.");
        if (RoadCostMultipliers.Any(pair => string.IsNullOrWhiteSpace(pair.Key)
            || !double.IsFinite(pair.Value) || pair.Value < 1))
            throw new InvalidDataException("Road cost multipliers must be finite values greater than or equal to 1.");
        if (ExcludedClosureEdges.Any(edge => edge.WayId <= 0 || edge.FromNodeId <= 0 || edge.ToNodeId <= 0
            || edge.FromNodeId == edge.ToNodeId || string.IsNullOrWhiteSpace(edge.Reason)))
            throw new InvalidDataException("Excluded closure edges require a positive OSM way ID, two distinct node IDs, and a reason.");
        if (RoutingMode is not ("anchors" or "roadRef")) throw new InvalidDataException("routingMode must be 'anchors' or 'roadRef'.");
        if (RouteType is not ("" or "loop" or "outAndBack" or "pointToPoint"))
            throw new InvalidDataException("routeType must be 'loop', 'outAndBack', or 'pointToPoint'.");
        if (Difficulty is not ("" or "Unrated" or "Easy" or "Moderate" or "Challenging"))
            throw new InvalidDataException("difficulty must be Unrated, Easy, Moderate, or Challenging when provided.");
        if (Loop && RouteType is not ("" or "loop"))
            throw new InvalidDataException("The legacy loop flag can only be combined with routeType 'loop'.");
        Elevation?.Validate();
        Terrain?.Validate(Elevation);
        Buildings?.Validate();
        Vegetation?.Validate();
        StreetFurniture?.Validate();
        BackgroundStreets?.Validate();
        if (RoutingMode == "roadRef")
        {
            if (!(Loop || RouteType == "loop") || PreferredRefs.Count == 0 || StartArea is null || TurnaroundArea is null)
                throw new InvalidDataException("roadRef mode requires a loop, preferredRefs, startArea, and turnaroundArea.");
            if (StartAreaRadiusMeters <= 0 || TurnaroundAreaRadiusMeters <= 0 || MaximumConnectorDistanceMeters <= 0
                || MinimumPreferredRefPercent is < 0 or > 100)
                throw new InvalidDataException("roadRef area radii/connector distance must be positive and preferred-ref percentage must be 0..100.");
            if (PreferredRefs.Any(string.IsNullOrWhiteSpace)) throw new InvalidDataException("preferredRefs cannot contain empty values.");
            if (LegacyReferencePoints.Any(a => string.IsNullOrWhiteSpace(a.Name) || !double.IsFinite(a.SourceDistanceMeters)
                || a.Latitude is < -90 or > 90 || a.Longitude is < -180 or > 180))
                throw new InvalidDataException("Legacy reference points must have valid names and coordinates.");
            return;
        }
        if (TargetDistanceMeters is not null)
            throw new InvalidDataException("targetDistanceMeters candidate ranking is currently supported only by roadRef loop generation.");
        if (Anchors.Count < 2) throw new InvalidDataException("At least two ordered anchors are required.");
        if (Anchors.Any(a => string.IsNullOrWhiteSpace(a.Name) || !double.IsFinite(a.SourceDistanceMeters)
            || a.Latitude is < -90 or > 90 || a.Longitude is < -180 or > 180))
            throw new InvalidDataException("Each anchor needs a name and valid latitude/longitude.");
        if (Anchors[0].SourceDistanceMeters != 0) throw new InvalidDataException("The first anchor source distance must be zero.");
        for (var i = 1; i < Anchors.Count; i++)
            if (Anchors[i].SourceDistanceMeters <= Anchors[i - 1].SourceDistanceMeters)
                throw new InvalidDataException("Anchor distances must increase strictly in route order.");
        if (ReferenceDistanceMeters is { } reference && Math.Abs(Anchors[^1].SourceDistanceMeters - reference) > 0.001)
            throw new InvalidDataException("The final anchor source distance must equal referenceDistanceMeters.");
        if (IsClosedRoute && (Math.Abs(Anchors[0].Latitude - Anchors[^1].Latitude) > 0.00001
            || Math.Abs(Anchors[0].Longitude - Anchors[^1].Longitude) > 0.00001))
            throw new InvalidDataException("For loop and out-and-back routes, the final anchor must close onto the first anchor.");
        if (RouteType == "outAndBack")
        {
            if (Anchors.Count < 3 || Anchors.Count % 2 == 0)
                throw new InvalidDataException("An out-and-back route requires an odd number of anchors including a single turnaround anchor.");
            for (var i = 0; i < Anchors.Count / 2; i++)
            {
                var outward = Anchors[i];
                var returning = Anchors[Anchors.Count - 1 - i];
                if (Math.Abs(outward.Latitude - returning.Latitude) > 0.0000001
                    || Math.Abs(outward.Longitude - returning.Longitude) > 0.0000001)
                    throw new InvalidDataException($"Out-and-back anchors must mirror the outward road path; '{outward.Name}' does not match '{returning.Name}'.");
            }
        }
    }
}

public sealed class TerrainBuildOptions
{
    public bool Enabled { get; init; }
    public double NearRadiusMeters { get; init; } = 250;
    public double FarRadiusMeters { get; init; } = 750;
    public double SpacingMeters { get; init; } = 10;
    public double FarSpacingMeters { get; init; } = 25;
    public double CorridorRadiusMeters { get; init; } = 250;
    public double ChunkSizeMeters { get; init; } = 250;
    public double FarChunkSizeMeters { get; init; } = 500;
    public double TransitionOverlapMeters { get; init; } = 10;
    public double FarVerticalBiasMeters { get; init; } = 0.15;
    public double VerticalBiasMeters { get; init; } = 0.08;
    public double RoadClearanceMeters { get; init; } = 0.15;
    public double RoadCorridorRadiusMeters { get; init; } = 5;
    public string ElevationCacheDirectory { get; init; } = "routes/cache/elevation";

    public void Validate(ElevationBuildOptions? elevation)
    {
        if (!Enabled) return;
        if (elevation is not { Enabled: true })
            throw new InvalidDataException("Terrain requires enabled LINZ route elevation authoring.");
        if (!double.IsFinite(NearRadiusMeters) || NearRadiusMeters <= 0
            || !double.IsFinite(FarRadiusMeters) || FarRadiusMeters <= NearRadiusMeters
            || !double.IsFinite(SpacingMeters) || SpacingMeters < 5 || SpacingMeters > 50
            || !double.IsFinite(FarSpacingMeters) || FarSpacingMeters < SpacingMeters || FarSpacingMeters > 100
            || !double.IsFinite(CorridorRadiusMeters) || CorridorRadiusMeters <= 0 || CorridorRadiusMeters > 1000
            || !double.IsFinite(ChunkSizeMeters) || ChunkSizeMeters < SpacingMeters || ChunkSizeMeters > 1000
            || !double.IsFinite(FarChunkSizeMeters) || FarChunkSizeMeters < FarSpacingMeters || FarChunkSizeMeters > 2000
            || !double.IsFinite(TransitionOverlapMeters) || TransitionOverlapMeters < 0 || TransitionOverlapMeters > NearRadiusMeters / 2
            || !double.IsFinite(FarVerticalBiasMeters) || FarVerticalBiasMeters < 0
            || !double.IsFinite(VerticalBiasMeters) || VerticalBiasMeters < 0
            || !double.IsFinite(RoadClearanceMeters) || RoadClearanceMeters < 0
            || !double.IsFinite(RoadCorridorRadiusMeters) || RoadCorridorRadiusMeters <= 0
            || string.IsNullOrWhiteSpace(ElevationCacheDirectory))
            throw new InvalidDataException("Terrain spacing, corridor, chunk, bias, clearance, or cache settings are invalid.");
    }
}

public sealed class BackgroundStreetBuildOptions
{
    public bool Enabled { get; init; }
    public double SearchRadiusMeters { get; init; } = 200;
    public double RouteOverlapSuppressionMeters { get; init; } = 8;
    public double SampleSpacingMeters { get; init; } = 15;
    public string ElevationCacheDirectory { get; init; } = "routes/cache/elevation";
    public void Validate()
    {
        if (Enabled && (!double.IsFinite(SearchRadiusMeters) || SearchRadiusMeters <= 0 || SearchRadiusMeters > 1000
            || !double.IsFinite(RouteOverlapSuppressionMeters) || RouteOverlapSuppressionMeters < 0
            || !double.IsFinite(SampleSpacingMeters) || SampleSpacingMeters is < 10 or > 20
            || string.IsNullOrWhiteSpace(ElevationCacheDirectory)))
            throw new InvalidDataException("Background street radius, overlap suppression, sample spacing (10..20m), or elevation cache is invalid.");
    }
}

public sealed class StreetFurnitureBuildOptions
{
    public bool Enabled { get; init; }
    public double SearchRadiusMeters { get; init; } = 80;
    public string ElevationCacheDirectory { get; init; } = "routes/cache/elevation";
    public void Validate()
    {
        if (!Enabled) return;
        if (!double.IsFinite(SearchRadiusMeters) || SearchRadiusMeters <= 0 || SearchRadiusMeters > 500
            || string.IsNullOrWhiteSpace(ElevationCacheDirectory))
            throw new InvalidDataException("Street-furniture extraction settings are outside the supported ranges.");
    }
}

public sealed class VegetationBuildOptions
{
    public bool Enabled { get; init; }
    public double SearchRadiusMeters { get; init; } = 200;
    public double SimplificationToleranceMeters { get; init; } = 1;
    public int GeneratedTreeCapPerArea { get; init; } = 150;
    public int TotalTreeInstanceCap { get; init; } = 1200;
    public double TreeRowSpacingMeters { get; init; } = 10;
    public string ElevationCacheDirectory { get; init; } = "routes/cache/elevation";

    public void Validate()
    {
        if (!Enabled) return;
        if (!double.IsFinite(SearchRadiusMeters) || SearchRadiusMeters <= 0
            || !double.IsFinite(SimplificationToleranceMeters) || SimplificationToleranceMeters is < .5 or > 1.5
            || GeneratedTreeCapPerArea <= 0
            || TotalTreeInstanceCap <= 0 || !double.IsFinite(TreeRowSpacingMeters) || TreeRowSpacingMeters is < 8 or > 12
            || string.IsNullOrWhiteSpace(ElevationCacheDirectory))
            throw new InvalidDataException("Vegetation extraction settings are outside the supported ranges.");
    }
}

public sealed class BuildingBuildOptions
{
    public bool Enabled { get; init; }
    public double SearchRadiusMeters { get; init; } = 150;
    public double SimplificationToleranceMeters { get; init; } = .75;
    public double MetersPerLevel { get; init; } = 3;
    public double OrdinaryHeightMeters { get; init; } = 8;
    public double SmallBuildingHeightMeters { get; init; } = 3.5;
    public string ElevationCacheDirectory { get; init; } = "routes/cache/elevation";

    public void Validate()
    {
        if (!Enabled) return;
        if (!double.IsFinite(SearchRadiusMeters) || SearchRadiusMeters <= 0
            || !double.IsFinite(SimplificationToleranceMeters) || SimplificationToleranceMeters < 0
            || !double.IsFinite(MetersPerLevel) || MetersPerLevel <= 0
            || !double.IsFinite(OrdinaryHeightMeters) || OrdinaryHeightMeters <= 0
            || !double.IsFinite(SmallBuildingHeightMeters) || SmallBuildingHeightMeters <= 0
            || string.IsNullOrWhiteSpace(ElevationCacheDirectory))
            throw new InvalidDataException("Building extraction settings must be positive and an elevation cache directory is required.");
    }
}

public sealed class ElevationBuildOptions
{
    public bool Enabled { get; init; }
    public string PrimaryCollectionUrl { get; init; } = "";
    public string? FallbackCollectionUrl { get; init; }
    public double SmoothingWindowMeters { get; init; } = 30;
    public double GradientWindowMeters { get; init; } = 25;
    public double ResampleIntervalMeters { get; init; } = 5;
    public double MaximumBridgeInterpolationMeters { get; init; } = 100;
    public double GradientReviewThresholdPercent { get; init; } = 15;

    public void Validate()
    {
        if (!Enabled) return;
        if (!Uri.TryCreate(PrimaryCollectionUrl, UriKind.Absolute, out var primary) || primary.Scheme != Uri.UriSchemeHttps)
            throw new InvalidDataException("Enabled elevation generation requires an HTTPS primaryCollectionUrl.");
        if (!string.IsNullOrWhiteSpace(FallbackCollectionUrl)
            && (!Uri.TryCreate(FallbackCollectionUrl, UriKind.Absolute, out var fallback) || fallback.Scheme != Uri.UriSchemeHttps))
            throw new InvalidDataException("fallbackCollectionUrl must be an HTTPS STAC collection URL.");
        if (!double.IsFinite(SmoothingWindowMeters) || SmoothingWindowMeters <= 0
            || !double.IsFinite(GradientWindowMeters) || GradientWindowMeters <= 0
            || !double.IsFinite(ResampleIntervalMeters) || ResampleIntervalMeters <= 0
            || !double.IsFinite(MaximumBridgeInterpolationMeters) || MaximumBridgeInterpolationMeters < 0
            || !double.IsFinite(GradientReviewThresholdPercent) || GradientReviewThresholdPercent <= 0)
            throw new InvalidDataException("Elevation smoothing, gradient, resampling, and review settings must be positive.");
    }
}

public sealed record GraphBuildStats(int RetainedNodeCount, int EligibleWayCount, int GraphNodeCount, int GraphEdgeCount,
    double NodeScanSeconds, double WayScanSeconds, double GraphBuildSeconds,
    bool CacheHit = false, double CacheLoadSeconds = 0, double CacheSaveSeconds = 0, double CacheBuildSeconds = 0);

public sealed record SnapCandidate(long FromNodeId, long ToNodeId, long WayId, double EdgeFraction,
    GeoPoint ProjectedCoordinate, double SnapDistanceMeters, double BearingDegrees,
    double DirectionAlignmentDegrees, bool DirectionCompatible, string Highway, string? RoadName, string? RoadRef,
    string? Bridge = null, string? Tunnel = null, string? Layer = null);
public sealed record AnchorSnap(RouteAnchor Anchor, long FromNodeId, long ToNodeId, long WayId,
    GeoPoint SnappedCoordinate, double SnapDistanceMeters, double BearingDegrees,
    double DirectionAlignmentDegrees, string Highway, string? RoadName, string? RoadRef);
public sealed record RoutedLeg(string FromAnchor, string ToAnchor, double StraightLineDistanceMeters, double DistanceMeters,
    double RoadDirectRatio, double MaximumCorridorDeviationMeters, AnchorSnap StartSnap, AnchorSnap EndSnap,
    string Status, IReadOnlyList<long> WayIds, IReadOnlyList<RoadWaySummary> Ways,
    IReadOnlyList<SnapCandidate> AlternativeStartCandidates, IReadOnlyList<SnapCandidate> AlternativeEndCandidates,
    IReadOnlyList<GeoPoint> Centreline, string SectionType = "road");
public sealed record RoadWaySummary(long WayId, string Highway, string? Name, string? Ref,
    string? Bridge = null, string? Tunnel = null, string? Layer = null, string? Oneway = null,
    string? Access = null, string? Bicycle = null, string? Vehicle = null,
    string? MotorVehicle = null, string? Motorcar = null, string? OnewayBicycle = null, string? Surface = null);
public sealed record RoutedRoadSegment(double StartDistanceMeters, double EndDistanceMeters, long WayId,
    string Highway, string? Name, string? Ref, string? Bridge, string? Tunnel, string? Layer,
    long FromNodeId = 0, long ToNodeId = 0, string? Oneway = null, string? Access = null,
    string? Bicycle = null, string? Vehicle = null, string? MotorVehicle = null, string? Motorcar = null,
    string? OnewayBicycle = null, string? Surface = null);
public sealed record NonPreferredConnector(long WayId, string Highway, string? Name, string? Ref,
    double DistanceMeters, string Reason);
public sealed record RouteRefSummary(IReadOnlyList<string> PreferredRefs,
    double NorthboundPreferredDistanceMeters, double SouthboundPreferredDistanceMeters,
    double ConnectorDistanceMeters, double PreferredRefDistanceMeters, double PreferredRefPercent,
    double MinimumPreferredRefPercent, IReadOnlyList<long> WayIds, IReadOnlyList<string> RoadNames,
    IReadOnlyList<NonPreferredConnector> NonPreferredConnectors, bool IsSuspicious);
public sealed record RoutePointOutput(double DistanceMeters, double Latitude, double Longitude,
    string? Bridge = null, string? Tunnel = null, string? Layer = null,
    double? ElevationMeters = null, double? RawElevationMeters = null, double? GradientPercent = null);
public sealed record RouteBuildResult(IReadOnlyList<GeoPoint> Centreline, IReadOnlyList<RoutePointOutput> RoutePoints,
    IReadOnlyList<AnchorSnap> Snaps, IReadOnlyList<RoutedLeg> Legs, double RawDistanceMeters,
    double MaximumAdjacentDistanceMeters, double MaximumSourceNodeSpacingMeters,
    double MaximumCorridorDeviationMeters, int SourceOsmNodeCount,
    int DensifiedPointCount, bool IsValid, IReadOnlyList<string> Warnings, IReadOnlyList<string> Errors,
    RouteRefSummary? RouteRefSummary = null,
    IReadOnlyList<RoutedRoadSegment>? RoadSegments = null,
    IReadOnlyList<GeneratedTurnaround>? Turnarounds = null);

public sealed record BuildTimings(double NodeScanSeconds, double WayScanSeconds, double GraphBuildSeconds,
    double RouteSolveSeconds, double OutputGenerationSeconds, double CacheLoadSeconds = 0, double CacheSaveSeconds = 0,
    bool CacheHit = false, double CacheBuildSeconds = 0);

public sealed class BuildReport
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Source { get; init; } = "OpenStreetMap";
    public string SourcePbfFilename { get; init; } = "";
    public string RoutingMode { get; init; } = "anchors";
    public string RouteType { get; init; } = "";
    public string Difficulty { get; init; } = "";
    public int RouteDataVersion { get; init; } = 1;
    public string GeometryFingerprint { get; init; } = "";
    public IReadOnlyList<string> PreferredRefs { get; init; } = [];
    public BoundingBox BoundingBox { get; init; } = new(0, 0, 0, 0);
    public string Profile { get; init; } = "road";
    public double RouteCorridorMeters { get; init; }
    public double DetourWarningRatio { get; init; }
    public double DetourFailureRatio { get; init; }
    public double MaxOutputSpacingMeters { get; init; }
    public int RetainedOsmNodeCount { get; init; }
    public int EligibleWayCount { get; init; }
    public int GraphNodeCount { get; init; }
    public int GraphEdgeCount { get; init; }
    public IReadOnlyList<AnchorSnap> Anchors { get; init; } = [];
    public IReadOnlyList<RoutedLeg> Legs { get; init; } = [];
    public RouteRefSummary? RouteRefSummary { get; init; }
    public IReadOnlyList<RoutedRoadSegment> RoadSegments { get; init; } = [];
    public double? TurnaroundWidthMeters { get; init; }
    public string TurnaroundWidthSource { get; init; } = "";
    public IReadOnlyList<GeneratedTurnaround> GeneratedTurnarounds { get; init; } = [];
    public double SourceNetworkDistanceMeters { get; init; }
    public IReadOnlyList<ClosureEdgeExclusion> ExcludedClosureEdges { get; init; } = [];
    public int NearbyLandmarkCandidatesConsidered { get; init; }
    public int SelectedLandmarkCount { get; init; }
    public BuildingReport? Buildings { get; init; }
    public VegetationReport? Vegetation { get; init; }
    public StreetFurnitureReport? StreetFurniture { get; init; }
    public double ActualOsmDistanceMeters { get; init; }
    public double? ReferenceDistanceMeters { get; init; }
    public double? TargetDistanceMeters { get; init; }
    public double? TargetDistanceDifferenceMeters { get; init; }
    public double? ReferenceDifferenceMeters { get; init; }
    public double? ActualToReferenceRatio { get; init; }
    public int FinalPointCount { get; init; }
    public int SourceOsmNodeCount { get; init; }
    public double MaximumAdjacentPointDistanceMeters { get; init; }
    public double MaximumSourceOsmNodeSpacingMeters { get; init; }
    public double MaximumCorridorDeviationMeters { get; init; }
    public bool IsValid { get; init; }
    public IReadOnlyList<string> Errors { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public BuildTimings Timings { get; init; } = new(0, 0, 0, 0, 0);
    public DateTimeOffset GeneratedAtUtc { get; init; }
}

public sealed record BuildingReport(int NearbyCandidates, int SelectedCount, int OrdinaryWays,
    int MultipolygonRelationsIncluded, int MultipolygonRelationsSkipped, int NamedBuildings,
    int ExplicitHeightCount, int LevelsHeightCount, int FallbackHeightCount, int MalformedHeights,
    int MalformedOrExcludedRecords, int OriginalVertices, int SimplifiedVertices, int LargestFootprintVertices,
    double MinimumHeightMeters, double MaximumHeightMeters, double MedianHeightMeters,
    int GeneratedTriangleCount, int RuntimeMeshChunkCount, int RuntimeMeshInstanceCount,
    double ExtractionSeconds, string SourcePbfFilename)
{
    public int ElevationSkippedCount { get; init; }
    public IReadOnlyDictionary<string, int> VisualCategoryCounts { get; init; } = new Dictionary<string, int>();
    public IReadOnlyDictionary<string, int> RoofShapeTagCounts { get; init; } = new Dictionary<string, int>();
    public int BuildingsWithRoofMetadata { get; init; }
    public int ExplicitBuildingColourCount { get; init; }
    public int ExplicitRoofColourCount { get; init; }
    public int ExplicitMaterialTagCount { get; init; }
    public int EffectiveFlatRoofCount { get; init; }
    public int RoofedBuildingCount { get; init; }
    public int EffectiveGabledRoofCount { get; init; }
    public int EffectiveHippedRoofCount { get; init; }
    public int FlatRoofFallbackCount { get; init; }
    public int WindowDetailedBuildingCount { get; init; }
    public int VisualFacadeTriangleCount { get; init; }
    public int VisualRoofTriangleCount { get; init; }
    public int VisualWindowTriangleCount { get; init; }
    public int VisualTriangleCount { get; init; }
    public int FacadePaletteVariantCount { get; init; }
    public int RuntimeSharedMaterialCount { get; init; }
    public int VisualRuntimeMeshChunkCount { get; init; }
    public int VisualRuntimeMeshInstanceCount { get; init; }
    public int VisualRuntimeSurfaceCount { get; init; }
    public int FootprintElevationSamples { get; init; }
    public int SlopedFootprintCount { get; init; }
}

public sealed record VegetationReport(int PolygonsConsidered, int PolygonsIncluded,
    IReadOnlyDictionary<string, int> CategoryCounts, int NamedAreas, int ExplicitTreeCount, int TreeRowCount,
    int TreeRowInstances, int GeneratedTreeCount, int TotalTreeInstances, int TreesRejectedForRoadProximity,
    int TreesRejectedForBuildingIntersection, int TreesRejectedAsDuplicates, int TreesRejectedNearLandmarks,
    int OriginalPolygonVertices, int SimplifiedPolygonVertices, int LargestPolygonVertices,
    int DemSamples, int DemCacheHits, int MalformedOrSkippedGeometries, int AreaGroundOverlays,
    int AreaTriangleCount, int MultiMeshCount, int AreaMeshInstanceCount, int SceneNodeCount,
    double GenerationSeconds, string SourcePbfFilename)
{
    public int DemSkippedAreas { get; init; }
    public int DemSkippedTrees { get; init; }
}

public sealed record StreetFurnitureReport(int TotalCandidates, int SelectedItems,
    IReadOnlyDictionary<string, int> CategoryCounts, int StreetLamps, int TrafficSignals, int StopGiveWaySigns,
    int Bollards, int Crossings, int BusStops, int Benches, int MalformedOrSkippedItems,
    int ItemsInsideRoadRibbon, int ElevationSamples, int RuntimeMultiMeshCount,
    int TotalRenderedInstances, int RenderedPrimitiveInstances, int SceneNodeCount, double GenerationSeconds, string SourcePbfFilename)
{
    public int ElevationSkippedCount { get; init; }
}

public static class GeoMath
{
    public const double EarthRadiusMeters = 6_371_000;

    public static double HaversineMeters(GeoPoint a, GeoPoint b)
    {
        const double toRadians = Math.PI / 180;
        var lat1 = a.Latitude * toRadians;
        var lat2 = b.Latitude * toRadians;
        var dLat = (b.Latitude - a.Latitude) * toRadians;
        var dLon = (b.Longitude - a.Longitude) * toRadians;
        var h = Math.Pow(Math.Sin(dLat / 2), 2)
            + Math.Cos(lat1) * Math.Cos(lat2) * Math.Pow(Math.Sin(dLon / 2), 2);
        return 2 * EarthRadiusMeters * Math.Asin(Math.Sqrt(Math.Clamp(h, 0, 1)));
    }

    public static double PointToSegmentMeters(GeoPoint point, GeoPoint start, GeoPoint end)
    {
        var latitude = (start.Latitude + end.Latitude + point.Latitude) / 3 * Math.PI / 180;
        var scaleX = EarthRadiusMeters * Math.Cos(latitude) * Math.PI / 180;
        var scaleY = EarthRadiusMeters * Math.PI / 180;
        var ax = start.Longitude * scaleX;
        var ay = start.Latitude * scaleY;
        var bx = end.Longitude * scaleX;
        var by = end.Latitude * scaleY;
        var px = point.Longitude * scaleX;
        var py = point.Latitude * scaleY;
        var dx = bx - ax;
        var dy = by - ay;
        var denominator = dx * dx + dy * dy;
        var t = denominator > 0 ? Math.Clamp(((px - ax) * dx + (py - ay) * dy) / denominator, 0, 1) : 0;
        return Math.Sqrt(Math.Pow(px - (ax + t * dx), 2) + Math.Pow(py - (ay + t * dy), 2));
    }

    public static double DirectionDifferenceDegrees(GeoPoint from, GeoPoint to, GeoPoint expectedFrom, GeoPoint expectedTo)
    {
        var latitude = (from.Latitude + to.Latitude + expectedFrom.Latitude + expectedTo.Latitude) / 4 * Math.PI / 180;
        var scaleX = Math.Cos(latitude);
        var ax = (to.Longitude - from.Longitude) * scaleX;
        var ay = to.Latitude - from.Latitude;
        var bx = (expectedTo.Longitude - expectedFrom.Longitude) * scaleX;
        var by = expectedTo.Latitude - expectedFrom.Latitude;
        var al = Math.Sqrt(ax * ax + ay * ay);
        var bl = Math.Sqrt(bx * bx + by * by);
        if (al <= 1e-12 || bl <= 1e-12) return 180;
        var cosine = Math.Clamp((ax * bx + ay * by) / (al * bl), -1, 1);
        return Math.Acos(cosine) * 180 / Math.PI;
    }
}
