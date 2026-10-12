using System.Net.WebSockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wodra.Multiplayer.Protocol;

namespace Wodra.Multiplayer.Server;

public static class MultiplayerServer
{
    public static WebApplication Create(string[]? args = null, string? urls = null)
    {
        var builder = WebApplication.CreateBuilder(args ?? []);
        if (!string.IsNullOrWhiteSpace(urls)) builder.WebHost.UseUrls(urls);
        builder.Services.AddSingleton<SessionRegistry>();
        var app = builder.Build();
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });
        app.MapGet("/healthz", () => Results.Ok(new { status = "ok", protocolVersion = MultiplayerProtocol.CurrentVersion }));
        app.MapGet("/presence", (string routeId, string routeFingerprint, SessionRegistry sessions) =>
        {
            if (string.IsNullOrWhiteSpace(routeId) || routeId.Length > 80 ||
                routeFingerprint is not { Length: 64 } || !routeFingerprint.All(Uri.IsHexDigit))
                return Results.BadRequest(new { error = "Valid routeId and routeFingerprint are required." });
            return Results.Ok(new { routeId, routeFingerprint = routeFingerprint.ToLowerInvariant(),
                activeRiders = sessions.GetPublicActiveRiderCount(routeId, routeFingerprint) });
        });
        app.Map("/ws", HandleWebSocketAsync);
        return app;
    }

    private static async Task HandleWebSocketAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync("WebSocket upgrade required.");
            return;
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        var connection = new ClientConnection(socket);
        SessionRegistry? registry = context.RequestServices.GetRequiredService<SessionRegistry>();
        RoomSession? room = null;
        try
        {
            var first = await ReceiveMessageAsync(socket, context.RequestAborted);
            if (first is not JoinSessionMessage join)
            {
                await connection.SendAsync(new JoinRejectedMessage { Reason = "JoinSession must be the first message." }, context.RequestAborted);
                return;
            }
            if (MultiplayerProtocol.ValidateJoin(join) is { } validationError)
            {
                await connection.SendAsync(new JoinRejectedMessage { Reason = validationError }, context.RequestAborted);
                return;
            }
            var joinResult = await registry.JoinAsync(connection, join, context.RequestAborted);
            if (joinResult.Error is { } rejection)
            {
                await connection.SendAsync(new JoinRejectedMessage { Reason = rejection }, context.RequestAborted);
                return;
            }
            room = joinResult.Room;
            connection.RiderId = joinResult.RiderId!;
            await connection.SendAsync(new JoinAcceptedMessage
            {
                RiderId = connection.RiderId,
                RouteId = join.RouteId,
                RouteFingerprint = join.RouteFingerprint.ToLowerInvariant(),
                RoomCode = join.RoomCode
            }, context.RequestAborted);
            foreach (var rider in joinResult.ExistingRiders)
                await connection.SendAsync(new RiderJoinedMessage { Rider = rider }, context.RequestAborted);

            while (socket.State == WebSocketState.Open && !context.RequestAborted.IsCancellationRequested)
            {
                var message = await ReceiveMessageAsync(socket, context.RequestAborted);
                if (message is null) break;
                if (message is LeaveSessionMessage) break;
                if (message is RiderRepositionedMessage reposition)
                {
                    var repositionError = room is null ? "Rider is not in a route room." :
                        MultiplayerProtocol.ValidateReposition(reposition, connection.RiderId, room.RouteId, room.Fingerprint);
                    if (repositionError is not null)
                    {
                        await connection.SendAsync(new ErrorMessage { Code = "invalid_reposition", Message = repositionError }, context.RequestAborted);
                        continue;
                    }
                    var repositionNow = Environment.TickCount64;
                    if (reposition.Sequence <= connection.LastSequence) continue;
                    if (connection.LastRepositionAtMs != 0 && repositionNow - connection.LastRepositionAtMs < 2_000)
                    {
                        await connection.SendAsync(new ErrorMessage { Code = "reposition_rate_limited", Message = "Rider repositioning is limited to once every two seconds." }, context.RequestAborted);
                        continue;
                    }
                    connection.LastSequence = reposition.Sequence;
                    connection.LastRepositionAtMs = repositionNow;
                    lock (room!.Clients)
                    {
                        connection.LastStateAtMs = repositionNow;
                        connection.LastState = new RiderStateMessage
                        {
                            RiderId = connection.RiderId, DisplayName = connection.DisplayName,
                            DistanceMeters = reposition.DistanceMeters, SpeedKph = reposition.SpeedKph,
                            CadenceRpm = reposition.CadenceRpm, PowerWatts = reposition.PowerWatts,
                            Paused = reposition.Paused, Sequence = reposition.Sequence
                        };
                    }
                    await registry.BroadcastRepositionAsync(room, connection, reposition with { DisplayName = connection.DisplayName }, context.RequestAborted);
                    continue;
                }
                if (message is not RiderStateMessage state)
                {
                    await connection.SendAsync(new ErrorMessage { Code = "unexpected_message", Message = "Expected RiderState, RiderRepositioned or LeaveSession." }, context.RequestAborted);
                    continue;
                }
                if (MultiplayerProtocol.ValidateState(state, connection.RiderId) is { } stateError)
                {
                    await connection.SendAsync(new ErrorMessage { Code = "invalid_state", Message = stateError }, context.RequestAborted);
                    continue;
                }
                var now = Environment.TickCount64;
                if (now - connection.LastStateAtMs < MultiplayerProtocol.MinimumUpdateIntervalMs)
                {
                    await connection.SendAsync(new ErrorMessage { Code = "rate_limited", Message = "Rider state updates are limited to approximately 13 per second." }, context.RequestAborted);
                    continue;
                }
                if (state.Sequence <= connection.LastSequence) continue;
                connection.LastSequence = state.Sequence;
                var sanitized = state with { DisplayName = connection.DisplayName };
                lock (room!.Clients)
                {
                    connection.LastStateAtMs = now;
                    connection.LastState = sanitized;
                }
                await registry.BroadcastStateAsync(room, connection, sanitized, context.RequestAborted);
            }
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
        catch (WebSocketException) { }
        catch (Exception ex)
        {
            appLog(context, $"WebSocket client ended: {ex.Message}");
        }
        finally
        {
            if (room is not null) await registry.LeaveAsync(room, connection, "disconnected");
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                try { await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Session closed", CancellationToken.None); }
                catch (WebSocketException) { }
            }
        }
    }

    private static void appLog(HttpContext context, string message) =>
        context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Wodra.Multiplayer").LogInformation("{Message}", message);

    private static async Task<ProtocolMessage?> ReceiveMessageAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[2048];
        using var stream = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            if (result.MessageType != WebSocketMessageType.Text) return null;
            if (stream.Length + result.Count > MultiplayerProtocol.MaximumMessageBytes) return null;
            stream.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);
        return MultiplayerProtocol.Deserialize(stream.GetBuffer().AsSpan(0, checked((int)stream.Length)));
    }
}

