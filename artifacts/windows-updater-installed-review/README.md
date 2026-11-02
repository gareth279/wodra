# Installed Windows updater review

This evidence records an isolated, packaged Windows upgrade using the app's
normal Velopack installer and restart flow. Packages were built with
`publish.ps1` into a temporary local feed; no GitHub release or public feed was
created.

## Installed app result

- Godot runtime: 4.6.2.
- Package transition: `0.1.0-alpha.9902` → `0.1.0-alpha.9903`.
- The 9902 app detected 9903, downloaded and staged the full package, then
  restarted through Velopack into 9903. The updated home screen showed the
  preserved `Updater Review (Test)` profile and `Wodra is up to date
  (0.1.0-alpha.9903)`.
- The test ride used Dummy Trainer at 85 rpm on `Dunedin One-way: Oval to
  Gardens Loop`. It was finalized as a 79 second, 0.56 km ride and a FIT file.
- A manual check on 9903 confirmed the no-update state.

## Recovery paths observed

- A missing full package asset produced a visible install error that said the
  current install and rider data were unchanged. Restoring the package and
  choosing **Retry Download and Install** staged the update successfully.
- With the staged package ready, a temporarily unavailable Velopack
  `Update.exe` produced a visible apply error and **Retry Apply and Restart**.
  Restoring the helper and retrying installed 9903 and restarted the app.
- A malformed local feed produced a visible check failure and retry action on
  9902. Restoring a valid feed let retry discover 9903.
- On the clean app restart before update, the home screen displayed the
  available update while automatic trainer, heart-rate and Sterzo discovery
  was running. Discovery completed without blocking the updater. No physical
  trainer, heart-rate monitor or Sterzo was attached during this test.
- The ride summary returned to route selection with the dummy trainer session
  still configured, so apply was correctly deferred until a fresh safe app
  session. The automated visibility regression also verifies active-ride
  deferral.

## User-data comparison

The test used isolated `APPDATA` and `LOCALAPPDATA` roots. These hashes were
recorded with the app closed before and after the install; all matched.

| Data | Before SHA-256 | After SHA-256 | Result |
| --- | --- | --- | --- |
| Settings JSON (1,737 bytes) | `1A06363CBF46D3CC4238DAF062FD33F5E25CB782C14908F1D7A6DBEE63E2DA2C` | same | preserved |
| Ride database (110,592 bytes) | `4D347F490D8C3A6978D66A7834910FE4C5280F6271ACB8CB07EE4297B3C9FCB2` | same | profile and ride preserved |
| Preservation sentinel (75 bytes) | `8F390F1C48C26DB1307F5EC350A03219A5D58F9BFA8DADD3E1C229F1AABF5B96` | same | preserved |
| Test ride FIT (2,414 bytes) | `A94BC94D5E1AAA5BCDD095CBEA65765A089BCBC7A4A7C121E184C84D5DA834BE` | same | preserved |

The database had one test profile and one completed ride on both sides of the
upgrade. The FIT remained in the app's normal `Documents/Wodra` location.

## Regressions and limits

- `Wodra.Update.Tests` passed in Debug and Release, including coalesced checks,
  hidden UI, active-session deferral, bounded timeout, offline failure/retry,
  interrupted download retry and apply failure.
- Godot's `UpdaterServiceVisibilityReviewTest.tscn` passed, including the
  minimum Settings row height, hidden startup parent during download, safe
  apply deferral and visible retry after apply failure.
- The packaged UI was exercised against a local feed with unavailable and
  malformed feed/package states. External internet was not disabled on the
  Windows host, and no physical cycling sensors were attached; network errors
  are covered by the service regression suite.
- `events.jsonl` contains sanitized application updater and device-discovery
  events from the packaged review. It omits session IDs, local paths and raw
  exception messages.
