# Route geometry and scenery contract

Bundled and imported routes use the same `RouteDefinition`, `RoutePoint`, `VisualRoute`, and route-package validation path. This is the integration point for GPX work in #41 and later video/GPS observation work in #61/#64; this document does not promise an importer or video pipeline.

## Identity, geometry, and chainage

- `Id` is a stable route identity. A display-name change does not create a new route identity.
- `RouteDataVersion` versions the authored route package. Increment it when the canonical path or its interpretation changes, and review dependent scenery sidecars and comparisons.
- Ordered `RoutePoint.DistanceMeters` values are cumulative route chainage in metres, start at zero, and end at `DistanceMeters`. They identify positions on the authored route, not distances in an unrelated projected map.
- `GeometryFingerprint` is computed from ordered chainage, WGS84 latitude/longitude, and elevation values. It is independent of rider/session state and display name.
- GPS coordinates are WGS84 degrees. Runtime route coordinates use a local east/up/south frame in metres. Elevation values are metres in the route's documented vertical datum; bundled Dunedin data uses LINZ terrain elevations.
- Route progression remains the source of truth for rider motion, recorded distance, timing, and trainer control. A visual connector must be incorporated into canonical geometry and chainage rather than added as camera-only travel.
- Camera clearance queries use the camera's volume against active scenery collision chunks. Destination chunks are activated before the query; diagnostics record camera mode, desired and resolved positions, collision source, and clearance outcome.

### Procedural local-coordinate tracks

- Fictional tracks without a surveyed geographic source use native `LocalXMeters`/`LocalZMeters`; do not encode local metres as fake latitude/longitude.
- A measured closed track must generate its centreline and chainage from the same geometry. Validate the sampled centreline length against `DistanceMeters`; do not use route closure correction or scale a broken shape to make its declared length appear correct.
- `VisualRoute` may retain legacy closure and length parameterization for other routes, but a velodrome rejects a discontinuous seam or a centreline that differs from its authoritative length by more than 1 mm.
- Track banking is a cross-track presentation field. It is not `ElevationMeters`, `GradientPercent`, trainer resistance, route ascent, FIT elevation gain, or lap distance.

## Corridor and derived objects

- Road/corridor width is route input with provenance. The built-in Dunedin rendering fallback is 7 m where no mapped width is available; it is not a universal road, gravel, cycleway, or MTB width and is never inferred from GPX.
- When `turnaroundWidthMeters` is enabled, the authoring package must also state `turnaroundWidthSource`. The shared route builder only repairs a short exact reverse retrace, using a continuous width-bounded turn and lane merge. Generated turn chainages and width assumptions are written to the route report and route package.
- Scenery may carry the source feature type and OSM identifier (or an equivalent stable imported-source identifier), its nearest route chainage, lateral distance from the route, and sampled ground elevation. Those fields let a rendered defect be traced back to its source record.
- Placement and clearance checks use horizontal distance together with elevation. They must preserve legitimate bridges, stacked roads, and buildings on different levels.
- A future GPX package may mark road width and its provenance as unknown. It must not infer that GPX supplies buildings, road width, or a vertical datum.
- Optional timestamped GPS/video observations may refer to route identity, geometry fingerprint, chainage, timestamp, and source observation ID. These references remain optional; footage is not required to load or ride a route.

Generated geometry is validated before scenery is accepted. A route geometry change requires a new package version and a review of all chainage-indexed scenery, route comparisons, and saved challenge compatibility before those assets are reused.
