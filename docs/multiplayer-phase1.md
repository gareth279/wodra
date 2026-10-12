# Multiplayer Phase 1

Phase 1 exchanges rider state between game clients. It does not create remote cyclist models.

## Start a local server

The standalone server targets .NET 10 and listens on port 8787 by default:

```powershell
dotnet run --project src/Wodra.Multiplayer.Server -c Release
```

To select a bind address or port:

```powershell
$env:WODRA_MULTIPLAYER_URLS = 'http://0.0.0.0:8787'
dotnet run --project src/Wodra.Multiplayer.Server -c Release
```

The WebSocket endpoint is `/ws`; `GET /healthz` is a basic health check. For two clients on one PC, use `ws://127.0.0.1:8787/ws`. For other devices on a trusted LAN, use the host PC's LAN address and allow inbound TCP port 8787 through its firewall. Public self-hosting should put the server behind a TLS reverse proxy and connect with `wss://`; the server itself accepts plaintext HTTP/WebSockets.

Stop the server with Ctrl+C. Session membership is held in memory and is cleared when the process stops.

## Connect two Wodra clients

Build the game, launch two instances, and start a dummy trainer ride in each. Select the same route in both instances. During each ride open **Menu → Multiplayer**, then enter:

- Server: `ws://127.0.0.1:8787/ws`
- Display name: a different name in each instance
- Room code: the same code in both instances, for example `dunedin-test`

Select **Join session**. The page shows connection status, other rider count, distance, speed, power and riding/paused status. **Leave multiplayer** disconnects that client while its local ride continues. Ending the ride closes its multiplayer connection.

PowerShell launch example (set `$GodotExe` to the installed Godot .NET executable):

```powershell
$GodotExe = 'C:\Path\To\Godot_v4.6.2-stable_mono_win64.exe'
Start-Process -FilePath $GodotExe -ArgumentList @('--path', (Resolve-Path 'src/IndoorCycling.Game').Path)
Start-Process -FilePath $GodotExe -ArgumentList @('--path', (Resolve-Path 'src/IndoorCycling.Game').Path)
```

Choose the dummy trainer mode in each game instance; no physical trainer is needed. In both rides, open the in-ride menu and join the same route and room.

## Session matching and protocol

Messages are UTF-8 JSON objects with a `type` and `protocolVersion`. The Phase 2 protocol version is `2`; Phase 1 clients and servers must be updated together. The initial `JoinSession` carries the stable route ID, the existing SHA-256 route geometry fingerprint, room code and display name. A session is keyed by route ID and room code; clients presenting different fingerprints for that pair are rejected. The server assigns a random rider ID and relays `RiderJoined`, `RiderState`, `RiderRepositioned` and `RiderLeft` messages to peers.

Rider snapshots carry absolute ride distance, simulated speed, power, cadence, pause state and a monotonically increasing sequence number. The game publishes about 6–7 snapshots per second. The server caps messages at 8 KiB, validates names, IDs, finite value ranges and sequence numbers, and limits rider-state updates to approximately 13 per second. The protocol is defined in `src/Wodra.Multiplayer.Protocol`.

Run the automated two-client server/protocol integration check with:

```powershell
dotnet run --project tests/Wodra.Multiplayer.Integration.Tests -c Debug
```

## Phase 1 limits

- No remote 3D riders, drafting, collisions, leaderboards, accounts or matchmaking.
- The room code is a session selector, not authentication or access control. Use a trusted network or TLS proxy.
- Sessions are in-memory and have no persistence or host migration.
- Each client remains authoritative for its own simulation; remote state is informational.
- Network loss does not stop the local ride, trainer communication or FIT recording. Rejoin manually from the Multiplayer page.
