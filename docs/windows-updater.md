# Windows updater

Wodra keeps Velopack as its per-user Windows installer and update mechanism. A
single application-lifetime `VelopackUpdateService` owns feed checks, package
downloads, staged updates, cancellation, and safe apply/restart. The startup
screen and Options window each show a `VelopackUpdatePanel` presenter bound to
that same service. Hiding a screen or closing Options changes only its view;
it does not cancel an update operation. Staged updates wait until no ride or
FIT replay is active.

## Regression cause

Before this change, `VelopackUpdatePanel.DownloadAndApplyUpdate()` was the
operation owner. It returned early when the current session was active **or**
the panel was not `IsVisibleInTree()`. On that path it set a status message,
hid the update details window, and returned without downloading or applying
anything. The visibility requirement became unsafe after the PR #152 pre-ride
screen redesign: the updater presenter is attached to UI whose parent can be
hidden or replaced while the application remains alive. An update action must
not depend on that screen's visibility. The existing mocked coverage exercised
active-ride deferral but had not hidden the startup presenter during download,
so both the silent UI dismissal and the missing visible error state passed.

The service now coalesces concurrent startup and Settings checks, applies a
bounded timeout to metadata checks, distinguishes feed failures from a
confirmed up-to-date result, and exposes retryable status. Package downloads
have their own cancellation path without the short feed timeout. Velopack
stages and verifies packages; only a successful return from the download and
verification operation plus a matching `UpdateManager.UpdatePendingRestart`
offer sets `HasStagedPackage`. A progress callback reporting 100 percent, or
even a successful downloader return without that staged offer, is not proof
that verification and local staging succeeded. The service calls apply/restart
only after the ride/replay safety callback allows it. A return from the restart
call is reported as a failure with a retry action. Failed downloads keep the
update offer and retry the download; failed applies retain the explicit
staged-package state and retry apply.

## Local installed-package review

The local feed override is available only to test builds compiled with
`EnableWodraLocalUpdateFeed=true`. In that configuration the app reads
`WODRA_LOCAL_UPDATE_FEED`; ordinary release builds always use the configured
GitHub alpha source. Example local package creation (does not publish):

```powershell
$env:EnableWodraLocalUpdateFeed = 'true'
& .\publish.ps1 -Version '0.1.0-alpha.9902' `
  -PublishRoot (Join-Path $env:TEMP 'WodraUpdaterReview\older')
& .\publish.ps1 -Version '0.1.0-alpha.9903' `
  -PublishRoot (Join-Path $env:TEMP 'WodraUpdaterReview\newer')
Remove-Item Env:\EnableWodraLocalUpdateFeed
```

Install the older package into a fresh `--installto` directory. Set isolated
`APPDATA` and `LOCALAPPDATA` paths before launching it, then point
`WODRA_LOCAL_UPDATE_FEED` at a directory containing the newer package's
`releases.alpha.json`, `assets.alpha.json`, and full `.nupkg`. Review
`%APPDATA%\Godot\app_userdata\Wodra\logs` for updater events and confirm the
version after Velopack restarts the app. Keep generated installers and user
data outside the repository; retain only the sanitized test log and summary
needed to reproduce a review.

The Windows validation workflow runs the updater service and UI lifecycle
regressions. `UpdaterServiceVisibilityReviewTest.tscn` specifically hides the
startup parent while a download is active and checks status-row minimum size,
active-ride deferral, and a visible retry path after an apply failure.
