# Route personal bests, segments and ghosts

## Boundaries and eligibility

Road route efforts use the route definition's forward reference line and unwrapped authoritative ride distance. `RouteEffortCatalog` is the authored boundary: it returns a full-lap effort for road routes with geometry, plus only the segment definitions whose route ID, route-data version and geometry fingerprint match. The tracker interpolates start and finish crossings between simulation samples. A route lap begins at chainage 0 when a ride starts there; later laps begin at interpolated finish-line crossings. This is a standing-origin start, not a fly-in time. Velodrome efforts continue to use `TrackEffortTimingController` and the existing track PB store.

The elapsed ride clock is used without subtracting pauses. A pause invalidates the effort already in progress; the next effort can be eligible. Periods without telemetry invalidate an overlapping attempt. Debug jumps and multiplayer Catch Up discard active boundary crossings and mark later results in that ride ineligible because recorded route progress can no longer be matched reliably. Bringing an AI pacer to the local rider does not move the rider and has no effect on effort eligibility. Dummy, debug and test-profile rides can exercise timing but cannot create eligible road PBs.

Only completed efforts with matching profile, route ID, route version, direction and geometry fingerprint are returned as PB candidates. Legacy ride history has no such provenance and is not backfilled. Its saved FIT can still be replayed through the existing history action, explicitly labelled as an unverified saved replay. A missing or unreadable FIT prevents a route PB from being selected as a ghost but does not erase its time.

No drafting or wind-shadow assist state is currently recorded in the local ride simulation or activity history, so route efforts do not claim a draft-assisted category or try to normalize for one. Existing multiplayer Catch Up and AI-pacer sync are repositioning actions, not drafting mechanics; Catch Up invalidates an active route effort while bringing an AI pacer to the player does not affect the player's effort.

## Authored segments (catalog version 1)

| Stable ID | Route | Direction | Chainage | Distance | Boundary rationale |
| --- | --- | --- | ---: | ---: | --- |
| `segment.dunedin.signal-hill-climb.v1` | Dunedin Signal Hill Climbing Loop, route version 1, geometry `c40dc0e974b6ac57c70e0c3879c2620deabefbe4537ee5f63958bf222eed915b` | Forward | 2,512.568–6,753.961 m | 4,241.392 m | Starts at the foothills approach (7.4 m route elevation); finishes at the upper lookout (300.1 m). The defined route profile gains about 292.6 m over the interval. |
| `segment.dunedin.cumberland-northbound.v1` | Built-in Oval-to-Gardens, route version 1, geometry `b8a2bbca1d952379b5fc0a8e44fcb44221f0db157241640112aef38c13ffcfd0` | Forward | 998.674–1,925.113 m | 926.439 m | Reuses the route's named northbound Cumberland Street span. The southbound return is a separate physical traversal and does not match this directed chainage interval. |

The Signal Hill coordinates used to review those boundaries are approximately (-45.8654537, 170.5185034) and (-45.8573733, 170.5460781). The built-in segment's source labels provide its boundary names. Each segment attempt is stored independently and linked to its parent activity; attempt index and deterministic result ID make retries idempotent. Unavailable optional route packages or revised geometry simply do not receive these definitions.

## Ghost comparison

The route selector can compare against the fastest eligible lap or the most recent eligible lap for the exact profile and geometry. It loads that activity's FIT trace, clips it to the recorded lap boundaries, and replays the lap on the matching route. At each local route progress, the displayed signed time difference is computed from recorded ghost elapsed time at that distance minus local elapsed time since the equivalent start line. Positive means the local rider is ahead. The comparison is hidden when the trace has no corresponding progress or crosses a recorded gap over ten seconds; it is never inferred from distance divided by current speed. Stationary plateaus use the first time the ghost reached that progress. Matching segment attempt indices provide split comparisons where trace and geometry allow it.

Ghosts are rendering-only riders in a separate lane. They do not feed simulation, trainer commands, local telemetry or FIT recording. Legacy history ghosts retain their existing replay behavior and identify that route/time eligibility is unverified.
