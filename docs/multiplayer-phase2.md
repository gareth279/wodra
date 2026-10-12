# Multiplayer Phase 2

Phase 2 uses protocol version 2 and requires the updated game and server together. It renders other connected players with the same `CyclistVisual` model used by local and AI riders. Rider snapshots are buffered for 200 ms and interpolated in absolute route distance, cadence, speed and power. The visual route wraps absolute distance only when finding the repeating road pose. Tracks hide after two seconds without a fresh update, and disconnect events remove the cyclist and name label. A validated `RiderRepositioned` message clears the old interpolation buffer immediately.

The multiplayer page shows each connected rider's lap, route distance and relative position. When a new rider receives state from someone more than 100 m ahead, a compact **Catch Up / Keep Riding** offer appears. Catch Up always opens a confirmation; neither joining nor dismissing the offer moves or pauses either ride.

## Catch Up and FIT recordings

Confirming Catch Up checks that the peer is still connected and has sent a fresh state. If this ride has an active or paused automatic FIT recorder, Wodra finalizes that file first. It moves the local rider to four metres behind the peer (clamped at route start), resets virtual speed and route visuals, requests the new trainer grade on the next simulation update, then starts a fresh FIT segment at the new route distance. A paused ride starts its new segment paused. The segment's first route altitude and GPS point are based at the new location, and recording progress starts at zero, so the reposition is absent from FIT distance and elevation gain. If recording was inactive, Catch Up leaves it inactive.

Consecutive segments are listed in an `{activity-id}.segments.json` manifest beside the FIT files, with route start/end and ridden distance for each file. Existing FIT files are kept. If saving the current FIT fails, the snap is cancelled. If creating the next segment fails, the snap remains and the HUD reports that FIT recording is inactive.

Ride summaries count distance actually simulated by the local rider, not route progress added by a snap. Remote riders use only remote telemetry for their own visual pedalling and never affect local physics, trainer control, camera tracking, pause, or recording.

## Two-client test

Start the server as described in [Multiplayer Phase 1](multiplayer-phase1.md). Launch two Godot clients, start dummy trainer rides on the same route, and join room `dunedin` using distinct names. After each client has sent a state update, both should see the other cyclist and name above the road. Use the multiplayer page to choose a rider and **Catch Up to selected rider**, confirm, and keep pedalling. The earlier FIT should remain in the recording folder and a new FIT segment should appear after the snap. The Multiplayer HUD should report the room rider count.

The integration test exercises two websocket clients and the explicit reposition relay. Recording tests verify FIT segmentation and progress rebasing. Interactive Godot rendering and the full two-window workflow still require a manual in-game check.
