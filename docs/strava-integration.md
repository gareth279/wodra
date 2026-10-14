# Strava profile linking and manual FIT uploads

Issue #12 remains open. Linking has completed against the deployed broker and a saved FIT upload has reached Strava, which returned HTTP 403. Upload success, persistence after restart and token refresh across restart remain unverified. Do not publish a feature alpha until the hosted end-to-end flow has passed.

Wodra opens Strava in the system browser and listens on an ephemeral loopback callback. It validates OAuth `state`, sends the one-use code and exact callback URI to the fixed HTTPS broker, and stores each profile's rotating refresh token in Windows Credential Manager. Athlete identity and upload state are stored separately without tokens. Disconnect removes only the selected profile's credential and metadata.

Saved FIT segments upload directly from the PC to Strava. Each segment has a durable journal key based on profile, ride, segment and file hash. Wodra records the external ID before sending, stores Strava's asynchronous upload ID, polls with a bounded interval, and records the activity ID/link on success. Definite HTTP 4xx rejection is retryable after its cause is addressed. If a submission outcome is ambiguous, Wodra keeps it blocked to avoid creating a duplicate. FIT files remain on the PC; they are never sent to the broker.

## Strava app settings

In the Strava API settings for app `Wodra` (client ID `234943`):

1. Set Category to **Indoor**.
2. Set the Callback Domain to **localhost** for Wodra's ephemeral loopback redirect.
3. Keep the client secret only in the private server configuration outside the document root.
4. Wodra requests `read,activity:write` and checks the granted scope before storing a link. Successful OAuth linking does not prove the app is permitted to upload activities.
5. Do not assume a 403 is caused by athlete capacity, subscription, or another policy. Inspect Wodra's safe upload diagnostic fields and the actual Strava response before attributing a cause.

## MyHost cPanel deployment

The package source is [hosting/wodra-strava-broker](../hosting/wodra-strava-broker); regenerate [the cPanel archive](../hosting/wodra-strava-broker.zip) with `./hosting/package-strava-broker.ps1`. The ZIP contains only placeholder configuration examples, source and tests. It excludes live configuration, secrets, OAuth sessions and runtime storage.

The working host currently uses PHP 8.5. Keep that version selected in cPanel MultiPHP Manager. PHP 8.1 or newer is required, with `curl`, `json`, and `openssl`, outbound HTTPS to `www.strava.com:443`, and a current CA bundle. No Composer packages or database are needed. The host may separately warn that `register_argc_argv` should be disabled; set `register_argc_argv=0` in that domain's PHP INI settings if the warning persists. This setting is separate from the broker configuration.

### Replace the working deployment safely

1. In File Manager, back up the deployed public `index.php`, `.htaccess`, and `deployment.php`, plus the private `Broker.php`. Keep the existing private `config.php`, private storage, rate directory and their contents. Do not download the live config into the repository or chat.
2. Extract the regenerated ZIP locally. Upload `public/index.php` and `public/.htaccess` to the existing `/home/gconz/api.g5.co.nz/wodra/strava/` URL directory, replacing those files. The updated `.htaccess` no longer relies on Apache `SetEnv`.
3. Upload `public/deployment.example.php` to the same URL directory as `deployment.php`. Edit only its two deployment settings: set `private_directory` to `/home/gconz/wodra-strava-private` and `url_base_path` to `/wodra/strava`. This file contains no secret and replaces the temporary path and routing edits made in `index.php` and `Broker.php`.
4. Upload `private/Broker.php` over `/home/gconz/wodra-strava-private/Broker.php`. Do not replace `/home/gconz/wodra-strava-private/config.php`; the working host already has the required integer TTL and storage paths. Confirm it still has `private_storage` set to `/home/gconz/wodra-strava-private`, `rate_limit_directory` set to `/home/gconz/wodra-strava-private/rate`, and `link_session_ttl_seconds` set to integer `600` without displaying or copying the client secret.
5. Keep `/home/gconz/wodra-strava-private` and its private storage/rate directories at mode `0700`; keep `config.php` at `0600`. The public URL directory and PHP files can use `0755` and `0644`. Confirm the cPanel PHP-FPM account owns or can write to the two private storage directories.
6. Do not copy `service_not_configured_1`, `service_not_configured_broker`, temporary boolean diagnostics, or any troubleshooting `error_log` block from the working edits. The packaged implementation logs only safe failed-check identifiers.
7. In cPanel Terminal, verify PHP 8.5 and required modules, then visit `https://api.g5.co.nz/wodra/strava/health`. Expect HTTP 200 and `{"status":"ok"}`. A failure's matching PHP error-log entry identifies the failed check without exposing configured values. Health does not test Strava authorization or upload permission.
8. Run the offline broker checks with `php tests/run.php` from the extracted package. Linking should continue to work after these replacements; do not perform the FIT upload until you choose to retest it in Wodra.

