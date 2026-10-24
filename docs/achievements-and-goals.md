# Achievements and personal goals

Achievements observe completed local ride activities. `RideSessionNode` supplies eligibility evidence and live notification previews; it does not change the authoritative simulation, trainer commands, measurements, FIT samples, or camera. `SqliteRideHistoryRepository` stores one contribution per existing activity ID and awards once per profile. The v4 migration creates the ledger without inferring eligibility from older history.

## Stable definitions

Milestone IDs and thresholds are centralized in `AchievementCatalog`. Route completion IDs use `route.first-complete.<stable-route-id>`. The Dunedin road collection uses `collection.dunedin-road-routes.v1`; its v1 membership is the four IDs listed by the catalog. To change collection membership, define a new collection version and ID. Do not remove or rewrite earned awards when route packages change.

Only routes explicitly marked as supporting verified completion can award route badges. The current verifier requires continuous authoritative progress over a full reference lap and a crossed lap boundary. A pause contributes no movement; debug repositioning and multiplayer catch-up reset the coverage segment. Out-and-back route packages do not opt into lap-based completion. A saved partial ride can still add its actual distance, active duration, and route-derived climbing to totals and goals.

The environment and activity mode are stored separately. Road loop completion can award route badges. Velodrome rides can contribute general distance, active time, and route-derived climbing, but not road route or collection badges; visual banking is not route elevation. Only the local ride is evaluated. Fake-trainer and debug sessions are ineligible, while test profiles remain isolated by profile ID.

## Goals and notifications

One optional distance or climbing goal is stored per profile. Replacing a goal starts it at the replacement timestamp; cancelling removes only the goal. Its progress uses eligible saved activity contributions begun from that timestamp onward. In-ride notifications are a display-only preview and obey the profile's Game features preference; persisted tracking does not. The history/profile achievements view and end-of-ride summary read the same saved awards and totals.

No existing rides are backfilled because their route coverage and dummy/debug provenance are not recorded with sufficient evidence. Existing ride history and activity IDs remain intact.
