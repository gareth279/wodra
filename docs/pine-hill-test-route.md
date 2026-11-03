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

The OSM/LINZ sidecars provide mapped building footprints and heights (357 buildings), 77 deterministic stylised tree placements, 19 street-furniture features, mapped footpaths and connecting streets (4.84 km), nearby landmarks, and 3D terrain. The 7 m road ribbon is Wodra's documented rendering fallback because the OSM capture does not provide surveyed widths; video confirms a broad residential road but not a precise width. Power cables, exact paint wear, parked cars, per-house material/roof details, and the exact lateral position of video-only features remain stylised or absent. OSM has no explicit roadside tree rows here; most rendered trees are associated with mapped green areas, so roadside greenery is visibly sparser than in the recording. The 8 m DEM gives the route grade but does not resolve fine banks, retaining walls, or distant skyline terrain. Video observations do not create GPS-positioned buildings.

## Build, install, and review

Create the anchor config and capture report with `capture_to_route.py`, then build through the existing RouteBuilder using the local OSM PBF and LINZ cache. The committed package is the contents of `routes/user-packages/pine-hill-test`. To install it for the current Windows user:

```powershell
.\tools\Wodra.RouteBuilder\install-user-route-package.ps1 -PackageDirectory .\routes\user-packages\pine-hill-test
```

Restart Wodra to reload the normal route catalogue. The runtime loads route points and matching terrain/building/landmark/vegetation/furniture/street sidecars from `%LOCALAPPDATA%\Wodra\Routes\pine-hill-test`; source MP4 and GPX files are not read at runtime.

The production RideScreen review harness selects Pine Hill Test from the regular catalogue, checks its preview and elevation profile, starts a ride with the dummy trainer, and captures the moving rider at 395, 775, 1,010, 1,372, and 1,679.7 m. It checks loaded buildings, vegetation, terrain and streets, finite camera/rider poses, point-to-point behavior, and camera clearance. Captures and the four-position source/game montage are retained locally at `%LOCALAPPDATA%\Wodra\PineHillEvidence`; they contain unredacted source frames and stay out of Git. The game camera is a chase view, so its height and perspective differ from the dash camera in the car. Current screenshots confirm the road sequence, route bends, grade, mapped building massing, and intersection order. Visual review also found that generic OSM building forms and sparse roadside vegetation do not yet match the footage closely enough for full visual acceptance; those are the main remaining scenery improvements. No start/end closure is present because this is a point-to-point capture.
