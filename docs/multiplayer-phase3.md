# Multiplayer Phase 3: Automatic Online Riding

Online Riding is optional and defaults to off. When enabled, Wodra checks the configured WebSocket server for availability and automatically joins the public session for the selected route after a trainer ride starts. Local trainer communication, ride simulation and FIT recording start independently and continue if the service is unavailable.

The in-ride **Menu → Multiplayer** page shows connection status and the public session for the current route. **Check riders on this route** requests an aggregate presence count. Only riders with a fresh state who are not paused are counted. The server matches route ID and geometry fingerprint, so incompatible route data cannot share a session. Rider position and ride telemetry are broadcast while Online Riding is enabled.

The existing server is the development WebSocket provider. It exposes `GET /healthz`, aggregate `GET /presence?routeId=…&routeFingerprint=…`, and `ws://…/ws`. The provider boundary is `IMultiplayerProvider`; a hosted provider can replace it without changing ride simulation. This phase does not add accounts, matchmaking, EOS, or a public service.

## Local development server

From the repository root, run:

```powershell
dotnet run --project src/Wodra.Multiplayer.Server/Wodra.Multiplayer.Server.csproj --urls http://127.0.0.1:8787
```

The default server address is `ws://127.0.0.1:8787/ws`. It can be changed in the advanced development multiplayer section. The address is stored in `user://settings/settings.json` as `MultiplayerServerAddress`; `OnlineRidingEnabled` is stored beside it and defaults to `false` for existing and new installations.

When the server becomes unavailable during a ride, Wodra keeps the ride local and retries with a capped backoff of 1, 2, 4, 8, 16, then 30 seconds. Turning Online Riding off cancels connection attempts, closes the multiplayer session and clears remote riders. Turning it back on resumes availability checks and reconnection for an active trainer ride.

## Two-client check

1. Start the local server with the command above.
2. Launch two game clients, enable **Online Riding** in both startup panels, and start dummy trainer rides on the same route.
3. Open **Menu → Multiplayer**. Each client should report Connected and show the other rider after state arrives.
4. Turn Online Riding off in one client. It should leave the public session while its local ride and FIT recording continue.
5. Turn it back on; the client should reconnect to the route session.

The advanced development section retains explicit server, display name and room controls. It is gated by Online Riding so sharing is always opt-in.
