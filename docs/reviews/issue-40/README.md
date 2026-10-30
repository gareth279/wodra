# Issue #40 production RideScreen review

Captures were taken by the graphical `WorkoutRideScreenReviewTest.tscn` against the production `RideScreen`, using the disposable profile/database and dummy input set to 175 W and 85 rpm. The four HUD captures were emitted at 960×540, 1280×720, 1920×1080, and 2560×1440 and the test verifies the PNG dimensions. They show the main metrics and workout panel together. Debug-on/off and interval-transition captures are included.

The route sequence was captured from a continuously running anticlockwise ride at approximately 0.85 km, 1.25 km, 1.70 km, 2.15 km, and just past 2.40 km. The session advanced through the start/finish seam into lap two. This is production-scene visual evidence, not a physical trainer test.

`before-training-studio.png` is the preceding local production RideScreen review capture showing the fictional Training Studio and old HUD placement. The owner screenshots referenced as 30 October were not attached in this task context, so that image is a local baseline and is not represented as the owner's capture.

The flat horizon and low-detail paddock objects are intentionally lightweight procedural scenery; the published map establishes the track outline, not building placement or landscape survey data. Start/finish chainage placement is approximate because the published schematic is not georeferenced.
