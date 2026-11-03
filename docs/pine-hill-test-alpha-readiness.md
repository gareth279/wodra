# Pine Hill Test alpha readiness

## Distribution option

The normal publisher bundle remains six routes. Pine Hill is opt-in only:

```powershell
. .\tools\WodraBuiltInRoutePackages.ps1
Write-WodraBuiltInRouteBundle -RepositoryRoot (Get-Location).Path `
  -DestinationRoot $env:TEMP\PineHillAlpha\Routes -Version 0.1.0-alpha.90 `
  -IncludeExperimentalRoutes
```

The publisher has a matching `-IncludeExperimentalRoutes` switch, default off. An opted-in bundle adds one route entry with `isExperimental: true`, all eight route JSON sidecars, `ATTRIBUTION.md`, and hashed runtime asset copies under `runtime-assets/`. The route's `res://` scene references still require the corresponding assets in the Wodra application PCK. The clean-package RideScreen review loaded the route from a fresh generated `Routes` folder, independent of a user-installed package.

The package includes no video, GPX/FIT recording, extracted frame, or local comparison image. The fence texture is a crop containing only the timber fence; local inspection found no people, vehicles or plates in the texture. Public distribution of this source-derived texture still needs the footage owner's explicit approval. No public alpha package was built.

## Markings and visual review

At 1,371.735 m, the triangle is now authored as 3.0 m wide by 2.5 m long, centered 0.5 m road-right and 2.0 m forward of its route anchor. Its apex points toward approaching traffic. The 7.0 m give-way line is centered 0.25 m road-right and 5.25 m forward, leaving a 2.0 m modeled gap from the triangle base. The yellow kerb paint remains route-relative. The road centreline and authoritative LINZ-derived elevations were not moved.

The sign asset is placed from the recorded observation at 4.2 m road-left and 5.0 m forward, with the existing 0.78 m sign face and 2.42 m post. Direction math is covered for eight compass headings. **The actual production capture still shows the GIVE WAY legend mirrored/read incorrectly.** This is not release-ready and needs a rendered-asset orientation correction followed by another RideScreen capture. Its approximate position and scale now match the observation record, but camera/GPS registration uncertainty is about 2 m RMS; those offsets are not surveyed.

The local review set, including start/approach/crossing/camera views and performance CSV, is in `%LOCALAPPDATA%\Wodra\PineHillEvidence\FinalReleaseReadiness`. It was captured using the production RideScreen from an opt-in route bundle. A separate Godot-exported Windows application was launched with the newly generated clean route bundle, but displayed only a blank gray window and never reached the route catalogue. Therefore clean packaged-app installation and selection have **not** passed; the RideScreen harness result does not substitute for that check. No raw footage or private reference frames are in the repository.

## Performance experiment

The supplied PR benchmark was 37.9 median FPS, 29.44 ms P95, 962 peak draw calls and 1,539 MiB peak card-wide VRAM. On the same Quadro P620/Godot GL Compatibility RideScreen review, the existing tree reduction ratio of 0.06 measured 38.58 median FPS, 28.08 ms P95, 962 peak draw calls, and 213.2 MiB peak Godot renderer video memory. The 0.05 tree candidate measured 39.09 median FPS, 28.59 ms P95, 962 peak draw calls, and 213.1 MiB renderer memory. Its frame-time and draw-call results did not show a repeatable improvement; the asset was restored to 0.06.

The current 1,145,157 peak primitive count and 962 peak draw calls show that broad scenery/terrain, rather than the two focal tree meshes or 27-mesh intersection asset alone, dominates scene cost. This iteration made no runtime optimization claim. The supplied card-wide peak VRAM measurement has not been reproduced with a reliable during-run sampler, so 1,539 MiB remains the last known peak; a fresh sampler measurement is outstanding.

## Validation state and blockers

- Debug and Release solution builds: passed (zero errors; warnings are pre-existing dependency/nullable warnings).
- VisualRoute suite and route marking/compass-direction checks: passed.
- Publisher package test: standard six-route bundle unchanged; explicit seven-route alpha bundle and copied-asset SHA-256 checks passed.
- Production RideScreen from the clean package: 28 assertions passed, including catalogue selection, preview/profile, authored assets, natural point-to-point finish, camera clearance at 395/775/1010/1372 m, and dummy-trainer movement.
- Latest CI at the previously pushed PR head passed all listed jobs. New local changes require a push and a fresh CI run.
- Clean route-bundle generation and sidecar/runtime-asset checks passed, but the exported-app smoke test failed at startup presentation (blank gray window; no normal route catalogue). The exact blocker is unresolved and must be fixed before alpha packaging.
- No release package was built or published.

The mirrored sign legend is a visual acceptance blocker. The fence texture remains owner-approval gated for public distribution. This remains an experimental point-to-point reconstruction; local 8 m DEM resolution, approximate road width and unmodeled fine retaining walls, power lines, paint wear and residential details remain limitations.
