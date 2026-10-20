# Alpha.46 camera framing review

Owner diagnostic `wodra-diagnostics-20261020-141726-ad72a272.zip` showed the rider displaced to the right in Side at 699.672 m and low/right in Front three-quarter at 725.002 m. The original gameplay view and rear views remained acceptably centered.

The inspection presets placed the camera several metres to the side or forward of the rider but aimed at a fixed point near the rider origin. That target did not account for the camera's oblique direction, the complete rider/bicycle bounds, or visible HUD panels. The route/map and debug/dummy panels further reduced the usable viewport.

Side, rear and front inspection views now fit the visible rider/bicycle bounds to the current viewport frustum. The target point is derived from those world bounds and the largest clear rectangle computed from live HUD control rectangles. The safe rectangle updates when the viewport or visible panel layout changes. Gameplay presets retain their forward road look-ahead and existing collision/recovery path.

`RideCameraSceneReviewTest.tscn` instantiates the production `RideScreen`, starts Rolling City with the dummy trainer, jumps to the reported route position, and captures all six presets at 1500×900 and 1920×1080 with the debug panel both on and off. It projects all eight rider/bicycle bounds corners after each settled view change and fails if any point leaves the HUD-safe area. Run it with:

```powershell
$env:APPDATA = Join-Path $env:TEMP 'WodraCameraAppData'
$env:LOCALAPPDATA = Join-Path $env:TEMP 'WodraCameraLocalAppData'
& 'C:\Tools\Godot\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64_console.exe' `
  --path src/IndoorCycling.Game --resolution 1500x900 `
  --log-file (Join-Path $env:TEMP 'WodraRideCameraScene.log') `
  --scene res://Tests/RideCameraSceneReviewTest.tscn
```

The test writes the complete 24-image matrix to `artifacts/camera-alpha46-review/`. Representative actual RideScreen frames are checked in alongside this note. The test runs with the live ride HUD and route scenery; it does not replace owner review.
