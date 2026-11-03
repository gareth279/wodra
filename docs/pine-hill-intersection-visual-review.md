# Pine Hill 1,372 m scene review

**Status: visual acceptance failed; PR #176 remains draft.** This review covers the give-way intersection only. The other route checkpoints were not rebuilt in this iteration.

## Production captures

The four-panel local comparison is `%LOCALAPPDATA%\Wodra\PineHillEvidence\pine-hill-1372m-iteration-comparison.png`. It contains the aligned source frame, the original PR #176 RideScreen baseline, the revised UI-free matched-camera RideScreen render, and the revised RideScreen chase-camera render. Individual production captures are under `%LOCALAPPDATA%\Wodra\PineHillEvidence\RideScreen`:

| View | Local file |
| --- | --- |
| Source video at elapsed 114 s (route alignment ≈ 1,371.7 m) | `gpx-114.jpg` |
| A — PR #176 baseline | `pine-route-1372m-pr176.png` |
| B — revised static view | `pine-route-1372m-static-improved.png` |
| C — revised gameplay chase view | `pine-route-1372m-gameplay-improved.png` |

Source frames are identifying footage and remain local; they are not included in the repository. B is a static camera inside the production RideScreen scene, with estimated 1.45 m height and 70° FOV. Its pose is route-relative and its projection is only approximate; the dashcam intrinsics and exact camera pose were not recoverable from metadata. C uses the regular chase camera and is included to verify the loaded game scene, not as a perspective-matched source comparison.

## What changed and what the renders show

| Feature | Source correspondence | Remaining difference |
| --- | --- | --- |
| Give-way marking | Hollow triangle points back toward the approaching camera, matching the source apex direction. Its base is about 3 m and length about 4 m in the authored route frame. | The projected triangle-to-line gap and apparent proportions remain wrong in B. The source camera is not calibrated, so a screen-space match alone cannot establish exact metres. |
| Sign | Roadside sign has a route-frame normal toward approaching traffic; an eight-heading test checks this orientation. | It is partly occluded by foliage and the small geometric legend is hard to read. The position and sign face have not passed visual review. |
| Fence | Authored weathered paling fence spans the cross street and appears in the production renders. | Board spacing/height is too regular and the colour is too light compared with the aged source fence. |
| House | A custom low bungalow mesh includes a hipped roof, cladding courses, framed windows, entry and chimney. | It is too central, too exposed and too large in the composition. The source shows only part of a roof behind the fence, left of the utility pole. |
| Trees and planting | The asset includes branch meshes, foliage masses, verge and garden shrubs. | Crowns still read as repeated angular clusters; some foliage masks the sign. The source has denser, irregular mature canopies. |
| Pole and terrain | Utility pole and local route terrain are present; DEM-based terrain and road grades remain authoritative. | Pole placement/proportion is approximate; nearby bank, road-edge detail, distant greenery and hill silhouette are too simple. |
| Overall streetscape | The revised scene has recognizable authored objects and is visibly different from the box-extrusion baseline. | It still does not convincingly reproduce the source composition or asset quality. The image comparison is a failure, not a qualified pass. |

Callouts 1–5 in the local montage identify the obscured sign, central house, angular canopy, uniform fence, and triangle spacing/projection respectively. Object presence is not the acceptance measure; visible silhouette, placement, scale, orientation and composition are.

## Orientation regression

The route renderer uses local `-Z` as travel-forward, `+X` as road-right and `+Z` as the direction toward approaching traffic. The yield triangle apex and sign face must point along the latter. `RouteSceneTransformMath` centralizes these conversions; the VisualRoute test exercises eight compass headings, asserting forward, approach, apex, base, right-side offset and orthogonality. Road paint remains attached to the route frame/tangent. The production image confirms the triangle apex is now at the near end, as in the source, although its placement/projection still needs work.

## Reusable authoring approach

The focused workflow is: inspect an aligned source frame; author a route-local Blender scene using metre-scale coordinates and documented evidence; export a self-contained GLB; refer to it from a route observation with chainage, offset, timestamp, confidence and provenance; load it through the existing route-scene layer; then capture the actual RideScreen view. The included Blender 5.2 script and `.blend` source are reproducible and use only in-house geometry/materials, avoiding third-party license and dependency review. Static meshes are joined by material to reduce runtime mesh instances. No source video is needed by the runtime.

This proves the asset-loading path, but the current models need another quality/composition pass before this authoring technique can be used as a successful route reconstruction. In particular, foliage needs a less repetitive volumetric canopy, the fence needs better weathering/irregularity, and the house and sign must be repositioned and rescaled from the observed frame. The intersection has not been expanded into a broader scenery refactor.
