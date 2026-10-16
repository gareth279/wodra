# EOS long-ride authentication recovery validation

## Failure evidence and correction

The supplied ride log records EOS authentication success at 12:33:52, lobby join at 12:34:38, `eos_auth_expired` at 13:32:51, and reauthentication success two seconds later. Lobby discovery then succeeded but eight join attempts returned `LobbyLobbyAlreadyExists`; trainer telemetry and FIT recording continued. The former expiry handler cleared local membership state and proceeded through normal lobby discovery/join even though EOS retained the rider in its lobby. `LobbyLobbyAlreadyExists` is not treated as proof of membership. Recovery now preserves the lobby state, reauthenticates under the expected Product User ID, and inspects the copied lobby details for the local member before reconciling the existing session. A join is attempted only when that authoritative membership check says the local rider is absent.

Authentication expiry notification registration now occurs during availability checks, before lobby joining, so menu time does not consume an unobserved token lifetime. Recovery retries are bounded to three attempts with 2 s and 5 s waits. A retained membership is restored without a duplicate `RiderJoined`; a lost membership is cleaned up and rejoined under the same identity. If EOS is unavailable, local ride progression and recording remain independent.

Automated tests cover the production membership decision and retry/backoff policy. They do not mock the EOS native lobby/P2P API; two-client EOS acceptance is still required.

## Procedure

1. Use two installed Wodra clients signed in under different Windows profiles/devices. Confirm both clients select the same route and see each other in the lobby.
2. Start a ride on one client. Keep the second client in the same route lobby. Record the first client's EOS authentication success time from its diagnostic log.
3. Spend at least 15 minutes in menus before starting the ride for a separate pass. In another pass, start the ride promptly. This confirms expiry is tied to EOS authentication, not the ride timer.
4. Keep the first ride active for at least 90 minutes after authentication. Around the 55–60 minute mark, observe the status and logs. The expiring-auth callback should trigger bounded reauthentication and authoritative local membership reconciliation without duplicate join attempts.
5. On the second client, confirm the first rider remains visible through renewal, then verify fresh distance, speed, power, cadence, pause state and route transitions continue to arrive. Reverse the clients for a second run if possible.
6. Repeat with a brief network interruption during the renewal window. Confirm recovery retries are bounded, the local ride and FIT recording continue, the status reports the interruption, and remote visibility returns after connectivity is restored.
7. Finish the ride normally, verify FIT finalization, leave the lobby, and confirm neither client retains a stale rider or subscription. Join a different route and verify no old-route membership is restored.

Do not treat a successful Debug/Release build, a simulator test, or a single-client run as evidence that remote EOS visibility and P2P recovery passed.
