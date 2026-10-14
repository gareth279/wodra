# Strava profile linking and manual FIT uploads

Issue #12 is still open. The client UI and local upload flow are implemented, and a PHP broker package is prepared for MyHost cPanel. The broker has not been deployed, so linking and uploads cannot complete yet. No real ride has been uploaded. Do not publish a feature alpha until the hosted flow has passed the end-to-end test.

Wodra opens Strava in the system browser and listens on an ephemeral loopback callback at `localhost`. It validates OAuth `state`, sends the one-use code and exact callback URI to the fixed HTTPS broker, and stores each profile's rotating refresh token in Windows Credential Manager under a target containing that profile's stable GUID. Athlete identity and upload state are stored separately without tokens. Disconnect removes only the selected profile's credential and metadata; it asks the broker to revoke the refresh token, but still completes locally if the host is offline.

Saved FIT segments upload directly from the PC to Strava. Each saved segment has its own Upload action and durable journal key based on profile, ride, segment and file hash. Wodra records the external ID before sending, stores Strava's asynchronous upload ID, polls with a bounded interval, and records the activity ID/link on success. A definite Strava rejection can be retried. If the network fails after submission and no receipt is available, Wodra marks the result unknown and blocks another submission to avoid creating a duplicate. Keep the FIT files; Wodra never sends them to the broker. Debug/non-recording rides and missing or empty FIT files cannot upload.

## Strava app settings

In the Strava API settings for app `Wodra` (client ID `234943`):

1. Set Category to **Indoor**.
2. Set the Callback Domain to **localhost** so Strava accepts Wodra's `http://localhost:<ephemeral-port>/strava/callback` desktop redirect. The redirect URI itself is passed exactly to the authorization and code exchange requests.
3. Keep the client secret only in the private server configuration described below. It is not in Wodra, this repository, or the example configuration.
4. Wodra requests `read,activity:write`; it checks that Strava granted both before storing the link. Do not use dashboard access or refresh tokens as application credentials or as rider tokens.
5. The app is currently limited to one athlete. That may permit owner-only testing if Strava authorizes the app owner, but it does not establish that the account is eligible or that access is approved. Strava's self-service capacity increase and any broader-use review are separate blockers. Do not claim multi-profile capacity until Strava approves it.

## MyHost cPanel deployment

The package source is [hosting/wodra-strava-broker](../hosting/wodra-strava-broker), with a ready-to-upload [cPanel package archive](../hosting/wodra-strava-broker.zip). Recreate it with `./hosting/package-strava-broker.ps1`; it contains separate `public`, `private`, and `tests` directories. The archive contains only the placeholder config. Deployment remains a manual action; this repository has no MyHost credentials.

1. In MyHost cPanel, add or confirm the `api.g5.co.nz` subdomain and its TLS certificate. Choose a document root outside the existing site, for example `/home/CPANEL_USER/api.g5.co.nz`. If the account maps the subdomain to another directory, use that actual root consistently below.
2. Enable PHP **8.1 or newer** for the subdomain in cPanel's MultiPHP Manager. Confirm `curl`, `json`, and `openssl` are enabled. PHP must have outbound HTTPS access to `www.strava.com:443`, a current CA bundle, and TLS peer verification. No Composer packages or database are needed.
3. Create the URL directory `/home/CPANEL_USER/api.g5.co.nz/wodra/strava` and copy the **contents** of the archive's `public/` folder into it. The `public` folder name itself is not part of the URL. The requested route then maps to `https://api.g5.co.nz/wodra/strava/`.
4. Create `/home/CPANEL_USER/wodra-strava-private` outside the document root. Copy `private/Broker.php` and `private/config.example.php` there. Rename `config.example.php` to `config.php`; set the existing public Client ID `234943`, the Strava client secret from the Strava dashboard, and absolute paths for `private_storage` and `rate_limit_directory`. Do not put this directory under `public_html` or the subdomain document root.
5. Set the private directory and its storage directories to owner-only mode `0700`; set `config.php` to `0600`. Set public directories to `0755` and public PHP/`.htaccess` files to `0644`. In File Manager, use the file permission editor; confirm the PHP-FPM/cPanel account can write to the private storage directories. The application also applies restrictive modes to created files.
6. Edit `public/.htaccess` and replace `/home/CPANEL_USER/wodra-strava-private` in `SetEnv WODRA_STRAVA_PRIVATE_DIR ...` with the real absolute private path. If cPanel PHP-FPM does not pass Apache `SetEnv` values, set the same variable in the domain's PHP-FPM environment instead. Do not copy the secret into `.htaccess`.
7. In cPanel Terminal, verify `php -v` and `php -m` show PHP 8.1+ and `curl`, `json`, `openssl`. Check outbound TLS with `curl -fsS -o /dev/null -w '%{http_code}\n' https://www.strava.com/oauth/token`; an HTTP response (including a method/auth error) shows DNS/TLS reached Strava, while a certificate, DNS, or connection error needs host support. Do not include tokens in this check.
8. Visit `https://api.g5.co.nz/wodra/strava/health`. It must return HTTP 200 and `{"status":"ok"}`. A generic 503 means check the PHP version/extensions, environment path, config location, private permissions and TLS setup. The endpoint reveals no values and does not verify Strava authorization.
9. Test the package's offline checks with `php tests/run.php` in the extracted package. Only after health passes should Wodra's browser linking test be attempted.

