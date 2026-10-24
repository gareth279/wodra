# Wodra web companion (foundation)

Read-only mobile browser telemetry relayed from the Windows app. No ride history, FIT files, trainer control, chat, third-party QR service, database, Composer, cron or persistent worker is used. The relay keeps only the latest small snapshot for each pair.

## Supported hosting

Minimum **PHP 8.2** (supported PHP branch), the core `json` extension, `random_bytes`, `hash_equals`, filesystem `flock`/atomic same-volume `rename`, and a writable private directory outside the document root. No cURL, OpenSSL PHP extension, SQLite or MySQL is required by this relay. HTTPS must terminate at the PHP host; the relay rejects non-TLS requests in production. `allow_insecure_local` is for local tests only.

Before deployment, confirm with the actual cPanel account: PHP version/handler, `json`, hosting filesystem lock/rename behavior, writable quota/file limits, document root, TLS certificate, and whether API POSTs to a same-origin PHP endpoint work. These hosting facilities and DNS for `wodra.g5.co.nz` have not been inspected or changed. Do not deploy until the domain and host checks pass.

## Local test

Use PHP 8.2+ CLI. Create a temporary PHP config outside this package with:

```php
<?php
return ['origin'=>'http://127.0.0.1:8789','storage'=>sys_get_temp_dir().'/wodra-companion-local',
  'invite_ttl_seconds'=>180,'pair_ttl_seconds'=>1800,'allow_insecure_local'=>true];
```

PowerShell:

```powershell
$env:WODRA_COMPANION_CONFIG = 'C:\temp\wodra-companion-test.php'
php -S 127.0.0.1:8789 -t hosting/wodra-companion/public
```

The public API is `POST /api.php`; call it from the browser page served on the same origin (or set the matching test origin). Never use localhost as the desktop production default.

## cPanel deployment (manual, after host verification)

1. Create/verify `wodra.g5.co.nz` in cPanel and issue/enable a valid TLS certificate. Confirm HTTPS and choose the subdomain's document root (for example `public_html/wodra-companion`). No public deployment is performed by this repository change.
2. Upload **only** the contents of `public/` to that document root. Do not expose `private/`, source, or the example config.
3. In the account home directory outside `public_html`, create `wodra-private/companion` with owner-only permissions. The PHP account must be able to create/lock files there.
4. Copy `private/config.example.php` outside the docroot as `wodra-private/config.php`. Replace `CPANEL_USER` in the storage path with the host's actual home path; set `origin` to the exact `https://` origin. Set `allow_insecure_local` false. Restrict config and directory permissions to the account.
5. If the config is not at `<package>/private/config.php`, configure the PHP handler's `WODRA_COMPANION_CONFIG` environment variable to its absolute path. If cPanel cannot set environment variables, place `private/config.php` beside `public/` (outside the selected document root) and keep its parent inaccessible over HTTP.
6. Set PHP to 8.2 or newer with `json`; confirm the quota supports at least 200 active small pair records and temporary atomic files. The store is bounded to 1 MB and abandoned pairs expire on requests; no cron is required.
7. Verify HTTPS, `POST` API from the same origin, no-store response headers, and private storage behavior. Pair using a test Wodra build/profile, revoke it, and confirm test files contain only token hashes and one current snapshot.

## Protocol and limits

Protocol version is `1` (fixed action/field contract; unknown fields are rejected in telemetry). Pairing uses a 10-character one-use hexadecimal code (40 random bits), valid for 180 seconds by default, plus independent random 256-bit invite, desktop and browser credentials. The desktop asks the rider to approve the pending phone. The browser role can only read; the desktop role can only publish. Credential values are held in memory/sessionStorage and only their SHA-256 hashes are kept by PHP. The phone code is in the URL fragment only and is cleared from browser history immediately. Never paste credentials into logs or URLs.

The desktop publishes every 3 seconds; the page polls every 3 seconds while foregrounded. That's approximately **40 small API requests/minute per active pair** (20 reads + 20 writes); a foreground phone retry backs off to 12 seconds when errors repeat. Hidden tabs stop polling. Requests abort after 5 seconds. Snapshot timestamps/sequence reject replay; stale data is labeled after 12 seconds. A pair expires after 8 hours without authenticated activity by default. Pairing requests are rate limited per source address (6 creates and 12 claims per minute). Configure HTTPS/proxy boundaries so the PHP process receives a meaningful `REMOTE_ADDR`; there is no trust in forwarded headers.

## Desktop configuration and safety

Companion defaults OFF and relay URL blank. No production hostname or localhost URL is preconfigured. Enabling the option alone does not transmit ride data; the rider must enter their deployed HTTPS relay URL, create an invitation, then approve the phone. Desktop credentials live only for this app process; quit, disable, cancel and revoke discard them. Without deployment configuration, pairing is unavailable while local riding remains unaffected.

## Local validation

`tests/run.ps1` starts the built-in PHP server against a temporary isolated store and runs API scenarios for one-time claims, approval/rejection, pair isolation, monotonic sequence, snapshot bounds, role separation, expiry, revocation and parallel reads. Website layout is plain responsive HTML/CSS and does not depend on a service worker or remote assets. Browser emulation is not a real iPhone/Android acceptance test.
