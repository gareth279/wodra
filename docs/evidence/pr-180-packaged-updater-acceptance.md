# PR #180 packaged updater acceptance

Run on Windows against source commit `fd8285ce3b6de67b04ac3d4a7bd55b71c83bfe0b`.

## Scope and environment

- Restored the repository-pinned Velopack CLI with `dotnet tool restore` (`vpk` 1.2.161).
- Built Windows x64 Release application payloads with the existing Godot export and Velopack packaging path. Test builds used `EnableEpicOnlineServices=false` and `EnableWodraLocalUpdateFeed=true`.
- All update feeds and installers were local and disposable, under `%LOCALAPPDATA%\Temp\WodraChannelAcceptance-fd8285c`. No GitHub release, tag, public feed, or production publisher invocation was used. No EOS credentials were supplied or fabricated.
- This demonstrates real Velopack package and updater behavior without EOS; it is not a production signed/EOS release-package validation.
- Local installers use the real Wodra Velopack identity. Installation was directed to a temporary install directory and application profile paths were isolated for the updater runs. A Setup install still registers the shared per-user Wodra uninstall entry and shortcut, so the real uninstall registration was restored to the original `%LOCALAPPDATA%\Wodra` Alpha 0.1.0-alpha.90 installation at the end. The existing shortcut was verified to target that installation.
- During the initial Setup smoke launch, one app start occurred before the test environment overrides were applied and it opened the existing user profile. It was used only to verify initial Alpha identity; no user settings or ride data were intentionally changed. That launch may have added ordinary diagnostic/log files to the existing profile. All subsequent profile/update tests used the disposable profile.

## Results exercised

| Scenario | Result |
|---|---|
| Alpha Setup initial channel | Pass. Installed Alpha 0.1.0-alpha.95; Settings reported version and Alpha selection. |
| Existing Alpha settings migration | Pass. Removed the new channel fields from the isolated settings file; the existing Alpha package resolved the unset channel to Alpha and checked the Alpha feed. |
| Alpha → Stable, Stable older than installed | Pass. Stable feed containing 0.0.9 did not downgrade Alpha 0.1.0-alpha.95; UI explained the newer Alpha was retained. |
| Alpha → Stable, newer Stable exists | Pass. Stable 0.1.0 was discovered, downloaded to completion, applied by Velopack and restarted. Settings showed Stable 0.1.0 afterward. |
| Stable → Alpha | Pass. Alpha 0.1.1-alpha.1 was offered, downloaded, applied and restarted. Settings showed Alpha 0.1.1-alpha.1 afterward. |
| Rider data preservation | Pass for the isolated profile. The same `Acceptance Rider (Test)` profile remained available after both apply/restart transitions. No physical trainer or production profile data was used. |
| Stable feed older than later Alpha | Pass. With Alpha 0.1.1-alpha.2 installed and Stable 0.1.0 selected, the UI said there was no newer Stable release and the current version would remain installed. |
| Staged package/channel change | Pass. Using the pinned Velopack API and a test locator, downloaded Alpha 0.1.1-alpha.3 without applying it. Wodra detected it as staged on Alpha. Switching to Stable cleared the staged-channel association and did not offer/apply that package. Switching back to Alpha exposed it as an available update, not as a staged update. |
| Separate channel manifests | Pass. The local feeds contained channel-specific `releases.alpha.json` / `assets.alpha.json` and `releases.stable.json` / `assets.stable.json`; the app queried only the selected channel. |

The real application update path was used for the Alpha 0.1.0-alpha.95 → Stable 0.1.0 and Stable 0.1.0 → Alpha 0.1.1-alpha.1 download/apply/restart tests. The staged-package preparation used the actual Velopack 1.2.161 API and a locator scoped to the disposable install, not a mocked updater.

## Representative package evidence

All paths below are inside the disposable local test root and have no public-feed role.

| Artifact | SHA-256 |
|---|---|
| Alpha 0.1.0-alpha.95 full package | `680B5450A593C0FBCB3021D39F7A97FA266FFCA6AB3E5D3D48F49242C4102829` |
| Alpha Setup | `DFACB7403F5D3A7E6395D8F37CDE01EE9C00F2E33A6DB614C0B7F690D7B7CF62` |
| Stable 0.1.0 full package | `F14D0FECBE910A8B4FF5BCB3A34EAA4ED6CEC32BD6BD90584CD778071FC0B7F1` |
| Stable Setup | `7D96D1CE9759AF806F37A0346E14C90BC8DBE66FAED8B9B13F7B6D72BB7FD34F` |
| Alpha 0.1.1-alpha.1 full package | `9737C059068E8B102F78FA8BE8B61CAA100A6E9D0D3C766B8507B74F73CAEF79` |
| Alpha 0.1.1-alpha.3 staged full package | `D25A9950E0AA82009D43A5B8A6D2B526E148809E1CE6B51D67E63F27CCE7F98B` |

The Alpha and Stable test packages share a test payload and were built at different package versions. These tests make no claim that differently versioned production packages will be byte-identical or that the test artifacts are the promoted production build.

## Not exercised / remaining boundary

- **Fresh Stable Setup default is not yet verified by installing the Stable Setup.** Its package and `stable` channel metadata were generated and inspected, but installing another Wodra Setup under the same Windows account overwrites the shared per-user Wodra uninstall registration and shortcut. The next required prerequisite is a disposable Windows user profile or VM so this installer can be run without altering the real account registration. A registered GitHub runner is not required; local Windows testing is available once that isolated profile/VM is used.
- These test packages intentionally omitted EOS and did not validate production signing, licensed EOS configuration, the protected private release runner, or production publisher gates. Those remain the requirements for full production package validation.
- No physical BLE FTMS/ANT+ trainer, heart-rate/steering device, Strava upload, or multiplayer service test was performed. These remain separate first-Stable product acceptance checks; they are not claimed as passed by the channel/updater acceptance run.
- Screenshots from the interactive Settings/update flow were observed during the run but were not saved as standalone image files. The state transitions and result are recorded above; the local logs and feeds remain in the disposable temp root on the test host.

## Fresh Stable Setup follow-up

On 2026-11-06 the Stable Setup artifact was re-verified before any execution. Its SHA-256 is `7D96D1CE9759AF806F37A0346E14C90BC8DBE66FAED8B9B13F7B6D72BB7FD34F`, matching the package evidence above. The Alpha 0.1.1-alpha.3 package hash was also recomputed from the actual local nupkg; the complete 64-character digest above corrects the truncated value previously recorded.

The fresh Stable Setup was **not run** during this follow-up. The available Windows host has only the owner account in an interactive session, and that account lacks elevated administrator rights to provision a disposable local user or enable/query Windows Sandbox. The already open remote desktop environments are not identified as disposable test VMs. The protected release VM is not an acceptable substitute because this test would execute a PR-built package there. Therefore, the exact missing prerequisite is access to a separate disposable Windows user profile or VM where the local Setup can be launched without touching the owner account. This is an isolation prerequisite, not a missing GitHub runner.

No fresh-install screenshots or runtime logs were captured because the installer was not launched. The existing owner Alpha installation, shortcut, uninstall registration, settings and rider data were not touched during this follow-up.

## Merge assessment

The packaged channel-switching, update, restart, downgrade-prevention, migration, local-feed separation, staged-update safety, and isolated rider-profile scenarios above passed. The specifically requested fresh Stable Setup acceptance remains outstanding and must be run in a separate disposable Windows profile/VM before packaged acceptance is complete. No release has been published.
