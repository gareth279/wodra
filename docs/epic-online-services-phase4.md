# Epic Online Services integration

Wodra now contains a Windows x64 EOS provider for the existing multiplayer protocol. With the official EOS C# SDK present at build time, it authenticates through Connect Device ID, discovers public route lobbies, exchanges rider snapshots and reliable reposition events over EOS P2P, and retries after interrupted sessions. The local ride, trainer, and FIT recording remain usable when EOS is unavailable.

The provider is selected by `AppSettings.OnlineMultiplayerProvider` (`EpicOnlineServices` by default). Set it to `WebSocket` in the untracked settings JSON to use the existing development server for automatic online riding. The manual multiplayer join flow continues to use WebSocket. The implementation is compiled against the installed official EOS C# SDK **1.19.2.1** and its matching generated bindings.

## Local SDK setup

The EOS SDK is not committed. Install/extract the official EOS C# SDK outside the repository. Builds find it at `%LOCALAPPDATA%\Wodra\EOS-SDK` by default, or use an alternate root:

```powershell
$env:EOS_SDK_ROOT = 'D:\SDKs\EOS-SDK'
dotnet build wodra.sln -c Debug -m:1
dotnet build wodra.sln -c Release -m:1
```

The root must contain `SDK\Source\Core`, `SDK\Source\Generated`, and `SDK\Bin\EOSSDK-Win64-Shipping.dll`. When present, the project compiles the official bindings and copies the native runtime into build and publish output. Without it, the game builds with an unavailable-provider implementation and solo riding remains available.

The current build integration targets Windows x64 only. A different target requires that platform's EOS native library and the corresponding EOS platform define/build configuration.

## Developer Portal configuration

