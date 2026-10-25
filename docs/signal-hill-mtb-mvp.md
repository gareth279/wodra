# Signal Hill MTB showcase (issue #67)

## Route choice and limits

The bundled route is **Signal Hill · The Big Easy**, ridden once from the mapped lower trailhead to the mapped lookout endpoint. The documented trail is an easy climbing track through native bush to Signal Hill Lookout; this is an uphill ride, not a downhill route converted into an out-and-back. The in-game activity ends at the lookout and never teleports, reverses, or starts an implied return ride.

The reviewed OSM source trace is 5,114.9 m. The route package retains the measured OSM chainage and reports 328.8 m of climb from its processed DEM profile. A public trail description gives an approximate 6.1 km for the Big Easy. The difference is not scaled or filled with invented geometry: this package follows only the named, connected OSM ways listed below and ends at the selected OSM lookout endpoint. The trace is a usable mapped showcase, but it is not a survey or a claim that the OSM centerline matches every metre of the published trail.

| Endpoint | WGS84 |
|---|---|
| Lower Big Easy trailhead | `-45.8599614, 170.5327309` |
| Signal Hill lookout trail end | `-45.8586162, 170.5470295` |

The route builder restricts its graph to five reviewed OSM ways: named path ways `1552538872`, `317151858`, `688281912`, and `317151866`, plus way `226859348`, the short LINZ-mapped track connection required by the OSM graph. An extra candidate way was removed from the allowlist after the generated route showed it was not used. Each section retains source way ID and route-relative chainage. OSM surface values are `ground` on tagged lower sections; untagged sections remain `unknown` rather than being guessed. OSM `width=1` occurs on some of the named path ways only. The rendered 1.55 m surface ribbon and 2.45 m edge bed are authored presentation dimensions, not measured trail widths.

OSM tags do not specify a one-way restriction for the selected path ways. The app offers only this authored uphill traversal and stops at its endpoint; it does not make a claim about current on-site access, closures, or trail directions outside the bundled ride. The official sources describe Big Easy as a climbing/access trail. Check local signs and conditions before riding.

## Source data and processing

- Route geometry: OpenStreetMap ways listed above, downloaded individually by `tools/Wodra.RouteBuilder/download_signal_hill_mtb_source.py`; the authoring graph is restricted to that allowlist. OpenStreetMap contributors, ODbL 1.0. No raw OSM extract is embedded in the app.
- Terrain/elevation: Toitū Te Whenua LINZ Otago–Dunedin and Mosgiel 2021 LiDAR DEM (1 m collection), falling back to LINZ New Zealand Contour-Interpolated DEM (8 m). This build sampled 1 m data for 0.2% of route points and the 8 m fallback for 99.8%. Both are attributed CC BY 4.0. The processed profile uses 5 m resampling, a 30 m smoothing window, and a 25 m gradient window. The recorded maximum sample grade is about 30.6%; the trace is not flattened to remove a real mapped climb.
- Terrain mesh and mapped green-area/vegetation sidecars are generated from the same DEM and a separate bounded OSM route-context extract. The context contributes mapped vegetation only; it does not provide street traffic, buildings, or route geometry.
- `tools/Wodra.RouteBuilder/build-signal-hill-mtb.ps1` recreates the temporary source extracts, route package, elevation/terrain, and vegetation sidecars from the reviewed route definition and local elevation cache. Temporary OSM extracts are not copied to the game bundle.

Sources: [New Zealand tourism — Signal Hill mountain-bike trails](https://www.newzealand.com/int/feature/signal-hill/), [Dunedin City Council — Signal Hill Reserve plan](https://www.dunedin.govt.nz/services/parks-and-reserves/parks-and-reserves/signal-hill-reserve), [OpenStreetMap copyright and licence](https://www.openstreetmap.org/copyright), and the [LINZ Otago 2021 1 m DEM collection](https://nz-elevation.s3.ap-southeast-2.amazonaws.com/otago/dunedin-and-mosgiel_2021/dem_1m/2193/collection.json) with the [LINZ 8 m fallback collection](https://nz-elevation.s3.ap-southeast-2.amazonaws.com/new-zealand/new-zealand-contour/dem_8m/2193/collection.json).

## Environment and supported features

`RouteEnvironment.MountainBike` and `RouteDiscipline.MountainBike` are explicit and independent of route type and the per-section surface labels. The shared trail environment builder supplies a narrow dark dirt surface and edge bed, while the route keeps the existing authoritative power/mass/grade simulation, trainer command policy, recording, profile, history, and route metadata. Surface labels do not alter rolling resistance or measured telemetry.

The open `Point-to-Point` route type clamps route pose, preview, elevation, vegetation, and progress at its distinct endpoint. It has no lap seam. Reaching that endpoint ends and saves the ride once. Route elevation generation uses endpoint-clamped interpolation and smoothing and calculates climbing over adjacent open-route samples, without wrapping the start into the finish.

MTB, gravel, and velodrome use the explicit no-ambient-actor environment policy. Road routes retain their existing ambient cars, pedestrians, road scenery, and cyclist behavior. This MTB route disables pacers, AI cyclists, multiplayer, route ghosts, and collectibles because safe width-aware support has not been demonstrated. Keyboard/default riding follows the route centreline; optional lateral input is limited to 0.28 m from that line. It adds no traction, suspension, jumping, drafting, or surface-dependent physics. It reuses the current rider and bike visual models; no MTB-specific rider or bike model is claimed.

The route is not added to the versioned Dunedin road-route achievements. It does not enable a full-route badge for this open endpoint. Existing history, FIT recording, favourites, and route selection load the same embedded route definition and fingerprint; the route can be selected without developer source-data files.

## Acceptance status

Automated and dummy-driven evidence is recorded on issue #67 and in the PR. Real-trainer validation and owner visual acceptance remain pending.

Mapped green-area polygons are the only source for generated tree placement. Tree positions are deterministic illustrative instances inside those mapped polygons, not surveyed individual trees; the route-context extract had no explicit tagged tree points. This is a restrained visual fill and does not claim exact planting locations.
