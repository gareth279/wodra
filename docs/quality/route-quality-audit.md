# Offline route-quality audit

Generated from committed route packages with no network access. The JSON report is the authoritative, deterministic per-finding record: [route-quality-audit.json](route-quality-audit.json).

- Routes audited: 9; road-corridor rules applied: 5.
- Findings: 2092; corrected at load: 93; unresolved critical: 0.
- Known retained cases: mapped building footprints overlapping background-street corridors remain visible and are reported for review where route/elevation correspondence is uncertain; six sharp heading reversals and eighteen sub-centimetre route samples are review findings.
- Severity overrides may be supplied as JSON through `WODRA_ROUTE_QUALITY_SEVERITY_OVERRIDES`, mapping violation categories to `info`, `review`, `error`, or `critical`. CI fails on critical unresolved findings.

| Route | Environment | Road rules | Scenery checked | Intrusions | Correctable | Unresolved | Crossings | Zebra paint | Duplicates suppressed | Sidecars validated |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| dunedin-one-way-oval-gardens | Road | True | 1377 | 94 | 94 | 0 | 194 | 0 | 0 | 5 |
| dunedin-rolling-loop | Road | True | 4749 | 400 | 351 | 0 | 389 | 0 | 0 | 5 |
| dunedin-signal-hill-climbing-loop | Road | True | 3065 | 200 | 194 | 0 | 270 | 0 | 0 | 5 |
| dunedin-south-coastal-loop | Road | True | 2346 | 71 | 67 | 0 | 69 | 1 | 0 | 5 |
| otago-central-rail-trail-clyde-alexandra | Trail | False | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 |
| pine-hill-test | Road | True | 466 | 25 | 25 | 0 | 9 | 0 | 0 | 6 |
| signal-hill-big-easy-mtb | MountainBike | False | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 |
| wodra-velodrome-250 | Velodrome | False | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 |
| wodra-xc-skills-loop | MountainBike | False | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 |

## Pine Hill corrections

The 1,322.354 m closed loop has 9 explicitly unmarked crossing sources and produces 0 zebra crossings. The hedge at 315.209 m is measured using a 21.8 × 1.4 m horizontal bound that covers the continuous body and the outer BuildHedge canopy lobes. Its prior placement (2.421814 m / 4.085052°) penetrated the protected corridor by 1.399 m; current sidecar placement is 6.600 m / 94.085052° with 0.408 m clearance. The table records other physical corrections at source and corrected route-local (lateral, forward) positions. Ground cover is clipped as triangles, and the start-area scenery is intentionally replaced by the authored intersection scene.

| Chainage (m) | Feature | Original (lateral, forward) | Corrected (lateral, forward) | Clearance | Status |
|---:|---|---|---|---:|---|
| 117.77 | 118-left-garden-tree | (-8.000, 1.500) m | (-11.234, 0.161) m | 0.519 m | corrected-at-load |
| 151.05 | bus-stop | (-5.12, -0.19) m | (-5.83, -0.89) m | 0.254 m | relocated-at-load |
| 253.97 | 254-right-mature-tree | (9.000, 3.000) m | (12.500, 3.000) m | 0.333 m | corrected-at-load |
| 265.44 | bus-stop | (4.56, -0.34) m | (5.80, -1.17) m | 0.302 m | relocated-at-load |
| 306.63 | 307-right-trimmed-hedge | (5.600, 1.000) m | (9.489, 6.820) m | 1.056 m | corrected-at-load |
| 306.63 | 307-left-front-garden-tree | (-8.500, 2.000) m | (-8.500, 1.500) m | 0.534 m | corrected-at-load |
| 306.69 | 395-power-pole | (-3.502, 7.770) m | (-3.502, 9.770) m | 0.294 m | corrected-at-load |
| 390.65 | 391-right-mature-tree | (8.800, 4.000) m | (11.252, 3.512) m | 0.323 m | corrected-at-load |
| 485.08 | bus-stop | (2.22, -0.60) m | (5.91, -2.13) m | 0.414 m | relocated-at-load |
| 496.71 | bus-stop | (-3.05, -0.61) m | (-5.82, -1.76) m | 0.324 m | relocated-at-load |
| 645.43 | 645-right-garden-tree | (9.000, 3.000) m | (11.310, 2.043) m | 0.393 m | corrected-at-load |
| 673.87 | 775-property-hedge | (-11.133, -0.000) m | (-13.443, -0.957) m | 0.491 m | corrected-at-load |
| 678.39 | 775-roadside-pole | (-7.559, 0.000) m | (-7.559, 2.000) m | 0.341 m | corrected-at-load |
| 719.58 | bus-stop | (3.40, -0.66) m | (5.85, -1.15) m | 0.352 m | relocated-at-load |
| 739.85 | 740-left-garden-tree | (-8.500, 2.000) m | (-11.500, 2.000) m | 0.296 m | corrected-at-load |
| 772.01 | 772-right-trimmed-hedge | (5.500, 1.000) m | (15.500, 1.000) m | 0.361 m | corrected-at-load |
| 772.01 | 772-right-garden-tree | (9.000, 4.000) m | (9.000, 3.000) m | 0.407 m | corrected-at-load |
| 875.47 | bus-stop | (2.72, -0.31) m | (5.95, -1.65) m | 0.281 m | relocated-at-load |
| 880.06 | bus-stop | (-1.97, -0.27) m | (-5.89, -1.05) m | 0.392 m | relocated-at-load |
| 886.48 | 886-right-conifer | (8.500, 3.000) m | (11.272, 1.852) m | 0.318 m | corrected-at-load |
| 907.44 | 907-right-weathered-fence | (5.000, 1.500) m | (16.000, 1.500) m | 0.250 m | corrected-at-load |
| 907.44 | 907-right-conifer | (8.500, 4.000) m | (8.500, 2.000) m | 0.727 m | corrected-at-load |
| 1132.35 | generated | see evidence JSON | see evidence JSON | 0.573 m | corrected-at-load |
| 1308.12 | bus-stop | (4.50, 0.01) m | (11.85, -1.45) m | 0.281 m | relocated-at-load |
| 1310.76 | bus-stop | (-4.91, 0.02) m | (-5.83, -0.36) m | 0.332 m | relocated-at-load |

