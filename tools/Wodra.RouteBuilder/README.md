# Wodra Route Builder

This offline .NET 8 command-line tool reads a local OpenStreetMap PBF and writes GeoJSON, Wodra route-points JSON, a diagnostic report, and a self-contained route review HTML file. Generated route-point distances are cumulative OSM road metres. `sourceDistanceMeters` and `referenceDistanceMeters` describe legacy route progress and are metadata only; they never scale generated geometry or distances. PBF reading uses the streaming OsmSharp reader; nodes outside the configured bounding box plus margin are discarded. An optional regional graph cache defaults to `routes/cache` and is keyed by PBF identity, bounding box, margin, and road-filter version.

The default `routingMode: "anchors"` projects ordered GPS anchors to directed roads and routes them in authored order. `routingMode: "roadRef"` supports a loop primarily defined by an OSM road reference: set `preferredRefs`, `startArea`, `turnaroundArea`, the two area radii, a maximum connector length, and a minimum preferred-ref coverage. It derives outward and return paths from directed ways tagged with the configured ref (`trunk` and `trunk_link`), allowing short graph connectors only at the turnaround and loop closure. Optional `legacyReferencePoints` are displayed as a faint historical reference and do not affect route selection. The report and preview identify connector ways and preferred-ref coverage; builds below the configured coverage are rejected as suspicious.

`roadRef` mode ranks its connected candidate loops by `targetDistanceMeters` when configured, then applies the area and connector constraints. This is a target for candidate selection only: every published distance still comes from traversed OSM edges. For more general loops, author area/checkpoint candidates and use the anchor solver's directed connectivity, access filters, corridor limits, and diagnostic report to validate each candidate.

For a road-based out-and-back, set `routeType: "outAndBack"` and provide an odd number of ordered anchors: start, outward checkpoints, one turnaround, then the same checkpoint coordinates in reverse order and the start. Each leg is still solved against permitted directed OSM edges, so a one-way or bicycle-restricted return fails instead of being drawn as a chord. Distance is measured over both directions. Review the lap seam before publishing: a simple retrace has a 180-degree direction reversal at its common start/finish, so use only where the mapped layout makes that turn safe and visually plausible. `routeType: "pointToPoint"` is available for authoring review but is not a repeated-lap route.

Successful route packages include `routeType`, `routeDataVersion`, difficulty, and a SHA-256 fingerprint of final ordered coordinates, chainage, and elevation, refreshed after elevation resampling. Difficulty can be authored or estimated from ascent per kilometre (12 m/km or less Easy; up to 30 Moderate; above 30 Challenging). The fingerprint excludes session progress and presentation metadata. Keep route JSON and generated scenery sidecars together as an independently attributed package. The route selector shows stable ID, route fingerprint, distance, ascent, difficulty, type, and attribution.

To install a reviewed package without rebuilding Wodra, copy `<id>.route-points.json` and any generated `<id>.*.json` scenery sidecars into `%LOCALAPPDATA%\Wodra\Routes\<id>\`. Wodra validates the route ID, loop closure, ordered coordinates, elevation profile, distance metadata, and optional fingerprint at startup, then loads matching scenery sidecars from the same folder. Invalid packages are skipped with a warning in the route-selection screen. The route folder should include its report and attribution documentation when redistributing; the report itself is not needed at runtime.

Successful route builds also scan the local PBF for named, distinctive OSM landmarks within 150 m of the generated centreline. The ranked, deduplicated candidate list is written to `<route-id>.landmarks.json` and displayed as clickable markers in the route preview. Review this file and map before copying it into `src/IndoorCycling.Game/Data/Routes/`; the game embeds only the small candidate metadata and remains offline. The runtime renders simple proxies within 700 m of the rider and labels within 220 m.

Set `buildings.enabled` in a route config to extract building-tagged closed ways and supported single outer-ring multipolygons whose **footprints** fall within `buildings.searchRadiusMeters` (default 150 m) of the route. Building footprints are simplified at the configured metre tolerance, use bounded OSM height parsing, and receive base elevations from existing cached LINZ DEM rasters. The builder writes `<route-id>.buildings.json` and adds an optional Buildings layer to the route preview. Dunedin's generated bundle is included with the game; runtime extrusions use the same `VisualRoute` coordinate mapping as the road and landmarks and are batched in 350 m route chunks. Multipolygons with inner rings or incomplete outer geometry near the route are reported as skipped; no missing geometry is fabricated.

Obtain the New Zealand extract from [Geofabrik](https://download.geofabrik.de/australia-oceania/new-zealand.html). Source PBF data is not committed. Generated files identify OpenStreetMap as their source and include attribution and ODbL licensing metadata; retain that attribution when redistributing derived route data.

Build:

```powershell
dotnet build tools/Wodra.RouteBuilder/Wodra.RouteBuilder.csproj
```

Dunedin example:

```powershell
dotnet run --project tools/Wodra.RouteBuilder -- build `
  --pbf 'D:\Geofabrik\new-zealand-latest.osm.pbf' `
  --config routes/authoring/dunedin-one-way-oval-gardens.json `
  --output routes/generated
```

