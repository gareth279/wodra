# Otago Central Rail Trail: Clyde–Alexandra gravel MVP

This bundled package is a route-geometry and gravel-environment MVP, not a survey-grade recreation of the rail trail. The trail association lists Clyde Railhead to Alexandra as 8 km one way. This package maps the Clyde–Alexandra section in both directions and closes a 16,135.6 m loop. Its lap seam is at the mapped point about 4 km from Clyde so the two railhead reversals are inside the lap.

## Sources and licenses

- Route alignment, surface tags, and mapped vegetation are from OpenStreetMap ways 133729829, 852195886, 852195887 and 44362734, retrieved from the OSM API. The route geometry follows the mapped cycleway; OSM does not establish a surveyed riding line or precise shoulder width. Attribution: © OpenStreetMap contributors, [ODbL 1.0](https://www.openstreetmap.org/copyright).
- Elevation and rendered surrounding terrain use the Otago/Central Otago 2022–2023 LINZ 1 m DEM collection. The collection identifies AAM NZ, Otago Regional Council and Toitū Te Whenua Land Information New Zealand as providers; attribution is retained in the route package. LINZ elevation products are released under [Creative Commons Attribution 4.0](https://creativecommons.org/licenses/by/4.0/).
- The Otago Central Rail Trail’s published [distance table](https://www.otagocentralrailtrail.co.nz/ride/what-youll-need-to-know-on-trail/distance-and-time/) gives Clyde Railhead to Alexandra as 8 km. The official [trail overview](https://www.otagocentralrailtrail.co.nz/plan/explore-our-trail) describes the wider 152 km trail. These establish route context, not detailed surface or elevation geometry.
- Wodra’s 2.8 m rideable surface and 3.3 m shoulder bed, including a 3 m turnaround-envelope fallback, are authored visual values, not measured trail widths. Turnarounds are smoothed within the visible surface. This MVP adds no gravel-specific resistance or handling.

## Geometry and profile

The selected course runs from a mapped point approximately 4 km from Clyde to the Alexandra railhead, returns through the same mapped section, continues to the Clyde railhead, then returns to the start. The loop therefore contains each direction and both trail-end turns. Two crossings of OSM way 852195886 (mapped bridge, `surface=unpaved`) fall at route chainages 10,500.0–10,610.0 m and 13,658.9–13,769.0 m. Their deck elevations are linearly bridged from the adjoining terrain over the mapped 110.0 m spans; this avoids treating a bridge deck as a terrain cliff, but does not reproduce bridge structure.

The packaged route is 16,135.5909 m, has 25.1 m generated positive ascent and a maximum absolute smoothed gradient of 2.08%. Elevation was sampled from the LINZ 1 m DEM at 5 m route intervals, smoothed across 30 m and differentiated over 25 m. Four no-data cells were locally interpolated. These values describe the source data and processing, not field-verified measurements. The full route profile and geometry fingerprint are in the embedded route-points package. No difficulty rating is assigned because these source-derived figures are not a validated rider difficulty scale.

Surface intervals retain route-relative chainage and source OSM way IDs. They classify mapped `gravel` and `unpaved` sections separately; both are presented with the MVP’s common visual gravel material. In the future, sealed or other authored sections can receive separate surface values without changing route discipline or environment.

## Regeneration

The checked-in `otago-central-rail-trail-clyde-alexandra.json` is the route authoring input. The raw OSM files and elevation cache are deliberately not shipped. To recreate them, run the helper scripts from the repository root, then run the route builder with the downloaded route ways as the routing graph and the broader map extract only as vegetation context:

```powershell
python tools/Wodra.RouteBuilder/download_otago_trail_source.py --output $env:TEMP\WodraGravelSource.osm
python tools/Wodra.RouteBuilder/download_route_context.py --output $env:TEMP\WodraGravelContext.osm
dotnet run --project tools/Wodra.RouteBuilder -- build `
  --pbf $env:TEMP\WodraGravelSource.osm `
  --config routes/authoring/otago-central-rail-trail-clyde-alexandra.json `
  --output routes/generated/otago-central-rail-trail-clyde-alexandra `
  --route-only true --with-elevation true `
  --vegetation-source $env:TEMP\WodraGravelContext.osm --cache-dir none
```

The four selected OSM ways are an explicit constrained route source, not an inferred bike-network route. The package and generated terrain/vegetation sidecars are embedded in the game assembly so an installed build does not need the raw extracts, LINZ cache, or authoring tools.