## Road-marking validation

The offline audit checked 931 crossing sources across five road routes. Pine Hill has nine explicitly unmarked crossing sources and generated zero zebra crossings. Oval (194), Rolling (389) and Signal Hill (270) generated zero zebras; South Coastal generated one zebra from explicit zebra tagging. Duplicate crossing marks suppressed: zero on every route. Existing stop and give-way controls and authored markings remain covered by the VisualRoute regressions; generic reference paint defaults off and unsupported unmarked crossing evidence is suppressed.

## Production RideScreen evidence

Godot 4.6.2 Compatibility renderer on NVIDIA Quadro P620. The full Pine Hill route completed three consecutive laps (3,974 m total) with normal steering spanning −2.83 m to +3.05 m within the 3.5 m route allowance. Twenty-seven moving chase-camera/HUD checkpoints cover corrected scenery (including the full correction list), crossings, intersections, bends and the seam. The production Dunedin Oval RideScreen review also passed six camera views and 720 moving frames with four AI riders and fifteen ambient cars; gravel, MTB, velodrome and road environment transitions passed their production reviews. All 27 moving checkpoint screenshots, seam captures, rider-distance/steering trace and performance CSV are committed under [artifacts/pr178-review-final](../../artifacts/pr178-review-final/).

| Capture | Description |
|---|---|
| [pine-ride-315m.png](../../artifacts/pr178-review-final/pine-ride-315m.png) | Corrected 315 m hedge, moving rider and normal HUD |
| [pine-ride-907m.png](../../artifacts/pr178-review-final/pine-ride-907m.png) | Corrected fence/tree and intersection |
| [pine-ride-1277m.png](../../artifacts/pr178-review-final/pine-ride-1277m.png) | Start-area intersection and lap approach |
| [pine-loop-lap2-after-seam.png](../../artifacts/pr178-review-final/pine-loop-lap2-after-seam.png) | Normal HUD immediately after lap seam |

Performance sample (114 frames at 1152×648): median 37.3 FPS, median frame time 26.79 ms, P95 frame time 29.22 ms, median 1,355,480 primitives, median 1,335 draw calls, video memory 219.4 MiB, texture memory 108.9 MiB and buffer memory 110.4 MiB. A comparable pre-clearance capture was not present in the repository or local review artifacts, so no before/after performance delta is claimed.

## Remaining review findings

The audit retains 839 mapped building/background-street footprint intersections as review findings because those OSM footprint and street ribbons can be offset or at different elevations; the renderer keeps those buildings rather than deleting significant scenery. Short segments and abrupt heading reversals are reported, not used to change authoritative route points. The six heading reversals are on the Rolling, Signal Hill and MTB routes and need owner review if route geometry itself is to change. Exact per-feature IDs, chainages, measured values, severity, status and evidence remain in the JSON.
