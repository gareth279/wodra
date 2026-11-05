# Windows update channels

Wodra has exactly two update channels, using the same app identity, per-user installation, data directory, updater, and safety checks:

| Channel | Feed | Intended users | GitHub release |
|---|---|---|---|
| Stable | `releases.stable.json` | Normal use | Published release, not a prerelease |
| Alpha | `releases.alpha.json` | Owner, developers, testers | Published prerelease |

The application-lifetime `VelopackUpdateService` owns feed checks, downloads, staged updates, cancellation, and apply/restart. Startup and Options panels are views on that shared service. Hiding either panel does not cancel an operation. The service defers apply during trainer rides and FIT replay, retains the existing retry path after download/apply failures, and allows offline launch and riding.

The application reads the selected channel from `user://settings/settings.json`. An older settings file without `UpdateChannel` inherits the channel embedded in its Velopack installation: existing Alpha installations stay on Alpha, and a fresh Stable Setup starts on Stable. An explicit saved selection takes precedence. The public installer is built with `--channel stable`; Alpha Setup packages carry `--channel alpha`.

Settings shows the installed version, selected channel, candidate version and update state. Stable is described as tested releases recommended for normal use; Alpha is described as development releases that may have bugs. Changing the selection saves it, cancels an in-progress download, waits for it to stop, creates an updater manager for the chosen channel and checks again. Download, staged update, retry, cancellation, ride/replay deferral and explicit restart are owned by the existing application-lifetime updater.

Velopack 1.2.161 uses `releases.{channel}.json`, `ExplicitChannel`, `AllowVersionDowngrade`, and `GithubSource`'s prerelease option. Wodra enables Velopack's lower-version discovery for channel switching, but applies an update only when its `SemanticVersion` is strictly greater than the installed version. A lower or equal offer is suppressed. If an Alpha version is newer than the Stable feed, Settings explains that the current Alpha will remain installed. A staged package is tagged in settings with the channel that downloaded it; the updater never applies a package staged for the other channel. An unknown staged package on a legacy install is accepted only for its original installer channel (Alpha for existing Alpha installs).

## User data and recovery

Profiles, settings, ride history, FIT files, and Strava state live in Godot's per-user data directory outside `%LOCALAPPDATA%\Wodra`. Channel selection and app updates do not move or erase those files. No update check is required to launch Wodra or ride offline.

If an update is defective, choose the other channel in Settings. Wodra will keep the installed version when that channel has no newer version and never silently downgrade. A later version on the chosen feed can be installed through the normal Download and Install flow. If the application cannot start, use the same per-user Setup installer for a newer compatible version; Velopack upgrades the existing installation and leaves Godot user data intact. Do not uninstall as a rollback step. Back up FIT files and the Wodra user-data folder before manual recovery.

## Integration test notes

The update service accepts a local `SimpleFileSource` only when the test build is compiled with `EnableWodraLocalUpdateFeed=true` and `WODRA_LOCAL_UPDATE_FEED` points to an isolated fixture. Production builds always use anonymous public GitHub Releases. Existing `UpdaterServiceVisibilityReviewTest.tscn` verifies the shared service survives hidden presenters, ride deferral, cancellation, retry and failed apply. `UpdateChannelSettingsReviewTest.tscn` verifies the production selector and both an available Alpha update and an older Stable feed state.

Release CI verifies package channel feeds and route manifests; a package test must additionally install Setup into a disposable Windows user-data root and exercise fresh Alpha/Stable defaults, an existing Alpha upgrade, both channel directions, restart, profile/history preservation, and stale-staged-package exclusion. Generate channel-specific installers with `-Channel alpha` and `-Channel stable`; compile a local-feed-only test application with `EnableWodraLocalUpdateFeed=true`, then use an isolated package directory and user-data roots. Keep generated installers and user data outside the repository. A source build or mock does not count as that packaged Windows acceptance. The current PR's full packaged install/switch/apply run remains pending access to the provisioned release workstation and its Wodra Player client configuration.
