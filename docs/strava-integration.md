# Strava integration readiness (issue #12)

## Current status

Wodra does not currently link Strava accounts or upload rides. The old global `StravaSettings` fields were never wired to an authorization or upload flow and were stored in ordinary `settings.json`; they are removed. When an old settings file contains the unsupported `Strava` object, startup rewrites settings without those fields. No token or secret value is logged. Do not place Strava credentials in `settings.json` or in the repository.

This change is not a Strava feature release. Keep issue #12 open and do not publish an alpha with a disabled or simulated connect/upload flow.

## Verified Strava constraints

The official Strava OAuth authorization code flow returns a one-use code, but exchanging it and refreshing access tokens both require the registered app's confidential `client_secret`. That secret must remain on a server. Strava permits `localhost` and `127.0.0.1` callback URLs, making a browser-based native-app callback possible, but it does not remove the server-side secret requirement. Wodra should open the system browser, bind an ephemeral loopback callback, validate an unpredictable per-attempt `state`, and send the code and exact redirect URI over HTTPS to a Wodra token broker. The broker exchanges the code and returns the token response. Wodra stores each profile's latest refresh token (Strava may rotate it) in Windows Credential Manager keyed by the stable Wodra profile GUID; short-lived access tokens can remain memory-only. Refresh goes through the broker. Unlink clears only that profile's credential and profile metadata and asks the broker to revoke access where available.

Request only `read` and `activity:write`: `read` is needed to identify the linked athlete, and `activity:write` is required to upload. If either scope is declined, linking must not be treated as successful. Cancellation and `access_denied` leave the profile unlinked.

FIT upload is multipart `POST /api/v3/uploads` and is asynchronous. Persist each upload ID before polling; poll at one second or longer; retain the upload ID/activity ID and status so a restart or retry reconciles the existing upload rather than posting the same file again. Use a stable unique `external_id` per saved FIT segment. A failed segment remains saved and can be explicitly retried. Catch-up rides with several FIT files must list and upload each segment individually. A ride with no existing FIT file is ineligible; screenshots or a debug ride are not converted into an upload.

Strava's current default application capacity is one athlete (“Single Player Mode”). The documented self-service increase is to ten; authenticating additional athletes beyond the app's approved capacity is blocked until Strava reviews the app. Wodra's per-profile account feature therefore cannot be released for ordinary multi-rider use until an app is registered, increased capacity is granted for the intended audience, and the app is approved for higher capacity as needed. API limits are app-wide, so upload status polling must be bounded and back off on rate limiting.

## Smallest hosting and registration setup

1. Register a Wodra app in Strava API settings and configure a callback domain that permits the loopback redirect used by Wodra. Apply for capacity appropriate to Wodra's rider profiles and complete Strava's Developer Program review before promising multi-account access.
2. Deploy `src/Wodra.Strava.Broker` as a small HTTPS token broker with `STRAVA_CLIENT_ID` and `STRAVA_CLIENT_SECRET` supplied as host secrets. It exposes code exchange, refresh, and revocation proxy operations; FIT bytes and athlete access tokens remain on the rider's PC. Do not log request bodies, authorization headers, codes, or tokens. The service applies per-IP rate and request-size limits and restricts callbacks to loopback. Configure the reverse proxy to redact request bodies/authorization headers and forward HTTPS only.
3. Configure the app's public broker base URL and client ID through release configuration. Neither value is a secret; the client secret is server-only. Use HTTPS in every distributed build.
4. Configure a real app and broker, then test link/cancel/denied/expiry/revoke and upload processing with an explicitly authorized test Strava account. Do not upload anyone's ride until they authorize the test upload.

Before implementation is called complete, the client must include a tested loopback OAuth lifecycle, Windows Credential Manager storage with profile isolation, a durable per-segment upload journal, and the Manage Profiles and ride-summary controls. The broker must be deployed and capacity-approved. There is no release link for this feature yet.

## Official documentation checked (2026-10-14)

- [Strava OAuth authentication](https://developers.strava.com/docs/authentication/)
- [Strava FIT and asynchronous upload API](https://developers.strava.com/docs/uploads/)
- [Strava API rate limits and athlete capacity](https://developers.strava.com/docs/rate-limits/)