public sealed class SessionRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<string, RoomSession> _rooms = new(StringComparer.OrdinalIgnoreCase);

    public Task<JoinResult> JoinAsync(ClientConnection connection, JoinSessionMessage join, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var roomKey = $"{join.RouteId}\n{join.RoomCode}";
            if (_rooms.TryGetValue(roomKey, out var existing)
                && (!string.Equals(existing.RouteId, join.RouteId, StringComparison.Ordinal)
                    || !string.Equals(existing.Fingerprint, join.RouteFingerprint, StringComparison.OrdinalIgnoreCase)))
                return Task.FromResult(new JoinResult(null, null, [], "Room code is already in use with a different route fingerprint."));
            var room = existing ?? new RoomSession(roomKey, join.RoomCode, join.RouteId, join.RouteFingerprint.ToLowerInvariant());
            if (existing is null) _rooms.Add(roomKey, room);
            var riderId = Guid.NewGuid().ToString("N");
            connection.DisplayName = join.DisplayName.Trim();
            RiderDescriptor[] peers;
            lock (room.Clients)
            {
                peers = room.Clients.Select(client => new RiderDescriptor { RiderId = client.RiderId, DisplayName = client.DisplayName }).ToArray();
                room.Clients.Add(connection);
            }
            _ = BroadcastAsync(room, connection, new RiderJoinedMessage
            {
                Rider = new RiderDescriptor { RiderId = riderId, DisplayName = connection.DisplayName }
            }, cancellationToken);
            return Task.FromResult(new JoinResult(room, riderId, peers, null));
        }
    }

    public int GetPublicActiveRiderCount(string routeId, string routeFingerprint)
    {
        lock (_gate)
        {
            var key = $"{routeId}\npublic";
            if (!_rooms.TryGetValue(key, out var room) ||
                !string.Equals(room.Fingerprint, routeFingerprint, StringComparison.OrdinalIgnoreCase)) return 0;
            var now = Environment.TickCount64;
            lock (room.Clients)
                return room.Clients.Count(client => client.LastState is { Paused: false } &&
                    now - client.LastStateAtMs <= 6_000);
        }
    }

    public Task BroadcastStateAsync(RoomSession? room, ClientConnection sender, RiderStateMessage state, CancellationToken cancellationToken) =>
        room is null ? Task.CompletedTask : BroadcastAsync(room, sender, state, cancellationToken);

    public Task BroadcastRepositionAsync(RoomSession? room, ClientConnection sender, RiderRepositionedMessage state, CancellationToken cancellationToken) =>
        room is null ? Task.CompletedTask : BroadcastAsync(room, sender, state, cancellationToken);

    public async Task LeaveAsync(RoomSession room, ClientConnection connection, string reason)
    {
        var removed = false;
        lock (_gate)
        {
            lock (room.Clients)
            {
                removed = room.Clients.Remove(connection);
                if (room.Clients.Count == 0) _rooms.Remove(room.Key);
            }
        }
        if (removed)
            await BroadcastAsync(room, connection, new RiderLeftMessage { RiderId = connection.RiderId, Reason = reason }, CancellationToken.None);
    }

    private static async Task BroadcastAsync(RoomSession room, ClientConnection except, ProtocolMessage message, CancellationToken cancellationToken)
    {
        ClientConnection[] recipients;
        lock (room.Clients) recipients = room.Clients.Where(client => !ReferenceEquals(client, except)).ToArray();
        var payload = MultiplayerProtocol.Serialize(message);
        var tasks = recipients.Select(client => client.SendBytesAsync(payload, cancellationToken));
        try { await Task.WhenAll(tasks); }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException) { }
    }
}

