# Levels Raceway workout scenery

This local workout circuit uses the full anticlockwise Timaru International Motor Raceway centreline as lightweight flat scenery. It is not registered in the geographic route catalogue and does not produce ride GPS, route records, or route-completion awards.

## Source and attribution

- OpenStreetMap way [101182095](https://www.openstreetmap.org/way/101182095), named Timaru International Motor Raceway, tagged `highway=raceway`, `oneway=yes`, and `source=LINZ`.
- Source extract retrieved 30 October 2026 from the [OSM API full-way endpoint](https://api.openstreetmap.org/api/0.6/way/101182095/full). The committed `levels-raceway.osm` retains only this way and its referenced nodes; contributor account metadata is omitted.
- © OpenStreetMap contributors, [ODbL 1.0](https://www.openstreetmap.org/copyright).
- Track length/direction and layout checked against the [South Canterbury Car Club circuit information](https://southcanterburycarclub.org.nz/circuit-activities/) and the [MotorSport NZ Levels Raceway map (PDF)](https://motorsport.org.nz/wp-content/uploads/CRO004f_Levels_CRO_Map.pdf).

## Geometry preparation and validation

`tools/Wodra.RouteBuilder/generate-levels-raceway.py` validates the source tags, closed way and anticlockwise signed area, chooses an approximate start/finish chainage on the mapped main straight, and exports `levels-raceway.centerline.gpx`. The GPX preserves the source path; no undocumented shortcuts, pit lane, or 1.6 km layout are joined into it. The schematic map does not georeference an exact start/finish coordinate, so that placement is approximate.

The source way has 108 references including its repeated closure node (107 unique positions). Haversine length on a mean Earth radius of 6,371,008.8 m is 2,402.70 m, 2.70 m (0.113%) over the published 2.4 km. It is closed and anticlockwise and its overall shape was compared visually with the published map. The difference is small and the route is not stretched to force a nominal length.

The generator resamples the track at 5 m spacing into `levels-raceway-workout.route-points.json`, preserving the exact final lap length and closing coordinate. The local Godot X/Z projection retains horizontal geometry, while latitude/longitude are null and elevation and gradient are zero. The embedded route is workout-only.

Regenerate from the committed source with:

```powershell
python tools/Wodra.RouteBuilder/generate-levels-raceway.py --osm src/IndoorCycling.Game/Data/Routes/levels-raceway.osm --gpx-output src/IndoorCycling.Game/Data/Routes/levels-raceway.centerline.gpx --output src/IndoorCycling.Game/Data/Routes/levels-raceway-workout.route-points.json
```

The scenery builder creates a broad continuous asphalt ribbon, grass runoff, white edge strips, selected kerbs, a start/finish stripe, simple safety barriers, unbranded paddock structures, and sparse trees. Geometry and environment are presentation-only; workout grade resistance remains zero and workout FIT output continues to omit position fields.