No secret is stored in the public directory, `.htaccess`, deployment file, archive or repository. The broker remains limited to OAuth exchanges, refresh and revoke. FIT uploads go directly from Wodra to Strava.

## Client and API contract

The public desktop configuration defaults to `https://api.g5.co.nz/wodra/strava/` and client ID `234943`. It contains no broker password or Strava secret.

| Route | Request | Response |
|---|---|---|
| `GET /health` | none | `{ "status": "ok" }` when configuration, dependencies and private write access are ready |
| `POST /session/start` | `{ "profile_id": "GUID", "redirect_uri": "http://localhost:PORT/strava/callback" }` | `{ "session_id", "state", "expires_at", "client_id", "scope" }` |
| `POST /session/complete` | `{ "session_id", "state", "code", "redirect_uri" }` | `{ "profile_id", "tokens": { Strava token response } }` |
| `POST /session/cancel` | `{ "session_id" }` | `{ "status": "cancelled" }` |
| `POST /oauth/refresh` | `{ "refresh_token" }` | Strava token response, including rotated refresh token |
| `POST /oauth/revoke` | `{ "refresh_token" }` | `{ "status": "revoked" }` |

The broker accepts JSON POSTs only, limits bodies to 16 KiB, throttles each remote IP, uses fixed HTTPS Strava OAuth endpoints, verifies TLS certificates/host names, disables redirects, and applies short timeouts and bounded upstream responses. Link state is unpredictable, stored as a hash, expires after ten minutes, and is consumed before exchange. Exact loopback callback binding is enforced.

## Upload diagnostics and retry safety

Upload failures log the HTTP status, a recognized category, and only recognized Strava `resource`, `field`, and `code` values. Raw response bodies, message text, access tokens, refresh tokens and authorization headers are never logged. A 401 identifies expired or revoked authorization; a 429 identifies rate limiting. A 403 or 400 is classified only when recognized error fields or message patterns support it. Otherwise it remains an unknown failure with its status and safe parsed fields for diagnosis.

A definite 4xx submission rejection is journaled as failed and the same saved segment can be retried after the cause is addressed. A successful receipt is journaled before polling; a known upload ID continues to be reconciled by polling instead of submitting again. Ambiguous outcomes remain blocked from automatic resubmission.

## Tests and release gate

PHP broker checks cover callback protections, state hashing/expiry/consumption, explicit base-path routing despite differing `SCRIPT_NAME`, placeholder-only deployment settings and safe configuration diagnostics. The client regression suite covers upload classification, safe field retention, response redaction, bounded/truncated responses and retry rules. Windows CI also builds the client and runs existing regression suites. These checks do not prove the live cPanel host or Strava's upload permission. Retest the saved segment manually; do not publish an alpha until upload success, restart persistence and token refresh have been verified.

## Official references

- [Strava OAuth](https://developers.strava.com/docs/authentication/)
- [Strava uploads](https://developers.strava.com/docs/uploads/)
- [Strava rate limits and athlete capacity](https://developers.strava.com/docs/rate-limits/)
- [MyHost MultiPHP Manager](https://myhost-clients.com/knowledgebase/153/)