Use `--cache-dir none` to disable the local graph cache. Builds that fail a detour threshold still write diagnostic GeoJSON and a report; their route-points JSON is marked invalid and contains no usable points. Open the generated `.preview.html` while online to review OSM tiles and route sections.

Set `vegetation.enabled` to extract nearby OSM parks, gardens, woodland, open green areas, explicit tree nodes, and tree rows. The configurable search radius defaults to 200 m; area boundaries are conservatively simplified and suitable wooded/park areas receive deterministic, capped tree placement. Tree and representative area elevations are sampled from the existing local LINZ DEM cache (generation fails rather than silently substituting route elevation when samples are unavailable). The builder writes `<route-id>.vegetation.json`, adds Vegetation and Trees preview layers, and reports area/tree/DEM and geometry counts. Supported multipolygon outer rings are assembled from relation members; unsafe or inner-ring geometries are skipped and counted. The runtime vegetation bundle is copied into `src/IndoorCycling.Game/Data/Routes/` after reviewing the generated JSON and map.

Set `streetFurniture.enabled` to extract only explicit OSM street lamps, signals, stop/give-way/crossing features, bollards, benches, bus stops/platforms, and tagged traffic signs near the route. The configurable radius defaults to 80 m. Point features and supported tagged ways receive nearest route chainage/tangent metadata; candidates inside the 7 m ride ribbon are retained and counted for review. Every selected feature receives a LINZ DEM sample from the local cache. The builder writes `<route-id>.street-furniture.json` and displays the items behind an optional Street furniture layer in the authoring preview.

Set `terrain.enabled` to generate near and far terrain from the same cached LINZ DEM tiles after route elevation has been authored. The near zone defaults to 10 m spacing within 250 m; the far zone defaults to 25 m spacing out to 750 m with a 10 m downward-biased overlap. Near/far radii, spacings, chunk sizes, overlap, downward bias, and near-road clearance are configurable. This step is fully offline and fails if the selected DEM tiles are not cached. It writes `<route-id>.terrain.json` plus per-zone and combined area, vertex, triangle, chunk, NoData, elevation-range, clearance, byte-size, and timing statistics in the report. Review and copy the bundle into `src/IndoorCycling.Game/Data/Routes/` to embed it. Routes without an embedded terrain bundle retain the generic fallback ground.

To regenerate and install the three additional Dunedin route packages from a configurable local extract, run from the repository root:

```powershell
.\tools\Wodra.RouteBuilder\generate-dunedin-routes.ps1 -PbfPath 'D:\path\to\new-zealand.osm.pbf'
```

The script validates each generated loop and installs only generated route files and scenery sidecars into `%LOCALAPPDATA%\Wodra\Routes\<route-id>\`. Add `-NoInstall` to generate and validate packages without copying them. Raw PBF files remain external. Terrain generation uses the local LINZ DEM raster cache and will fail if the configured route-area coverage is not cached.
