# Alpha.90 rider visual correction

These are unannotated 1920×1080 screenshots captured from the production `RideScreen` using Godot 4.6.2, the existing imported rider, and the Quadro P620 renderer. The run exercised the live rider and full crank-cycle regression at 85 rpm.

## Findings and correction

The green jaw came from the imported Human mesh's `COLOR_0` jersey channel, not shell intersection. Hiding both generated garment shells left the green patch visible. The Human mesh has 4,103 vertices weighted to `head` or `neck03`; the runtime now clears their jersey channel before the outfit shader runs. The regression checks zero jersey-colored vertices in this protected face and upper-neck set, and zero head/upper-neck weights on all generated jersey surfaces. The rendered shell-hidden capture confirms the natural face and jaw remain skin colored.

The supplied helmet shell fit remains candidate B (+10 mm forward, 12° pitch, existing vertical fit). Only the two forward strap branches were rerouted posteriorly and slightly outward to keep the cheek clear and place their junction below the ear. The chin strap and buckle remain integrated in the supplied helmet assembly.

The separate neck-plane extrusion caused the diagonal raised strip in side view. It was removed; the fitted jersey's own neckline is retained. Shorts hem contours are smoothed on both the garment boundary and gripper, and grippers now use a pure shorts color channel and the same outfit shader/material as the shorts for a continuous boundary.

## Face and helmet

- [Shells hidden: face and jaw skin check](face-teal-shells-hidden.png)
- [Front, teal](face-front-teal.png), [navy](face-front-navy.png), [white](face-front-white.png)
- [Left profile](face-left-profile-phase-000.png), [right profile](face-right-profile-phase-000.png), [three-quarter](face-three-quarter-phase-000.png)
- Additional face views through four head phases: `face-left-profile-phase-*`, `face-right-profile-phase-*`, and `face-three-quarter-phase-*`.

## Collar and garments

- Collar: [front](garment-collar-front.png), [side](garment-collar-side.png), [rear](garment-collar-rear.png)
- Jersey: [front](garment-jersey-front.png), [side](garment-jersey-side.png), [rear](garment-jersey-rear.png), [three-quarter](garment-jersey-three-quarter.png)
- Shorts: [front](garment-shorts-front.png), [side](garment-shorts-side.png), [rear](garment-shorts-rear.png)
- Eight crank phases at 45° intervals: `garment-shorts-side-phase-000.png` through `garment-shorts-side-phase-315.png`.

## Full rider

- [Front](fit-rider-front-full.png), [side](fit-rider-side-full.png), [rear](fit-rider-rear-full.png), [three-quarter](fit-rider-front-three-quarter-full.png)
- Live crank phases: `rider-side-full-phase-000.png`, `090`, `180`, and `270`.

The final Godot run passed the imported-scene review, selected-color assertions, face mask regression, and full-cycle rider/pedal/hand checks. Godot reports existing GLES resource cleanup warnings during test-process teardown after the PASS result.
