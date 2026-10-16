# Issue #54: shared road elevation and route surface audit

## Root cause

`tools/Wodra.RouteBuilder/elevation/generate_elevation.py` samples a route on a regular 5 m chainage grid, calibrates 8 m fallback DEM spans by interpolating vertical corrections between 1 m DEM coverage boundaries, and smooths the resulting profile in route order. On an out-and-back route those corrections are applied to each traversal independently. The same Signal Hill roadway therefore received different calibrated heights depending on whether the rider was outbound or returning. Smoothing was not the source of the original 6.865 m peak discrepancy; smoothing carried a smaller part of the calibration difference into adjacent samples.

The fix pairs samples only when they are sustained reciprocal matches along the route: close lateral position, limited along-track offset (to support different sample spacing), compatible raw DEM heights, and aligned or reversed travel tangents. One crossing or a parallel road 2 m away cannot form a match. Matched values are averaged before gradient calculation; a cosine blend carries endpoint corrections into adjacent unique road so the shared span does not introduce a hard gradient step. The same operation runs after smoothing and bridge interpolation.

The game previously emitted a road ribbon quad for every consecutive route sample. `RepeatedRouteSurfacePolicy` now omits one of two sustained duplicate physical traversals from each ribbon surface. Route distance, route points, rider motion, grade, trainer inputs and FIT altitude still retain the full route traversal. The policy uses a spatial hash and only suppresses a span after five or more consistent matches over at least 25 m; isolated intersections and nearby parallel roads remain separate.

## Built-in route audit

Elevation samples were refreshed for the three generated loops with cached LINZ data. The one-way Oval Gardens route has no repeated-traversal elevation profile.

| Route | Repeated spans | Matched sample pairs | Matched route length | Largest processed difference before | Difference after | Duplicate road segments omitted at runtime spacing |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Signal Hill Climbing | 5 | 879 | 4.535 km | 6.865 m | 0 m | 477 |
| Rolling City and Valley | 2 | 140 | 0.865 km | 0.654 m | 0 m | 89 |
| South Dunedin Coastal | 3 | 257 | 1.649 km | 0.208 m | 0 m | 124 |
| Oval Gardens one-way | 0 identified | — | — | — | — | 0 |

At the issue’s Signal Hill chainages 3,999.914 m and 9,759.922 m, route-point interpolation now gives 83.619 m and 83.524 m: a 0.095 m difference at positions 0.316 m apart. Three additional matched locations have elevation differences of 0.025 m, 0.029 m and 0.081 m. Gradient signs reverse across those traversals. The VisualRoute regression also checks both visual appearances at the same geographic locations, which share the same road/rider surface heights.

The duplicate-segment counts use the game's default 10 m `VisualRoute` sampling, the same spacing used by `RideWorldView`. Only the generated route-points, report, elevation profile CSV, sample CSV and authoring profile HTML for the three refreshed routes changed. Route centreline and scenery sidecars were not rebuilt.

The VisualRoute package check found two of ten building footprints within 5 m of the Signal Hill route whose independently sampled footprint DEM bases differ from the route centreline by about 2 m (the largest is 2.042 m). One such difference was already present in the previous route package; the other reflects the corrected 8 m fallback road profile beside independent 1 m footprint samples. These are separate measurements on terrain across a cut/cross-slope, not duplicate road samples. The check now allows 2.25 m for this proximity comparison while still validating every footprint against its own DEM samples. This is an automated data check, not proof that every nearby building looks correct in a rendered ride.

## Validation limits

Automated coverage verifies shared elevation before and after smoothing, opposite-direction grade, different sample spacing, close parallel roads, intersections, bridge height separation, and Signal Hill's reported positions. The headless Godot `RouteWorldSmokeTest` constructed and released road, rider, building, street, terrain and vegetation meshes for all four installed routes. Its runtime-spacing surface audit identified 477 Signal Hill, 89 Rolling City, 124 South Coastal, and 0 Oval Gardens duplicate road segments to omit. Headless construction is not a rendered ride review and does not confirm trainer resistance, FIT altitude, or AI/local rider presentation on a physical ride. Those checks remain part of owner acceptance for #54.