public sealed class RoomSession(string key, string roomCode, string routeId, string fingerprint)
{
    public string Key { get; } = key;
    public string RoomCode { get; } = roomCode;
    public string RouteId { get; } = routeId;
    public string Fingerprint { get; } = fingerprint;
    public List<ClientConnection> Clients { get; } = [];
}

public sealed class ClientConnection(WebSocket socket)
{
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    public string RiderId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public long LastSequence { get; set; } = -1;
    public long LastStateAtMs { get; set; }
    public long LastRepositionAtMs { get; set; }
    public RiderStateMessage? LastState { get; set; }

    public Task SendAsync(ProtocolMessage message, CancellationToken cancellationToken) =>
        SendBytesAsync(MultiplayerProtocol.Serialize(message), cancellationToken);

    public async Task SendBytesAsync(byte[] payload, CancellationToken cancellationToken)
    {
        await _sendGate.WaitAsync(cancellationToken);
        try
        {
            if (socket.State == WebSocketState.Open)
                await socket.SendAsync(payload, WebSocketMessageType.Text, true, cancellationToken);
        }
        finally { _sendGate.Release(); }
    }
}

public sealed record JoinResult(RoomSession? Room, string? RiderId, IReadOnlyList<RiderDescriptor> ExistingRiders, string? Error);
