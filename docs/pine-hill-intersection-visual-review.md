# Pine Hill 1,372 m scene review

**Status: still not accepted for visual quality. PR #176 remains draft and unmerged.** This iteration addresses only the give-way intersection. The start, 395 m and 775 m scenes were not reconstructed.

## Production renders and comparison

The local four-panel comparison is `%LOCALAPPDATA%\Wodra\PineHillEvidence\pine-hill-1372m-pr176-current-review.png`. It contains the source frame, the PR #176 production baseline, the current matched-camera RideScreen view, and the current RideScreen chase-camera view. Individual current captures are in `%LOCALAPPDATA%\Wodra\PineHillEvidence\RideScreen`:

| View | Local file |
| --- | --- |
| Original dashcam frame at video 114 s | `gpx-114.jpg` |
| A — PR #176 RideScreen baseline | `pine-route-1372m-pr176.png` |
| B — current matched-camera RideScreen | `pine-route-1372m-static-improved.png` |
| C — current gameplay chase camera with rider | `pine-route-1372m-gameplay-improved.png` |

B is rendered by the production Godot `RideScreen` world and `RouteSceneLayer`. It uses route chainage 1,372 m, an estimated 1.45 m camera height, route heading, and 70° vertical FOV. Camera intrinsics and exact video-to-GPS camera position are unavailable; this is a comparison pose, not a calibrated camera match. C is the normal gameplay chase camera with the dummy trainer active. Raw video and source frames remain local and are not committed.

## What changed

- The give-way sign is on the left approach and its face points toward the dashcam. Road-local `-Z` follows travel, `+Z` faces approaching traffic. Godot yaw and the local sign/marking axes are checked for eight compass headings.
- The hollow give-way triangle apex points toward the approaching camera, matching the source frame. Its authored estimate is 3.0 m wide by 2.5 m long, with a 5 m route-forward origin. The give-way line remains at 9.5 m. The rendered orientation is correct; its screen projection and exact placement still need refinement against calibrated camera geometry.
- The long cross-street timber fence, hipped-roof house, utility pole, kerb ramp, tactile pad and verge are in one metre-scaled Blender scene asset. The former ball-shaped kerb planting was replaced by tapered grass-blade meshes.
- Two branching, textured broadleaf assets are used as route-local backdrops. The misplaced centre candidate was removed after camera review. Its species and coordinates remain approximations.
- Three generic OSM building extrusions that conflicted with the fence-screened frame are hidden through the existing per-building visual style while retaining their mapped footprints and route data.
- Route-authored observations are now visible only within 220 m of current chainage. This prevents observations from distant checkpoints, including start-area trees, appearing in the focal scene. The production review measured 6 of 24 observations visible at 1,372 m.

## Remaining differences

| Feature | Current correspondence | Difference still visible |
| --- | --- | --- |
| Road and marking | Give-way triangle apex direction, sign approach face and cross-street arrangement are correct. Yellow kerb dashes, tactile ramp and give-way line are present. | The source and rendered road-edge/marking spacing do not yet line up in screen space. Camera calibration is unavailable, and the scene projection still looks too small/central in B. |
| Sign and pole | Sign is readable from the approach after off-route observations are culled. Utility pole is near the fence and house roof. | Sign lettering and pole proportions are approximate; their lateral placement is not surveyed. |
| Fence | A long weathered paling fence spans the far side of the junction. | The authored boards are still too regular and light; the source fence has stronger gray weathering and less uniform edges. |
| House | A bespoke hipped roof, weatherboards, windows, entry and chimney replace the nearest generic extrusion. | The visible house silhouette remains too exposed and simplified; a second roofline in the source is not yet faithfully represented. Some distant OSM masses remain generic. |
| Vegetation | Two detailed leaf-card/branch tree meshes and tapered verge grass replace the single spherical proxy at this location. | Canopy density, tree spacing and leafy backdrop coverage remain thinner than the source. Exact species and positions are unknown. |
| Terrain and lighting | LINZ road elevation is unchanged; local terrain and distant hill forms load in the production view. | The background reads as a simple green hill and blue sky rather than the dense, layered vegetation in the frame. The rendered vertical composition still differs. |

A and B show a visible qualitative improvement over the original generic extrusion view: the sign, fence, custom roof, real branching tree asset and planting all come from route-scene authored data. The comparison still fails the requested visual standard. Object presence alone is not acceptance, and no other checkpoint should be reconstructed until this scene is reviewed again.

## Asset and authoring workflow

The focal Blender source is `tools/modeling/source/PineHillGiveWayIntersection.blend`; `tools/modeling/build_pine_hill_intersection_assets.py` exports its self-contained GLB. Each observation records route chainage, lateral/forward offset, video timestamp, confidence and provenance. `RouteSceneLayer` loads the authored scene and existing Wodra scenery layers remain the runtime renderer. The route loads without video processing.

The two tree instances use Poly Haven *Tree Small 02* by Rico Cilliers, downloaded at 1K under CC0 and reduced from 2,062,487 source mesh faces to 206,248 triangles per tree. The GLB contains its textures. The [asset page](https://polyhaven.com/a/tree_small_02) and [CC0 licence](https://polyhaven.com/license) are documented in `docs/pine-hill-test-route.md`. The tree serves as a broadleaf proxy; species and placement are estimates.

## Validation

The production RideScreen review loads the route through the normal catalogue, renders B and C, checks the 1,372 m camera, starts the dummy trainer and rides naturally toward the open endpoint. Current review result: 30 production assertions passed; route length 1,679.8 m; no loop seam exists because the recording is point-to-point. The chainage visibility assertion confirms remote observation features are hidden at the focal scene. This validates runtime integration and camera clearance; it does not grant visual acceptance.

The production teardown logs existing Godot renderer RID/resource leak warnings after the review scene exits; these do not occur during the assertions or prevent captures. No frame-rate benchmark has been run. Source assets, footage and comparisons remain local; the route package itself has no runtime video dependency.
