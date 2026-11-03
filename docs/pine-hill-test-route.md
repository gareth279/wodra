# Pine Hill Test route prototype

Pine Hill Test is the first capture-derived Wodra road route. It is a user-installed point-to-point package with ID `pine-hill-test`; it does not need source footage at runtime.

## Source capture and alignment

The capture folder contained three H.264 MP4 files and `track.gpx`. FFprobe reports 3840×2160 video at nominal 30 fps, with durations 59.750 s, 60.008 s, and 18.968 s. MP4 creation times are embedded in UTC. The GPX contains 140 fixes at one-second intervals from 2026-11-03 03:21:27 to 03:23:45 UTC (138 s). It contains WGS84 coordinates, time, elevation, and speed extensions; the videos have creation times but no embedded location tag.

The first video begins one second before the first GPX fix. The second begins at GPX +60 s and the third at +120 s. Thus all footage aligns to the GPX timeline with high timestamp confidence. The first/second clip boundary has a 1.258 s media gap, while the second/third clips overlap by 0.008 s. The GPX has no gaps over two seconds and no stationary intervals under 0.5 m. Its recorded distance is 1,690.4 m; maximum one-second displacement is 17.8 m. The start and finish are 337.7 m apart, so the route is not a loop.

The local review contact sheet and extracted frames are retained outside Git at `%LOCALAPPDATA%\Wodra\PineHillEvidence`. They are intentionally unredacted source footage and must not be committed or packaged publicly.

## Route geometry

The existing `Wodra.RouteBuilder` solved 47 ordered anchors, three seconds apart, against the locally available New Zealand OSM extract. The resulting centreline follows Pine Hill Road, Lowe Street, Wilkinson Street, Pryde Street, Hislop Street, Allenby Avenue, and Pine Hill Road to the recorded natural endpoint. OSM chainage is 1,679.842 m (0.6% shorter than the raw GPS trace); the maximum anchor snap is 7.3 m and maximum corridor deviation is 9.5 m. The capture direction and turn sequence are preserved. No connector or closure was invented.

The authoritative runtime chainage and gradient come from the generated route points. Elevation sampling used LINZ's cached New Zealand Contour-Interpolated 8 m DEM because the local 1 m LiDAR tile did not cover this block. The route has 30.1 m positive ascent. GPX altitude (149.8–163.5 m) is retained only in the capture analysis as a source comparison and is not used as authoritative route elevation.

## Video observations and scenery

`routes/user-packages/pine-hill-test/pine-hill-test.observations.json` contains human-reviewed video observations with capture timestamps, raw GPS chainage, matched route chainage, lateral match distance, geometry fingerprint, confidence, and provenance. The viewed frames show suburban uphill/downhill streets, kerbs and grass berms, detached houses, wooden fences, hedges, mature trees, power poles, and views over the surrounding hillside. At the 1,372 m position, a give-way sign, painted junction markings, tactile kerb ramp, and long timber fence are particularly distinctive.

The previous prototype's observations were prose only: `RideWorldView` never loaded them. Thus detailed notes about fences, hedges and trees had no effect on the renderer; OSM extrusions, OSM greenery and generic markings remained the only visible scene. That was the primary cause of the rejected visual comparison.

The observations sidecar supports individual route-relative scene features with chainage, lateral/forward placement, orientation, dimensions, variant/material, video timestamp, confidence and provenance. `RouteSceneDataLoader` validates it and the production `RouteSceneLayer` consumes it inside `RideWorldView`; observations are rendered rather than remaining prose-only metadata. Side-object placement samples the cached LINZ DEM, while road paint stays attached to authoritative route elevation. Scenery remains presentation-only and does not alter route geometry or physics.

### 1,372 m Blender-authored scene — iteration remains unaccepted

The intersection prototype includes `PineHillGiveWayIntersection.glb`, authored with `tools/modeling/build_pine_hill_intersection_assets.py` in Blender 5.2. It uses in-house meshes/materials only, with no downloaded assets or external texture/license dependencies. The route observation loads the GLB at the recorded 1,371.735 m pose through the normal `RideWorldView` route-scene layer. Within the local 36 m window it replaces duplicate primitive video features and four selected OSM building extrusions; route geometry, OSM footprints and authoritative elevation remain unchanged. The source `.blend` and script are included for iteration. This is a focused prototype asset, not a production-quality Pine Hill reconstruction.

