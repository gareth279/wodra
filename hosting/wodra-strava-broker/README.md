# Wodra Strava PHP broker

This package is for PHP 8.1+ hosting with cURL, JSON and OpenSSL enabled. It contains no Strava client secret. The broker exchanges authorization codes, refreshes or revokes tokens, and keeps only short-lived linking-session state. FIT files go directly from Wodra to Strava and are never sent to or stored by this broker.

## Package layout

- `public/` is the web document root content. Copy `deployment.example.php` to `deployment.php` and configure the private directory and URL base path. This file contains no secret.
- `private/` must be placed outside the domain document root. Copy `config.example.php` to `config.php` there and fill in the secret on the host.
- `tests/` contains offline PHP checks. Run with `php tests/run.php`.

The API routes are `/health`, `/session/start`, `/session/complete`, `/session/cancel`, `/oauth/refresh` and `/oauth/revoke`. Token exchange and refresh responses use Strava's token fields directly; session completion wraps them in `{profile_id, tokens}`. The desktop client is the only intended caller, but a native public client cannot prove its identity using an embedded static secret. HTTPS, short-lived single-use sessions, state and callback binding, request limits and per-IP throttling mitigate abuse; they do not turn the desktop binary into a confidential client.

`GET /health` checks configuration, URL routing, supported PHP/extensions, private path separation and private storage write access. It returns only `{"status":"ok"}` or a generic failure. The PHP error log records safe failed-check identifiers without configured values. It does not test or disclose credentials and never contacts Strava.

The package script uses an explicit file allowlist. It never includes live `config.php` or `deployment.php`, OAuth session files, rate-limit state or other runtime storage.
