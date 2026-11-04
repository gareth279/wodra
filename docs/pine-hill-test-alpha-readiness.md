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

## Windows startup and route loading

The earlier “blank grey window” report was a first-run UI interpretation, not a failed application startup. A clean Windows Godot export reaches the normal Wodra home screen and opens **Manage Rider Profiles** because isolated user data has no active rider profile. Maximising the window exposed that modal; creating a disposable profile in the isolated test data reached the standard home and route catalogue. Godot logged `GameBootstrap initialised`, loaded RideScreen resources and initialized OpenGL on the Quadro P620 without C# exceptions, resource failures, or renderer errors.

The same normal application binary was launched against an isolated six-route package and an opt-in seven-route package. The main comparison export showed the normal catalogue without Pine Hill; the experimental package showed Pine Hill Test in the same catalogue (9 routes in this app configuration). The PCK includes the production scenes and imported route assets. The app was not pointed at the source-video folder. This demonstrates offline packaged startup and catalogue selection, not a publisher-built release installation.

The direct export does not have the local EOS client configuration, so normal online initialization and the canonical publisher’s full online smoke run remain unverified. The interactive packaged app reached Pine Hill in the catalogue, but pressing Start Ride led to the existing ANT+ setup screen because this PC has no configured trainer service. Dummy-trainer movement was verified separately in the production RideScreen review scene, which loads the route through the same catalogue and rendering path.

## Markings and visual review

At 1,371.735 m, the triangle is now authored as 3.0 m wide by 2.5 m long, centered 0.5 m road-right and 2.0 m forward of its route anchor. Its apex points toward approaching traffic. The 7.0 m give-way line is centered 0.25 m road-right and 5.25 m forward, leaving a 2.0 m modeled gap from the triangle base. The yellow kerb paint remains route-relative. The road centreline and authoritative LINZ-derived elevations were not moved.

The sign asset is placed from the recorded observation at 4.2 m road-left and 5.0 m forward, with a 0.78 m sign face and 2.42 m post. Production captures traced the mirror error to the glyph geometry’s local X projection in the imported route frame. The correction reflects only the legend strokes inside the sign face; it does not reverse the sign, route, or travel direction. The eight-compass transform regression remains in place. Fresh RideScreen renders at 1,368.5 m approach and 1,372 m chase view are in `%LOCALAPPDATA%\Wodra\PineHillEvidence\FinalReleaseReadiness`; the approach capture is the sign-legibility review. Sign placement is still approximate: camera/GPS registration uncertainty is about 2 m RMS and has not been surveyed.

The local review set, including start/approach/crossing/camera views and performance CSV, is in `%LOCALAPPDATA%\Wodra\PineHillEvidence\FinalReleaseReadiness`. It was captured using the production RideScreen. Evidence stays local because it contains private reconstruction details. No raw footage or unredacted source frames are in Git or route packages.

## Performance experiment

The supplied PR benchmark was 37.9 median FPS, 29.44 ms P95, 962 peak draw calls and 1,539 MiB peak card-wide VRAM. On the same Quadro P620/Godot GL Compatibility RideScreen review, the existing tree reduction ratio of 0.06 measured 38.58 median FPS, 28.08 ms P95, 962 peak draw calls, and 213.2 MiB peak Godot renderer video memory. The 0.05 tree candidate measured 39.09 median FPS, 28.59 ms P95, 962 peak draw calls, and 213.1 MiB renderer memory. Its frame-time and draw-call results did not show a repeatable improvement; the asset was restored to 0.06. With the corrected sign asset, the latest matched review measured 39.0 median FPS, 28.05 ms P95, 962 peak draw calls and 212.9 MiB Godot renderer video memory. The renderer metric is not a dedicated-card-wide VRAM sample.

The latest 1,147,861 peak primitive count and 962 peak draw calls show that broad scenery/terrain, rather than the two focal tree meshes or 27-mesh intersection asset alone, dominates scene cost. This iteration made no runtime optimization claim. The supplied card-wide peak VRAM measurement has not been reproduced with a reliable during-run sampler, so 1,539 MiB remains the last known peak; a fresh sampler measurement is outstanding.

## Validation state and blockers

- Debug and Release solution builds: passed (zero errors; dependency/nullable warnings remain).
- VisualRoute route scene, sign/marking compass transforms, and RouteBuilder suites: passed.
- Publisher route-package and smoke-isolation tests: passed. The standard six-route bundle is unchanged; the explicit seven-route alpha bundle verifies runtime asset hashes and excludes raw footage/reference frames.
- Production RideScreen review from the experimental bundle: 28 assertions passed, including catalogue preview/profile, authored assets, natural point-to-point finish, camera clearance at 395/775/1010/1372 m, and dummy-trainer movement through to the final 20 m.
- Latest local P620 sample: 39.0 median FPS, 28.05 ms P95, 962 max draw calls, 212.9 MiB Godot renderer video memory. Card-wide VRAM sampling is still unavailable.
- GitHub validation for implementation head `d1296a7` passed in Debug and Release; CodeQL and all four language analysis jobs also passed.
- No canonical publisher-built installation or normal online startup was tested because `%LOCALAPPDATA%\Wodra\eos-client.json` is absent. The local interactive app lacks ANT+ trainer setup; this is why its Start Ride path could not be used for the ride-through test.
- No release package was published. Public distribution of the video-derived fence texture remains owner-approval gated.

Release readiness remains conditional on a canonical publisher smoke run with the provisioned EOS configuration, a packaged-app ride test on a configured dummy trainer, updated CI, owner approval to distribute the fence texture, and owner acceptance of the fresh sign render. The route remains an experimental point-to-point reconstruction; local 8 m DEM resolution, approximate road width and unmodeled fine retaining walls, power lines, paint wear and residential details remain limitations.

### Recording artifact check

The final RideScreen harness run produced wodra-20261104-122011.fit (386 bytes) in the local evidence folder. Wodra's FitRideLoader accepted its FIT integrity checks and decoded 3 record frames plus 2 timer events (6 seconds, 11.6 m). The FIT recording remains local and is not included in the route package or repository.

