# Wodra

## Route data

The Dunedin One-way: Oval to Gardens Loop uses bundled OSM-derived route geometry. © OpenStreetMap contributors, ODbL 1.0. Its generated elevation profile uses the LINZ Otago — Dunedin and Mosgiel LiDAR 1 m DEM (2021), licensed CC BY 4.0 by Otago Regional Council; AAM NZ is the producer and Toitū Te Whenua Land Information New Zealand hosts/processes the data. The runtime embeds only route point/elevation values and stays offline. RouteBuilder caches source COGs under `routes/cache/elevation/`; install `tools/Wodra.RouteBuilder/elevation/requirements.txt` to regenerate the profile. After reviewing regenerated outputs, copy the `.route-points.json` into `src/IndoorCycling.Game/Data/Routes/` and rebuild the game.

## Multiplayer

Phase 1 server setup, two-client dummy-trainer instructions, protocol and limitations are documented in [docs/multiplayer-phase1.md](docs/multiplayer-phase1.md).
Phase 2 remote cyclist rendering, Catch Up, FIT segmentation and two-client checks are documented in [docs/multiplayer-phase2.md](docs/multiplayer-phase2.md).
Phase 3 automatic opt-in online riding, route presence and reconnection are documented in [docs/multiplayer-phase3.md](docs/multiplayer-phase3.md).
EOS Phase 4 integration, external SDK setup, Portal configuration, and remaining live validation requirements are documented in [docs/epic-online-services-phase4.md](docs/epic-online-services-phase4.md).
Windows x64 alpha packaging, archive validation, EOS credential staging, and GitHub draft release steps are documented in [docs/windows-alpha-release.md](docs/windows-alpha-release.md).
The shared route identity, geometry, chainage, coordinate, width, and scenery provenance contract for bundled and future imported routes is documented in [docs/route-geometry-contract.md](docs/route-geometry-contract.md).
Bluetooth FTMS trainer discovery, connection-screen status, recovery lifecycle, and ANT+ integration points are documented in [docs/trainer-connectivity.md](docs/trainer-connectivity.md).
Strava OAuth/token-broker requirements and issue #12 release prerequisites are documented in [docs/strava-integration.md](docs/strava-integration.md).

## Developer commands

```powershell
dotnet build wodra.sln -c Debug -m:1
dotnet build wodra.sln -c Release -m:1
dotnet run --project tests/IndoorCycling.VisualRoute.Tests -c Debug
dotnet run --project tests/IndoorCycling.Recording.Tests -c Debug
dotnet run --project tests/IndoorCycling.Persistence.Tests -c Debug
dotnet run --project tests/Wodra.RouteBuilder.Tests -c Debug
```
