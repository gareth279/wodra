# Pine Hill 1,372 m asset-quality and RideScreen review

**Status: prototype only; visual acceptance is pending owner review. PR #176 remains draft and unmerged.** This pass is restricted to the give-way intersection. The route, OSM/LINZ data, catalogue, trainer and ride simulation remain the existing implementation.

## Local visual evidence

The source frame and unredacted screenshots stay outside Git under `%LOCALAPPDATA%`:

- Four-panel comparison: `Wodra\PineHillEvidence\Iteration6\pine-hill-intersection-four-panel.jpg`
- Production chase screenshot with rider, bicycle, HUD: `Wodra\PineHillEvidence\Iteration6\pine-route-1372m-gameplay-improved.png`
- Route-camera view at mapped chainage 1,371.735 m: `Wodra\PineHillEvidence\Iteration6\pine-route-1372m-static-route-camera.png`
- Route-camera view at source GPS chainage 1,377.37 m: `Wodra\PineHillEvidence\Iteration6\pine-route-1372m-static-gps-camera.png`
- Other approach views: `pine-route-approach-1362.0m-static.png` and `pine-route-approach-1368.5m-static.png` in the same folder.
- Performance CSVs: `pine-render-performance.csv` and `dedicated-vram-samples.csv`.

Panel A is the 4K source image `frame_011.jpg`, at 54 s into clip 2 (about 114 s on the combined capture timeline). Panel B is the PR #176 gameplay baseline. Panel C uses the production Godot RideScreen world with a static comparison camera. Panel D is the normal production chase camera. Panel C is only an approximate camera comparison: it uses the GPS-to-route matched chainage, route tangent, 1.45 m height and 70° vertical FOV. The calibrated dash-camera intrinsics, exact yaw/pitch and full extrinsic transform are unavailable. At that timestamp the raw GPS chainage is 1,377.37 m while the OSM-matched scene anchor is 1,371.735 m. This residual is retained as an explicit alignment uncertainty; the camera pose is not presented as surveyed.

## Scene and geographic registration

- The road, cross-street and elevation remain sourced from the route's OSM/LINZ package. Scene objects are placed in its route-local east/up/south frame and use the observation chainage and offsets.
- The long weathered fence uses a perspective-rectified crop of the clean right-hand fence region from the 4K video. The Blender scene builds separate tapered paling geometry, rails, posts and cap, with the source texture applied by board UV. No depth-predicted surface or source-facing card is used. Estimated 34 m run and 1.82 m height are image/map-informed approximations, not measured fence endpoints.
- The focal house is conventional 3D geometry with a hipped tiled roof, weatherboard walls, garage, windows, entry and chimney. Its silhouette, approximate scale and left-side placement follow the visible roof and mapped residential context. The facade layout and exact position remain approximate; no photographed house card is used.
- Two shared, textured Poly Haven Tree Small 02 CC0 assets provide branching, irregular broadleaf canopies. They replace the sparse 41k-triangle test version with approximately 83k triangles per instance. Species, placement and canopy extents remain approximations informed by the frame and OSM roadside context. Short verge grass, the photo-informed sign/pole, existing kerb geometry and the route-authored give-way line/triangle complete the focal scene.
- The give-way triangle remains oriented with its apex toward the source approach. Its observation width is now 4.0 m and length 2.5 m; these are visual estimates. Its orientation and sign approach face pass the deterministic route-local transform checks over eight compass headings. Camera projection and exact line-to-kerb spacing still need review.

## Comparison findings

The new render is visibly improved over the generic PR #176 baseline: the fence has source-derived weathering on real board geometry, the roof is hipped, the trees have volume and branches, and video observations select and place the assets in the production world. It is not yet a claim of visual acceptance.

Remaining differences visible in Panel C/D:

- The fence reads too pale and too uniform in the Wodra daylight; its joints, weathering and run endpoints do not align closely enough with the source.
- House roof proportions and position only partly match the source. The second roofline and dense background greenery are not reconstructed; generic OSM scenery remains visible at the sides.
- The near tree now has a branching canopy and trunk, but its projected relation to the sign and source tree still needs camera/geographic review. The second tree is a simplified visual stand-in.
- The route-camera view is not a calibrated dashcam reprojection. Road-edge, triangle, transverse line, fence and sign screen positions differ from the source. Camera and scene alignment need review together.
- The chase view confirms real 3D depth and rider clearance, but the distant hills and local greenery remain too simple for the source streetscape.

These gaps mean the requested recognizable-location goal remains open. Do not extend this method to the other three checkpoints until the owner accepts this scene.

## Workflow and reuse

`tools/modeling/extract_pinehill_fence_texture.py` rectifies a selected 4K fence region offline. `tools/modeling/build_pine_hill_intersection_assets.py` builds the house, fence, sign, utility pole, kerb-side planting and intersection mesh; `tools/modeling/prepare_polyhaven_pine_tree.py` packages the reusable CC0 tree. Route-scene observations carry chainage, lateral/forward offsets, dimensions, source timestamp, confidence, variant and provenance. `RouteSceneLayer` loads the finished GLBs and existing OSM/LINZ/road systems stay authoritative at runtime. No video or Python/Blender dependency is needed at runtime.

For a longer route, OSM footprints, LINZ terrain and shared licensed assets can be selected/placed from route data and observations. Manual authoring is still needed for prominent fences, house style/position when not derivable from footprints, vegetation composition, intersection markings and confidence review. The 34 m fence estimate and house/tree offsets show that this experiment still needs camera-to-route registration and authoring review; they should not become automatic measurements.

## Runtime and validation

On 2026-11-04, Windows / Godot 4.6.2 / OpenGL compatibility / Quadro P620, 1600×900:

| Measure | RideScreen result | Scope |
| --- | ---: | --- |
| Median / minimum sampled FPS | 38.3 / 29.1 | 111 frame samples during the production review; one run, not a benchmark guarantee |
| P95 frame time | 31.27 ms | Whole RideScreen scene |
| Peak draw calls | 962 | Whole frame |
| Peak primitive count | 1,148,373 | Whole frame, Godot RenderingServer counter |
| Focal authored asset triangles | 265,555 | One intersection asset plus two trees |
| Godot render video memory | 212.9 MiB | RenderingServer `VideoMemUsed` counter |
| Dedicated VRAM used / free | 1,544 / 504 MiB of 2,048 MiB | `nvidia-smi` card-wide sample during the run, includes desktop/other processes |

The RideScreen review loaded Pine Hill Test from the normal catalogue, selected its preview/elevation profile, loaded all required OSM/LINZ and authored scenery, captured at three approach poses plus the source GPS pose and chase view, then rode with the dummy trainer. Result: 28 production assertions passed; route length 1,679.8 m. The route is point-to-point, so no loop seam is claimed. Debug and Release Game builds succeeded. `IndoorCycling.VisualRoute.Tests` passed after updating the marking width assertion. Existing renderer/RID and text-server teardown leak messages still appear when the test process exits; they did not prevent capture or assertions.

No source video, raw frame or comparison image is committed. The new fence texture is a derived image from the user's footage and remains only in the focused branch; it must stay out of public releases unless separately reviewed and authorized.