Complete these settings in the [Epic Developer Portal](https://dev.epicgames.com/portal):

1. Under **Your Products**, select **Wodra** → **Product Settings** → **SDK Download & Credentials**. Select or create a development sandbox and deployment for testing. This page provides the Product ID, Sandbox ID, Deployment ID, and the client credentials used by the SDK.
2. In **Product Settings → Clients**, create a **custom client policy**, enable **User required**, and grant the Connect service needed for Device ID login plus Lobbies and P2P. Follow Epic's [client policy guide](https://dev.epicgames.com/docs/epic-online-services/eos-fundamentals/client-and-client-policy/client-policy-guide) for the current portal form and feature names.
3. Still on **Clients**, create a **user client** assigned to that policy. Copy its Client ID and Client Secret into the local environment shown below. Do not create or use a trusted-server client or server policy for the distributed game.

The client needs only EOS Game Services through Connect, Lobbies, and P2P; no Epic account login or Epic Games Launcher is used. The EOS SDK `Platform.Options` in this integration requires the user-client Client ID and Client Secret, so treat both as extractable game-client configuration and keep the policy narrow. Epic describes the client and policy setup in its [official guide](https://dev.epicgames.com/docs/epic-online-services/eos-fundamentals/client-and-client-policy/client-policy-guide).

## Persistent client configuration

Wodra reads EOS client configuration in this order: non-empty environment-variable overrides (field by field), `%LOCALAPPDATA%\Wodra\eos-client.json`, then `eos-client.json` beside the running application. Godot editor runs also search the game project and managed assembly locations, so the per-user file works without relying on Godot's working directory. If no source is present, EOS stays unavailable and offline riding continues.

To configure a Windows user once, create `%LOCALAPPDATA%\Wodra` and copy the tracked `eos-client.example.json` there as `eos-client.json`. Replace its five placeholders with the Wodra Product, Sandbox and Deployment IDs and the restricted **Wodra Player** user client credentials. Keep the real file out of the repository; `eos-client.json` is ignored by Git. Restart Wodra after editing. You can still override individual values for automated tests or one-off runs:

```powershell
$env:WODRA_EOS_PRODUCT_ID = '<Product ID>'
$env:WODRA_EOS_SANDBOX_ID = '<Sandbox ID>'
$env:WODRA_EOS_DEPLOYMENT_ID = '<Deployment ID>'
$env:WODRA_EOS_CLIENT_ID = '<User client ID>'
$env:WODRA_EOS_CLIENT_SECRET = '<User client secret required by the SDK client configuration>'
```

Configuration values and secrets are never emitted to Wodra logs. Diagnostics may identify the source path and missing field names. Treat all game-client configuration, including a client credential embedded or supplied at launch, as extractable. Keep the policy narrowly scoped and never use a trusted-server credential or policy in a distributed client. No encryption or obfuscation makes a packaged client secret private. The SDK's Connect bindings define Device ID as an identity for the current local OS user profile. `WODRA_EOS_CACHE_DIR` changes EOS's cache directory; it does not create another Device ID. Multiple Wodra processes under the same Windows account therefore reuse that account's Device ID and Product User ID.

Connect calls `CreateDeviceId`, logs in with the SDK's `DeviceidAccessToken` credential, and creates a Product User ID only for the documented new-user (`InvalidUser` plus continuance token) response. On desktop, the SDK reuses the Device ID for the current OS user profile across application runs and reinstalls. The platform `CacheDirectory` defaults to `user://settings/eos-cache`; `WODRA_EOS_CACHE_DIR` can select a different cache location for storage isolation, but it does not isolate identity. Do not delete Device ID credentials or copy EOS cache/credential data to manufacture test identities.

For a reliable two-player test, use two PCs, each under its own Windows account, with the same Wodra build, Portal configuration and route. A supported single-PC setup is to launch one Wodra process under each of two distinct Windows user profiles; install the same client configuration for each profile (or set overrides for each account). The EOS Device ID cache remains private to each Windows user profile. Do not copy EOS identity caches between Windows accounts or computers. Separate cache folders may still be used for file isolation, but identity separation comes from the OS user profiles. Confirm the Portal deployment permits both users. On the next Debug run, compare the logged `productUserIdHash` values: different values establish distinct Product User IDs without exposing either ID; matching values mean the processes still share an identity.

## Runtime behaviour and protocol

- A single platform instance is initialized and ticked from Godot's main loop. Solo riding does not wait for online availability. Platform shutdown runs when the main game node exits.
- Lobbies are public and scoped by a deterministic bucket derived from protocol version, route ID, and route geometry fingerprint. Exact compatibility values are also published as lobby attributes. Lobby capacity is ten.
- Simultaneous lobby creation is followed by a discovery settle and convergence on a compatible, available lobby.
- EOS Product User IDs are multiplayer rider IDs. Only existing rider state and explicit reposition protocol messages are exchanged. Rider snapshots use the existing local publish rate (about 6–7 updates per second) over unreliable channel 0; reposition events use reliable ordered channel 1. Sequence tracking accounts for cross-channel arrival order, rejects duplicate/stale data, and prevents a delayed pre-reposition snapshot from moving a rider back.
- P2P packets are accepted only from current lobby members on Wodra's socket. State/reposition payloads also pass the shared protocol identity, range, sequence, route ID, and fingerprint validation.
- Incoming P2P connections are accepted only for members of the current lobby. EOS P2P `AllowRelays` is requested explicitly, preserving direct peer connections first and allowing relay fallback. If a platform does not permit changing this setting, the SDK default is already `AllowRelays`; the condition is logged.

### Windows network access prompt

Wodra's WebSocket provider uses `ClientWebSocket`, so the game client makes an outbound connection and does not open a server listener. EOS P2P owns its networking sockets inside the Wodra process and may negotiate direct peer traffic after another lobby member joins; EOS relay traffic is also allowed. The observed firewall prompt appeared at peer-connection time, consistent with Windows seeing the process participate in inbound/direct P2P traffic. The exact packet path depends on NAT and network policy, and the application cannot force Windows to display its consent dialog earlier without attempting inbound communication.

When Online Riding is enabled, the startup screen tells users that Windows may request network access if direct peer connections need it. If the prompt appears, allow the signed/published Wodra executable on the appropriate private network only when the user trusts that network. Development runs may identify Godot's executable instead of the packaged Wodra executable, so Windows can prompt separately for those paths. Wodra does not install firewall rules, require elevation, or open a broad port range. If relay traffic succeeds without inbound permission, no exception is required.
- Authentication expiration and lost sessions flow into the existing reconnect loop. Reauthentication checks the returned PUID against the original process identity and refuses to silently switch riders.
- FIT and simulation remain local authorities. Lobby membership does not control cycling or recording.
- On normal application shutdown the session is left, notifications/connections are cleaned up, then EOS is shut down.

## Validation status

The linked EOS C# SDK build path has been compiled locally in Debug and Release. This verifies source/API compatibility and native-library packaging into build output; it does not verify Developer Portal permissions, successful live authentication, lobby visibility, NAT traversal, or two-client P2P. Those require valid Portal values and two distinct EOS identities. Godot interactive two-client validation remains a release gate.

For a live check, install the client configuration for each Windows user, enable Online Riding, and start the same route and route fingerprint. In Debug logs, confirm the PUID hashes differ, both lobby membership snapshots report two distinct members, and each process logs `eos_p2p_first_rider_state_sent` and `eos_p2p_first_rider_state_received` for the other's hashed identity. `eos_debug_first_rider_state_send_attempt` reports the first SDK result even when it fails. `eos_debug_rider_packet_received` confirms a deserialised state packet reached the process, while `eos_debug_packet_rejected` identifies sender/socket/protocol/validation/sequence rejection. The in-ride status remains **waiting for rider state** until a recent valid snapshot is accepted, then advances to **rider-state exchange active**. Each client should render the other cyclist and show a Catch Up offer when the separation threshold is met. `eos_debug_p2p_request_waiting_for_membership` indicates that request/membership callbacks arrived out of order; acceptance is deferred until lobby membership is confirmed. Then test disconnect/reconnect and closing the game during a lobby session. Two windows launched under one Windows account remain one Device ID identity regardless of separate `WODRA_EOS_CACHE_DIR` values.

## Packaging and distribution

`publish.ps1` performs the Release solution build before Godot export. With the SDK root configured, `EOSSDK-Win64-Shipping.dll` is copied into the game's managed output and must be included in the final distributable alongside the managed assembly. Verify the DLL is present in the published directory/archive. Do not include the SDK source tree. Review the installed SDK's license and ThirdPartyNotices and satisfy Epic's current distribution and end-user notice requirements before shipping.

The publisher excludes EOS credentials by default. To explicitly include an approved client configuration from a private file outside the Git checkout, run:

```powershell
.\publish.ps1 -EosClientConfigPath "$env:LOCALAPPDATA\Wodra\eos-client.json"
```

Only use the restricted, user-required **Wodra Player** client policy values. The generated Windows application and any public GitHub Release package can be inspected, so every packaged client credential is extractable. Do not put trusted-server credentials in this file. To prepare another device without packaging credentials, copy the JSON through your private configuration process into that Windows user's `%LOCALAPPDATA%\Wodra\eos-client.json`; leave the per-user EOS identity cache on that device untouched.

Keep the SDK package, credentials, local EOS cache, and test-user data outside Git. See Epic's official [EOS SDK reference](https://dev.epicgames.com/docs/epic-online-services/eos-get-started/eos-get-started-reference), [Connect API](https://dev.epicgames.com/docs/api-ref/interfaces/connect), [Lobby interface](https://dev.epicgames.com/docs/api-ref/interfaces/lobby), and [P2P guide](https://dev.epicgames.com/docs/epic-online-services/multiplayer/nat-p2p-interface/p2p-guide/p2p-intro) for portal and service setup.
