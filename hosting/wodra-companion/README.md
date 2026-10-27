# Wodra web companion

The companion is a read-only, same-origin mobile web app. It relays only the latest active ride snapshot; it does not store ride history or FIT files and does not provide trainer or application controls.

## Hosting requirements

The relay supports the production host's PHP 8.2 runtime, built-in `json` support, `random_bytes`, `hash_equals`, filesystem `flock` and atomic same-volume `rename`. Runtime state and `config.php` must remain outside the document root. HTTPS is required in production. The handler reports a JSON error rather than exposing credentials or filesystem paths if private storage is unavailable.

Verify the domain's active PHP-FPM/Apache handler, not only the command-line version. CI runs syntax and API integration checks with PHP 8.2. The production domain is `https://wodra.g5.co.nz/`; Windows defaults to this URL while Companion itself remains opt-in. Custom HTTPS relay URLs are retained as an advanced override.

## Protocols and trust model

Protocol 2 separates a profile-scoped desktop identity, a one-time invitation, a remembered browser device, and its current ride snapshot. Desktop identity material is protected with the current Windows user's DPAPI and is never stored in ordinary settings JSON. Each phone receives an opaque random 256-bit credential in a host-only `HttpOnly`, `Secure`, `SameSite=Strict` cookie. PHP stores only its SHA-256 hash. The credential expires after 90 days and renews during authenticated use when fewer than 30 days remain. A user can revoke one device or all devices in Wodra, or forget the phone from the web page.

Selecting **Connect new phone** is a short-lived (180-second default), single-use invitation for one new device. The QR contains the public companion URL and the temporary code only. Scanning it pairs automatically; there is no second approval step. Manual code entry remains available. Pairing creation and claims are rate limited. Browser actions require the configured exact Origin, credentials are not accepted in URLs, and `SameSite=Strict` further limits cross-site requests.

Trusted phones poll while their page is visible. The desktop publishes only while Companion is enabled and a ride is active, and clears the snapshot when the ride ends. An offline or idle phone remains trusted and shows **Waiting for Wodra**. The previous protocol 1 pairing/read flow remains available for Alpha.77 clients while they transition; protocol 1 retains its explicit desktop approval and session behavior.

The web page uses the standard Screen Wake Lock API and defaults its toggle on. Browsers may refuse or revoke the lock, and iOS power-saving/browser policy can still turn the display off. Users can temporarily disconnect polling or permanently forget the phone. The page is read-only.

## Local PHP integration test

Run on PHP 8.2 or newer:

```powershell
hosting/wodra-companion/tests/run.ps1
```

The script syntax-checks all PHP sources, starts an isolated local PHP server, and runs protocol 1 and protocol 2 tests against temporary storage. The browser wake-lock state machine has a separate Node test:

```powershell
node hosting/wodra-companion/tests/wake-lock.test.cjs
```

For manual testing, make a private test config outside this package with `origin`, a writable private `storage` directory, `invite_ttl_seconds` (180), `pair_ttl_seconds` (28800), and `allow_insecure_local` true only for loopback. Set `WODRA_COMPANION_CONFIG` to that file before starting PHP's built-in server. Never use HTTP or `allow_insecure_local` for public hosting.

## Production deployment and rollback

There is no automated production deployment credential or deployment workflow in this repository. Deploy only through the host's authorized cPanel/SSH process after taking a recoverable backup of the current public files and confirming private configuration/storage paths. Upload the contents of `public/` to the existing companion document root; do not upload `private/`, test fixtures, or repository source. Preserve the current production PHP handler and private configuration. Do not replace the private store or its credentials.

Before changing the live site, validate a copy of the release on PHP 8.2 with `tests/run.ps1` and syntax-check every PHP file. Upload public files to a staging directory first where hosting permits, then switch the document root or copy files as one reviewed operation. Retain the backup until the checks below pass; restore the previous public files if they fail. No database migration is required, and the v1 API actions remain in the new API for existing Alpha.77 clients.

After upload, verify anonymously that the website and JS/CSS load over HTTPS, and `GET /api.php` returns HTTP 405 with JSON `method_not_allowed` (not a PHP parse error, 404, or `not_configured`). From an authorized same-origin test browser and a disposable Wodra profile, pair, restart the browser and desktop, start/end a test ride, reconnect, revoke the test phone, and verify it is rejected. Confirm the private store is outside the document root and contains hashes rather than raw credentials. Do not use or disclose production credentials in logs or diagnostics.

`tests/run.ps1` provides local service-level coverage, not proof of production deployment or physical iPhone Safari/Android behavior. Those still require an authorized deployed integration and owner-device acceptance.
