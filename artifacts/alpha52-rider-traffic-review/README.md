# Alpha.52 production traffic review evidence

These captures were taken from the Godot 4.6.2 production `RideScreen` using the normal Rolling City route, route lighting, live HUD, 4 AI riders, 15 ambient cars, 10 pedestrians and a fake 220 W / 86 rpm trainer. The review scene drove to 720 m, waited for each camera transition to settle, checked camera-safe bounds and measured oriented imported-model envelopes at every sampled frame. It then checked 720 process frames, preserving every eighth frame in the source review before making the compact GIF.

- `ride-motion-750px.gif`: continuous 90-frame sample of the production scene while the ride advances; 9 seconds, covering route movement and camera-selected rider context.
- `ride-Side-debug-on.png` / `ride-Side-debug-off.png`: settled side view with debug overlay on/off.
- `ride-GameplayRefined-debug-on.png` / `ride-GameplayRefined-debug-off.png`: settled normal chase view with debug overlay on/off.
- `ride-FrontThreeQuarter-debug-on.png`, `ride-RearThreeQuarter-debug-on.png`, `ride-CloseRear-debug-on.png`, `ride-GameplayOriginal-debug-on.png`: additional production camera captures.

The final deterministic scene run passed with 17.194 m minimum model-envelope clearance between visible cyclists and 1.941 m minimum ambient-actor-to-cyclist clearance. The actor result is a minimum across the scene's sampled camera and motion checks, not a claim that every possible route, speed, traffic spawn or human steering pattern was exercised. Issues #28 and #102 remain open for owner physical acceptance.

The previous alpha.51 failure was not captured in this same harness and camera setup, so these images document the post-fix runtime state rather than a pixel-matched before/after pair. Owner-provided alpha.51 diagnostics remain the before evidence.
