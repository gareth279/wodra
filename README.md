# Wodra

## Route data

The Dunedin One-way: Oval to Gardens Loop uses bundled OSM-derived route geometry. © OpenStreetMap contributors, ODbL 1.0. Its generated elevation profile uses the LINZ Otago — Dunedin and Mosgiel LiDAR 1 m DEM (2021), licensed CC BY 4.0 by Otago Regional Council; AAM NZ is the producer and Toitū Te Whenua Land Information New Zealand hosts/processes the data. The runtime embeds only route point/elevation values and stays offline. RouteBuilder caches source COGs under `routes/cache/elevation/`; install `tools/Wodra.RouteBuilder/elevation/requirements.txt` to regenerate the profile. After reviewing regenerated outputs, copy the `.route-points.json` into `src/IndoorCycling.Game/Data/Routes/` and rebuild the game.

## Developer commands

```powershell
dotnet build wodra.sln -c Debug -m:1
dotnet build wodra.sln -c Release -m:1
dotnet run --project tests/IndoorCycling.VisualRoute.Tests -c Debug
dotnet run --project tests/IndoorCycling.Recording.Tests -c Debug
dotnet run --project tests/IndoorCycling.Persistence.Tests -c Debug
dotnet run --project tests/Wodra.RouteBuilder.Tests -c Debug
```