The broker accepts JSON POSTs only, limits request bodies to 16 KiB, throttles each remote IP, uses only fixed HTTPS Strava OAuth endpoints, verifies TLS certificates/host names, disables redirects, and uses short connect/total timeouts and bounded upstream responses. Linking state is unpredictable, stored as a hash, expires after ten minutes, and is consumed before exchange. The exact loopback redirect URI is bound to the session. Error responses are generic; request bodies and upstream token contents are not logged or echoed except the intended OAuth token response to Wodra. The broker is not a generic proxy and has no FIT upload route.

The desktop binary is a public client. An embedded “client secret” would be extractable and is not used as client authentication. Per-IP throttling and session binding limit abuse but cannot prove an installed client's identity. Review abuse/rate limits and hosting logs after deployment; configure web-server logs not to record request bodies or authorization headers.

## cPanel verification, rollback and secret rotation

- Before replacement, save a copy of the currently deployed `public/` and private PHP files. The current host has not been modified by this work.
- To roll back, restore the prior `public/index.php`, `.htaccess`, and private PHP/config backup together, then recheck `/health`. If no prior broker exists, remove the new public route and private files; then remove the `WODRA_STRAVA_PRIVATE_DIR` setting. Existing rider credentials stay on their PCs, but linking/uploads remain unavailable until service returns.
- To rotate the Strava secret, generate/regenerate it in Strava, update only the private `config.php`, then restart/reload the domain's PHP handler if required and check `/health`. Do not place the value in shell history, chat, source control or web root. Existing refresh tokens may require riders to link again if Strava invalidates them.
- Restrict cPanel account access and keep private config/storage backups encrypted. Do not expose `phpinfo()`, session files, rate-limit files, or the private folder through the web root.

## Client and API contract

The only client configuration is public and defaults in `AppSettings` to `https://api.g5.co.nz/wodra/strava/` and client ID `234943`. It contains no broker password or Strava secret.

| Route | Request | Response |
|---|---|---|
| `GET /health` | none | `{ "status": "ok" }` if configured, dependencies and private write access are ready |
| `POST /session/start` | `{ "profile_id": "GUID", "redirect_uri": "http://localhost:PORT/strava/callback" }` | `{ "session_id", "state", "expires_at", "client_id", "scope" }` |
| `POST /session/complete` | `{ "session_id", "state", "code", "redirect_uri" }` | `{ "profile_id", "tokens": { Strava token response } }` |
| `POST /session/cancel` | `{ "session_id" }` | `{ "status": "cancelled" }` |
| `POST /oauth/refresh` | `{ "refresh_token" }` | Strava token response, including rotated refresh token |
| `POST /oauth/revoke` | `{ "refresh_token" }` | `{ "status": "revoked" }` |

There is no server-side athlete-token database and no upload route. Wodra sends each FIT file directly to Strava, which returns an upload ID; Wodra polls Strava for processing state and stores the resulting activity ID locally.

## Tests and release gate

`hosting/wodra-strava-broker/tests/run.php` checks callback host/scheme/path/port restrictions, unique state/session generation, state hashing, exact callback matching, expiry, cancellation and one-use consumption without contacting Strava. GitHub Windows validation runs these checks under PHP 8.2. Local .NET/Godot checks cover client compilation and existing regression suites. Neither those tests nor `/health` prove that this cPanel account supports the required extensions/egress or that Strava has approved the app. A hosted owner-account link/upload must be explicitly authorized and tested before alpha publication. No real FIT upload is authorized yet.

## Official references

- [Strava OAuth](https://developers.strava.com/docs/authentication/)
- [Strava uploads](https://developers.strava.com/docs/uploads/)
- [Strava rate limits and athlete capacity](https://developers.strava.com/docs/rate-limits/)
- [MyHost MultiPHP Manager](https://myhost-clients.com/knowledgebase/153/)
- [MyHost shared hosting overview](https://myhost-clients.com/knowledgebase/359/)
