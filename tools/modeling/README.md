# Wodra rider and road bicycle prototype

This is an owner-review prototype for issues #84, #85, and #86. It keeps the
existing runtime contact targets and cadence-driven pedal solver while
replacing the faceless chest/head with a generated adult human mesh. The bike,
clothing finish, helmet, and remaining articulated rider parts are authored by
the Wodra Blender script. The game ships only the GLB; Blender and MPFB are
authoring tools.

## Versions and generation

- Blender 5.2.2 LTS (glTF 2.0 exporter enabled).
- MPFB 2.0.18 extension, compatible with Blender 4.2+.
- Godot 4.6.2 .NET imports the resulting GLB using its glTF scene importer.

Install and enable MPFB from Blender's Extensions preferences. It uses its
bundled base mesh and macro targets; this model does not need MakeHuman, other
asset packs, online services, a Blender MCP server, or a Blender installation
on player machines. From the repository root run:

```powershell
& 'C:\Program Files\Blender Foundation\Blender 5.2\blender.exe' --background --python tools/modeling/build_wodra_cyclist.py
```

The script saves an editable Blender scene to
`tools/modeling/source/WodraCyclist.blend`, exports the game asset to
`src/IndoorCycling.Game/Assets/Models/WodraCyclist.glb`, and produces three
Blender geometry previews under `tools/modeling/previews/`. Open the Godot
project once after regeneration so it refreshes the imported GLB.

Run the in-game prototype on the real Rolling City route with production rider,
camera, terrain, buildings, vegetation, and golden-hour lighting:

```powershell
& 'C:\Tools\Godot\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64.exe' --path src/IndoorCycling.Game res://Tests/RiderBikePreview.tscn
```

The preview cycles the chase, side, and front three-quarter cameras. Three
variant riders animate together at 55, 85, and 115 rpm; the left rider coasts,
restarts, and steers across the lane. Close the window to stop it.

To record the 18-second review run and save three stills from the actual Godot
route scene:

```powershell
& 'C:\Tools\Godot\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64_console.exe' --path src/IndoorCycling.Game res://Tests/RiderBikePreview.tscn --write-movie "$env:TEMP\Wodra-rider-preview.avi" --fixed-fps 30 --resolution 960x540 --quit-after 540 -- --capture-preview
```

The captured stills are in `src/IndoorCycling.Game/Tests/previews/`:
`RiderBikePreview-Chase.png`, `RiderBikePreview-Side.png`, and
`RiderBikePreview-FrontThreeQuarter.png`.

## Runtime integration

`CyclistVisual` is used for local, AI, ghost, and interpolated remote riders.
All use the same imported meshes and runtime cadence, two-bone leg, wheel,
crank, steering, and lean updates. Rider variation changes jersey and bicycle
frame materials without making duplicate meshes. Cadence still comes from
ride telemetry; no power or cadence values are synthesized by the renderer.

The mesh retains the existing named GLB targets documented in
`CyclistVisual.TryBuildImportedModel`. At zero or stale cadence the established
animation policy settles into its coasting pose. Wheel rotation remains tied to
road speed and wheel radius, separately from crank cadence. The generated
human mesh adds no bones or runtime dependency; Blender's MPFB rig API was
evaluated, but the game's existing rider controller provides the practical
cadence/contact solution with lower runtime and asset cost for this pass.

## Provenance and licensing

- MPFB source code is GPL-3.0-or-later. It is not copied into the game or this
  repository. MPFB's bundled base meshes, targets, rigs, and textures are
  CC0-1.0. The generated character mesh is released as CC0 by its upstream
  authors; this export uses generated base-mesh data and Wodra-authored edits.
- The bike, jersey/shorts material treatment, helmet geometry, and Wodra
  modeling/export scripts are project-authored. The repository has no root
  source license yet, so no broader project license is implied here.
- No OpenSim model or data is included. OpenSim's application/API license does
  not determine the license on individual models or teaching materials. Its
  educational cycling material was reviewed as optional posture context, but
  it did not provide a needed runtime dependency or a data asset used here.
- Godot uses glTF 2.0/GLB, avoiding editor-time Blender calls in player builds.

## Review status and limits

The base torso/head now has facial and anatomical shape, while the established
procedural arms/legs remain visibly stylized. This is intended for owner visual
feedback, not a claim of production-finished clothing fit or biological joint
simulation. Inspect the side, rear, and three-quarter previews, then run the
actual Godot route preview as described in the PR. No external mesh, texture,
or model license was left unresolved in the distributed asset.

## P620 preview cost

Measured on the NVIDIA Quadro P620 with Godot 4.6.2 .NET, OpenGL Compatibility,
and the same Rolling City preview scene with three visible riders. The normal
interactive run reported 57.8 fps during its first five-second sample and 60.0
fps in the next two samples; the project frame cap is 60 fps. Its longest early
frame was about 146 ms while route scenery initialized, then steady samples
were 16.7 ms. A controlled run with the pre-existing procedural fallback in
the same scene reported 57.9 then 60.0 fps, with a similar 150 ms startup
spike. This is a capped-scene comparison, so it shows no measurable steady
frame-rate regression; it is not an uncapped throughput benchmark.

Each imported rider is about 21,656 triangles (64,968 across the three preview
riders), compared with the existing fallback estimate of 2,200 per rider.
The exported GLB is 444 KB; the editable Blender scene is 366 KB. The GLB has
45 mesh nodes and no image textures or runtime armature. The Rolling City
building mesh alone is about 249,873 triangles in this scene, so scenery remains
the larger triangle cost. GLB triangle count is the main cost increase to
consider for larger multiplayer fields.
