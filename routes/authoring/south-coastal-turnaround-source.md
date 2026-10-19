# South Coastal turnaround source and regeneration

The route builder used the Geofabrik New Zealand snapshot dated **18 October 2026**:

- Download: `https://download.geofabrik.de/australia-oceania/new-zealand-261018.osm.pbf`
- Local source filename: `new-zealand-261018.osm.pbf`
- SHA-256: `1e80302e6b5476b58fcd8cc4d0f90aa9c579175068d8631170b3ac50df703328`
- Size: 405,131,818 bytes
- Source attribution and license: © OpenStreetMap contributors, ODbL 1.0. Geofabrik distributes the extract; the map data remain OpenStreetMap data.

The prior route had exact directed road retraces at two anchor joins. The source ways are `John Wilson Ocean Drive` (way 1462169549; `unclassified`, two lanes, asphalt) and `Victoria Road` (way 511224234; `tertiary`, two lanes, 50 km/h, asphalt). Neither way has a `width` tag in the snapshot. At the joins the directed graph offers no separate connected turn path, so shortest-path routing turned back along the approach. The mapped source graph and tags were retained; the route builder now detects only exact short reverse retraces and writes a continuous U-turn that stays within the configured road corridor.

The South Coastal authoring configuration sets `routeDataVersion` to 2 and an explicit 7 m turnaround corridor fallback. This is the documented standard-road fallback, not a width measurement from OSM. The generated turn uses a 1.54 m radius and a 14 m merge, with a maximum lateral displacement of 3.08 m. The algorithm receives the authored corridor width; future road, gravel, cycleway, and MTB routes must set their own width and provenance when this fallback is unsuitable. It does not infer road width from GPS.

To reproduce the package from a fresh checkout, keep the PBF outside Git and regenerate the full route and all chainage-indexed sidecars:

```powershell
$pbf = Join-Path $env:TEMP 'WodraSourceData\new-zealand-261018.osm.pbf'
dotnet run --project tools/Wodra.RouteBuilder -- build `
  --pbf $pbf `
  --config routes/authoring/dunedin-south-coastal-loop.json `
  --output routes/generated/additional `
  --cache-dir routes/cache
```

The route builder regenerates OSM geometry and scenery, samples the route and roadside layers from the configured LINZ raster cache, updates the geometry fingerprint, and preserves OSM attribution in the package. The PBF checksum should be verified before use. Do not commit the raw PBF.
