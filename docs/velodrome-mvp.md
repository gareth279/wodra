# Route environments and activity modes

`RouteDefinition.Environment` selects the route's world presentation and `SupportedModes` lists the ride activities it offers. These are separate: a future racing mode can be offered by road, velodrome, or MTB routes without making racing an environment. `RouteCapabilities` declares feature combinations such as traffic, pacers, ghosts, collectibles, and multiplayer. Routes that predate this metadata receive the legacy road capabilities and retain their current behavior.

## 250 m velodrome MVP

The built-in `wodra-velodrome-250` route is one local-coordinate stadium oval: two 45 m parallel straights and two 80 m semicircular corners. The sampled centre reference line measures exactly 250 m. Its corner radius accounts for the finite chords in the generated polyline; `VisualRoute` validates the measured centreline and does not close or rescale velodrome geometry. The route data version is 2 because the original points did not represent the intended path. The stable route ID preserves stored results, while versioned PB keys keep old-geometry results separate.

The racing surface is 7 m wide and the blue safety apron is a separate inside surface. The centreline and all derived geometry use native local metres, not invented geographic coordinates. Banking is a visual cross-track slope: 13.5° on straights and smoothly transitions to 43.6° through both corners over 8 m. Positive rider-left is the outside of this oval, so the outside edge is higher. Track bank is not route elevation or grade and never changes trainer resistance, authoritative distance, or recorded elevation gain. Steering stays within the racing surface with a 0 m default line and 3.05 m maximum lateral offset.

`VelodromeTrackBuilder` owns the banked track surface, coloured lane lines, edge markings, start/finish marking, infield, roof, arena walls, and supports. `RideWorldView` chooses this environment builder from route metadata; road scenery, ambient traffic, and local road AI riders are absent on the track. `RouteCapabilities.SoloVelodrome` explicitly disables pacers, ghosts, collectibles, traffic, and multiplayer for this MVP. These features remain available on legacy road routes.

`RideActivityMode` is independent metadata. The track currently offers free riding, one-lap timing, flying 200 m with a 50 m run-up, and standing-start 500 m and 1 km. `TrackEffortTimingController` observes authoritative distance and elapsed time and linearly interpolates each crossing from the surrounding simulation samples. Pause and reposition mark an attempt as practice. Dummy, debug, and test-profile efforts are timed but cannot become personal bests. Bests are stored in existing per-user settings, keyed by rider profile, stable route ID, route data version, and effort mode.

## Adding a future environment or mode

For another environment, add a `RouteEnvironment` value, provide a dedicated visual/world builder, and define the route's feature capabilities. Keep environment-specific geometry and presentation out of `RideSessionNode` and shared simulation. For a new activity mode, add a `RideActivityMode`, declare it in the route's supported modes, and implement its timing/rules in a mode-specific policy that consumes authoritative session state. Version the route data when geometry changes that affect personal-best comparability. Add focused tests for mode rules and a production-scene review for the environment. Do not select behavior by route ID.

This MVP does not implement MTB, racing, track drafting, ghosts, pacers, collectibles, track multiplayer, advanced banking physics, fixed-gear simulation, or external leaderboards. The issue's later phases remain future work.
