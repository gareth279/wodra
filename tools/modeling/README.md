# Wodra cyclist prototype

This is an owner-review prototype for PR #89 and issues #84–#86. It contains a
complete MPFB human mesh, the default weighted skeleton, fitted road-bike
geometry, a baked three-revolution pedal action, and an in-game cadence-driven
preview. It remains a work in progress pending owner visual approval.

## Verified authoring tools

- Blender 5.2.2 LTS, installed at `C:\Program Files\Blender Foundation\Blender 5.2\blender.exe`.
- MPFB 2.0.18, installed in Blender 5.2's persistent `user_default` extension
  repository at `%APPDATA%\Blender Foundation\Blender\5.2\extensions\user_default\mpfb`.
- Godot 4.6.2 .NET.
- FFmpeg 9.0.2 (`Gyan.FFmpeg.Shared`) for encoding the Blender PNG sequence.

Install the reviewed FFmpeg package with:

```powershell
winget install --id Gyan.FFmpeg.Shared --exact --source winget --accept-source-agreements --accept-package-agreements
```

MPFB 2.0.18 is the official extension release for Blender 4.2 and later. Its
official versioned package is 45,956,992 bytes with SHA-256
`c075b3e9b94b8e5556a5f03f4653602d02ea306de08126e8f55ed6dee28a2dad`.
The download URL and hash are published by Blender's official extension index.
The archive was verified before installation. To repeat the installation in
the current Windows account:

```powershell
$archive = Join-Path $env:TEMP 'add-on-mpfb-v2.0.18.zip'
$url = 'https://extensions.blender.org/download/sha256:c075b3e9b94b8e5556a5f03f4653602d02ea306de08126e8f55ed6dee28a2dad/add-on-mpfb-v2.0.18.zip'
Invoke-WebRequest -Uri $url -OutFile $archive
$expected = 'c075b3e9b94b8e5556a5f03f4653602d02ea306de08126e8f55ed6dee28a2dad'
if ((Get-FileHash $archive -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expected) { throw 'MPFB archive hash mismatch' }
& 'C:\Program Files\Blender Foundation\Blender 5.2\blender.exe' --command extension install-file -r user_default -e $archive
```

The dependency is installed in the current Blender user's persistent extension
directory; only the downloaded staging archive is temporary. MPFB initializes
its own user data at `%APPDATA%\Blender Foundation\Blender\5.2\extensions\.user\user_default\mpfb`.

## Rebuild and export

From the repository root, generate a separate WIP source blend, GLB, and preview
sequence without overwriting the accepted static source blend or its previews:

```powershell
& 'C:\Program Files\Blender Foundation\Blender 5.2\blender.exe' --background --python tools/modeling/build_wodra_cyclist.py -- tools/modeling/exports/WodraCyclist-animated-candidate.glb tools/modeling/checkpoints/WodraCyclist.animated-candidate-01.blend
```

The script uses MPFB's documented `LOWERUPPER` leg and arm helpers, saves a
36 fps `PedalCycle` action lasting about 3.03 seconds in GLB and covering three
complete revolutions,
exports the full weighted skeleton and human mesh, and renders new previews at:

- `tools/modeling/previews/static_fit_candidate_03/fit_{side,front,rear}.png`
- `tools/modeling/previews/crank_phase_checks_03/crank_{000,090,180,270}.png`
- `tools/modeling/previews/cadence_preview_01/frames/frame_####.png`

The animation preview uses side lighting and camera framing. Encode the image
sequence as an H.264 review clip with FFmpeg:

```powershell
& "$env:LOCALAPPDATA\Microsoft\WinGet\Packages\Gyan.FFmpeg.Shared_Microsoft.Winget.Source_8wekyb3d8bbwe\ffmpeg-9.0.2-full_build-shared\bin\ffmpeg.exe" -framerate 36 -i tools/modeling/previews/cadence_preview_01/frames/frame_%04d.png -c:v libx264 -crf 22 -pix_fmt yuv420p -movflags +faststart tools/modeling/previews/cadence_preview_01/pedal_cycle_side.mp4
```

To regenerate the runtime GLB directly, pass
`src/IndoorCycling.Game/Assets/Models/WodraCyclist.glb` as the first argument.
Open/import the Godot project after changing the GLB so its scene importer
refreshes the asset.

## Godot runtime and review

`CyclistVisual` imports the `Skeleton3D` and `PedalCycle` `AnimationPlayer` clip.
It samples the baked skeletal cycle from telemetry cadence; when cadence reaches
zero it holds the current pose and resumes from the same crank phase. Crank
rotation follows cadence independently, and both wheels continue to follow road
speed and wheel radius. The renderer consumes ride state and does not synthesize
trainer telemetry or change ride physics. The existing camera and route behavior
remain in place.

Run the actual Rolling City route preview:

```powershell
& 'C:\Tools\Godot\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64.exe' --path src/IndoorCycling.Game res://Tests/RiderBikePreview.tscn
```

It shows three riders at 55, 85, and 115 rpm; the left rider coasts and restarts,
and the production chase, side, and front three-quarter cameras cycle. For an
18-second local recording and actual in-game stills:

```powershell
& 'C:\Tools\Godot\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64_console.exe' --path src/IndoorCycling.Game res://Tests/RiderBikePreview.tscn --write-movie "$env:TEMP\Wodra-rider-preview.avi" --fixed-fps 30 --resolution 960x540 --quit-after 540 -- --capture-preview
```

The stills are saved in `src/IndoorCycling.Game/Tests/previews/` as
`RiderBikePreview-Chase.png`, `RiderBikePreview-Side.png`, and
`RiderBikePreview-FrontThreeQuarter.png`.

## Provenance and licensing

- MPFB code is GPL-3.0-or-later. Its base mesh, default rig, and related bundled
  assets are CC0-1.0. MPFB is an authoring dependency only and is not shipped in
  the game.
- The generated human and weighted rig come from MPFB; the cycling pose, fitted
  shoes, bicycle, clothing materials, helmet geometry, and export scripts are
  Wodra-authored. No external human model or motion-capture animation is used.
- The game ships the GLB only and has no Blender or MPFB runtime dependency.

## Current review state

The static candidate and sampled 0°, 90°, 180°, and 270° phases are provided for
visual inspection. The MPFB rig has 171 bones and the human mesh has 291 weight
groups. The side render shows saddle contact, forward torso lean, bent elbows,
hood contact, and feet on the pedals without bone stretching. The current asset
is a prototype; owner visual review remains required before merging or releasing.
