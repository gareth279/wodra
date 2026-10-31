# Issue 158 bike A/B performance run

Run date: 2026-10-31. Both assets were tested in isolated copies of the same Godot project using the production `RideScreen` and `RideJitterRealtimeReviewTest.tscn`, at a window of 1152 x 648, with VSync enabled (59.973 Hz), OpenGL 3 / Compatibility, Intel i7-9700, and NVIDIA Quadro P620. The runs used the same Dunedin route, debug build, harness, scene settings, and requested visible rider count. Each row is one 12-second animated-baseline sample; the harness also exercised disabled skeletal sampling and steer/coast/restart for each run.

The original model is the exact `WodraCyclist.glb` at the parent of the PR's CGTrader import. The optimized model is the final CGTrader V2 runtime GLB. Both were imported by Godot 4.6.2 in the isolated project before capture. No file in the owner worktree was swapped or modified for the original-model baseline.

| Visible riders | Bike | Frame delta median / p95 | Godot process time median / p95 | Draw calls median | Render objects median | Godot reported render video memory |
| ---: | --- | ---: | ---: | ---: | ---: | ---: |
| 1 | Original Wodra | 16.46 / 18.56 ms | 21.11 / 48.80 ms | 551 | 1,231 | 35.9 MB |
| 1 | Optimized CGTrader V2 | 15.87 / 18.06 ms | 19.55 / 41.84 ms | 570 | 1,253 | 129.3 MB |
| 12 | Original Wodra | 30.30 / 33.33 ms | 34.40 / 57.50 ms | 1,528 | 2,216 | 50.4 MB |
| 12 | Optimized CGTrader V2 | 31.94 / 37.50 ms | 37.04 / 57.10 ms | 1,740 | 2,448 | 143.8 MB |

## Interpretation and limits

- The optimized bicycle is not a performance win against the old Wodra bicycle. With one rider, sampled frame/process medians were similar and draw calls increased by 3.5%. With 12 riders, frame-delta median rose about 5.4%, draw calls about 13.9%, render objects about 10.5%, and the engine-reported render-video-memory counter rose about 93 MB. The render-video-memory counter is a whole-process Godot counter, not a per-bike allocation measurement.
- This demonstrates a modest but real 12-rider rendering/load regression on this older P620 system, alongside the model's substantial visual improvement and 44.8% triangle / 45.4% GLB-byte reduction from the first CGTrader import. The remaining model is still far heavier than the original Wodra bike. Owner acceptance should weigh that tradeoff.
- These are single runs, not a statistically controlled benchmark. Frame-delta p95 is influenced by VSync scheduling and scene activity. Godot's available counters supplied CPU process time, frame delta, draw calls, render objects and whole-process render-video-memory; no GPU frame-time/per-process VRAM instrumentation was available. No claim of GPU-time improvement is made.
- Godot's GLB importer has `meshes/generate_lods=true`, but this run did not record effective per-mesh LOD selection or triangle counts by camera distance. LOD generation is enabled; actual distant-rider LOD benefit remains unverified. Draw calls and render objects still increase with the imported asset at 12 riders.
- Production gameplay phases completed, but the current UI capture path renders a solid grey viewport in the available desktop capture despite live nonzero renderer counters. Therefore this run does not replace owner visual inspection of the final bar-tape material tweak or provide new final-state screenshots.

## Reproduction

From a Windows checkout with Godot 4.6.2 Mono installed, the same RideScreen harness now accepts a rider count and output directory:

```powershell
$godot = 'C:\Tools\Godot\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64_console.exe'
& $godot --path src/IndoorCycling.Game --resolution 1500x900 res://Tests/RideJitterRealtimeReviewTest.tscn -- `
  --bike-bench-visible-riders=12 `
  --review-output-dir="$PWD\artifacts\issue158-road-bike\performance\run-12"
```

Use `--bike-bench-visible-riders=1` for the one-rider case. To compare the original, use the parent-of-import GLB as the model in a disposable project copy, import it, and run the same commands; do not replace the working checkout asset during the comparison. The CSV records frame/process/physics timing, draw calls, render objects, and Godot's render-video-memory counter per sample.