Road-local coordinates use Godot local `-Z` for direction of travel, local `+X` for road-right, and local `+Z` for approaching traffic. The give-way marking apex and sign face point toward approaching traffic (`+Z`). A deterministic regression checks the route frame, lateral offset, mark apex and sign face for eight headings. The triangle uses a 3 m base by 4 m length and is manually placed; source-camera perspective shows its placement/spacing still differs from the footage. A production-path, UI-free camera capture was compared to the source frame and normal chase view. The static camera’s height, heading and FOV are estimates, not calibrated camera intrinsics.

This review **does not pass visual acceptance**. Compared with the source, the scene’s sign is partly hidden and its lettering is small; the authored house is too central and fully exposed where the source shows only a partial roof; the tree crowns remain angular and repetitive; the fence is too uniform and bright; the give-way triangle-to-line screen spacing and overall projection differ. The chase view confirms these are actual production-renderer results. See [`pine-hill-intersection-visual-review.md`](pine-hill-intersection-visual-review.md) for comparison notes and local screenshot paths. Do not treat the presence of these objects or a passing build as acceptance.

The terrain authoring radius now extends to 1,000 m using the locally cached LINZ 8 m DEM (27,126 rendered triangles, no NoData cells). This adds measured distant relief around the route; it is still a cartographic DEM, not a fine-resolution survey of banks or gardens. Road paint at the give-way approach is aligned to route elevation so the hollow triangle, transverse give-way line, and kerb dashes render on top of asphalt. The triangle dimensions and sign lettering are manually tuned against the dash-camera frame. Video-informed style overrides now cover 28 nearby mapped house footprints at the four review points; house heights, facades and roof choices remain low-confidence stylised estimates where the video cannot identify an individual footprint.

The source frame at elapsed 114 s shows the give-way sign, transverse line and triangle, yellow kerb dashes, pale timber fence, utility pole, grass verge and mature foliage. The triangle is oriented to point back toward the approaching dashcam, opposite travel direction. Its authored dimensions are an estimate from the source perspective; its placement is checked in the rendered camera comparison rather than inferred from feature JSON alone.

The OSM/LINZ sidecars provide mapped building footprints and heights (357 buildings), 77 deterministic stylised tree placements, 19 street-furniture features, mapped footpaths and connecting streets (4.84 km), nearby landmarks, and 3D terrain. The 7 m road ribbon is Wodra's documented rendering fallback because the OSM capture does not provide surveyed widths; video confirms a broad residential road but not a precise width. Power cables, exact paint wear, parked cars and individual house details outside the authored scene remain stylised or absent. The 8 m DEM gives route grade but does not resolve fine banks, retaining walls or the distant skyline. Road markings sourced from OSM controls can be disabled per route where video review shows they are unsupported; this Pine Hill package disables those generic reference stripes and uses its authored give-way marks.

## Build, install, and review

Create the anchor config and capture report with `capture_to_route.py`, then build through the existing RouteBuilder using the local OSM PBF and LINZ cache. The committed package is the contents of `routes/user-packages/pine-hill-test`. To install it for the current Windows user:

```powershell
.\tools\Wodra.RouteBuilder\install-user-route-package.ps1 -PackageDirectory .\routes\user-packages\pine-hill-test
```

Restart Wodra to reload the normal route catalogue. The runtime loads route points and matching terrain/building/landmark/vegetation/furniture/street sidecars from `%LOCALAPPDATA%\Wodra\Routes\pine-hill-test`; source MP4 and GPX files are not read at runtime.

The production RideScreen review harness captures the original PR #176 baseline, the revised UI-free source-style camera, and the normal chase camera at the intersection. Comparison images remain local under `%LOCALAPPDATA%\Wodra\PineHillEvidence`; raw source footage stays outside Git. No other checkpoint is being rebuilt until the intersection passes visual review. No start/end closure is present because this is a point-to-point capture.
