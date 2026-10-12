using System.Net.WebSockets;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Wodra.Multiplayer.Protocol;
using Wodra.Multiplayer.Server;

await MultiplayerIntegrationTests.RunAsync();

internal static class MultiplayerIntegrationTests
{
    private const string RouteId = "dunedin-one-way-oval-gardens";
    private const string Fingerprint = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    public static async Task RunAsync()
    {
        ProtocolRoundTripsAndRejectsInvalidData();
        await TwoClientLifecycleAsync();
        Console.WriteLine("PASS: multiplayer protocol validation and two-client server lifecycle");
    }

    private static void ProtocolRoundTripsAndRejectsInvalidData()
    {
        var encoded = MultiplayerProtocol.Serialize(new JoinSessionMessage
        {
            RouteId = RouteId, RouteFingerprint = Fingerprint, RoomCode = "test-room", DisplayName = "Rider One"
        });
        if (MultiplayerProtocol.Deserialize(encoded) is not JoinSessionMessage { DisplayName: "Rider One" })
            throw new InvalidOperationException("JoinSession protocol roundtrip failed.");
        if (MultiplayerProtocol.Deserialize("{"u8) is not null)
            throw new InvalidOperationException("Malformed JSON was accepted.");
        if (MultiplayerProtocol.ValidateJoin(new JoinSessionMessage { RouteId = RouteId, RouteFingerprint = "bad", RoomCode = "r", DisplayName = "R" }) is null)
            throw new InvalidOperationException("Invalid route fingerprint was accepted.");
        if (MultiplayerProtocol.ValidateState(new RiderStateMessage { RiderId = "r", DistanceMeters = double.NaN }, "r") is null)
            throw new InvalidOperationException("Non-finite rider state was accepted.");
    }

    private static async Task TwoClientLifecycleAsync()
    {
        await using var app = MultiplayerServer.Create([], "http://127.0.0.1:0");
        await app.StartAsync();
        var server = app.Services.GetRequiredService<IServer>();
        var address = server.Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        var httpUri = new Uri(address);
        var wsUri = new UriBuilder(httpUri) { Scheme = "ws", Path = "/ws" }.Uri;
        using var first = new SimulatedClient();
        using var second = new SimulatedClient();
        using var incompatible = new SimulatedClient();

        await first.JoinAsync(wsUri, "Rider One", Fingerprint);
        await second.JoinAsync(wsUri, "Rider Two", Fingerprint);
        if (first.RiderId == second.RiderId) throw new InvalidOperationException("Server assigned a duplicate rider identity.");
        var firstJoined = await first.ReadUntilAsync<RiderJoinedMessage>();
        var secondJoined = await second.ReadUntilAsync<RiderJoinedMessage>();
        if (firstJoined.Rider.DisplayName != "Rider Two" || secondJoined.Rider.DisplayName != "Rider One")
            throw new InvalidOperationException("Connected riders were not discovered by each other.");

        await first.SendAsync(new RiderStateMessage
        {
            RiderId = first.RiderId, DisplayName = "spoofed", DistanceMeters = 2130,
            SpeedKph = 25.5, CadenceRpm = 85, PowerWatts = 264, Sequence = 1
        });
        var liveState = await second.ReadUntilAsync<RiderStateMessage>();
        if (liveState.DistanceMeters != 2130 || liveState.DisplayName != "Rider One" || liveState.PowerWatts != 264)
            throw new InvalidOperationException("Rider state was not relayed or server-side name sanitization failed.");

        await second.SendAsync(new RiderStateMessage
        {
            RiderId = second.RiderId, DistanceMeters = 1550, SpeedKph = 18, CadenceRpm = 72,
            PowerWatts = 180, Paused = true, Sequence = 1
        });
        var paused = await first.ReadUntilAsync<RiderStateMessage>();
        if (!paused.Paused || paused.DistanceMeters != 1550)
            throw new InvalidOperationException("Rider pause state was not relayed independently.");

        await incompatible.ConnectAndSendJoinAsync(wsUri, "Rider Three", new string('b', 64));
        var rejected = await incompatible.ReadUntilAsync<JoinRejectedMessage>();
        if (!rejected.Reason.Contains("fingerprint", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("A mismatched route fingerprint was not rejected.");

        await first.LeaveAsync();
        var left = await second.ReadUntilAsync<RiderLeftMessage>();
        if (left.RiderId != first.RiderId) throw new InvalidOperationException("Rider departure was not broadcast.");

        using var reconnected = new SimulatedClient();
        await reconnected.JoinAsync(wsUri, "Rider One", Fingerprint);
        var rejoinAtExisting = await second.ReadUntilAsync<RiderJoinedMessage>();
        var rejoinAtNew = await reconnected.ReadUntilAsync<RiderJoinedMessage>();
        if (rejoinAtExisting.Rider.DisplayName != "Rider One" || rejoinAtNew.Rider.DisplayName != "Rider Two")
            throw new InvalidOperationException("Reconnecting rider was not discovered by both clients.");

        var droppedRiderId = reconnected.RiderId;
        reconnected.Abort();
        var dropped = await second.ReadUntilAsync<RiderLeftMessage>();
        if (dropped.RiderId != droppedRiderId) throw new InvalidOperationException("Abrupt disconnect did not remove the rider.");
        await second.LeaveAsync();
        await incompatible.LeaveAsync();
        await app.StopAsync();
    }

    private sealed class SimulatedClient : IDisposable
    {
        private readonly ClientWebSocket _socket = new();
        public string RiderId { get; private set; } = "";

        public async Task JoinAsync(Uri uri, string name, string fingerprint)
        {
            await ConnectAndSendJoinAsync(uri, name, fingerprint);
            var accepted = await ReadUntilAsync<JoinAcceptedMessage>();
            RiderId = accepted.RiderId;
        }

        public async Task ConnectAndSendJoinAsync(Uri uri, string name, string fingerprint)
        {
            await _socket.ConnectAsync(uri, CancellationToken.None);
            await SendAsync(new JoinSessionMessage
            {
                RouteId = RouteId, RouteFingerprint = fingerprint, RoomCode = "test-room", DisplayName = name
            });
        }

        public Task SendAsync(ProtocolMessage message) => _socket.SendAsync(
            MultiplayerProtocol.Serialize(message), WebSocketMessageType.Text, true, CancellationToken.None);

        public async Task<T> ReadUntilAsync<T>() where T : ProtocolMessage
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            while (true)
            {
                var buffer = new byte[MultiplayerProtocol.MaximumMessageBytes];
                var result = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer), timeout.Token);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    if (_socket.State == WebSocketState.CloseReceived)
                        await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Acknowledged", CancellationToken.None);
                    throw new InvalidOperationException("Server closed before expected event.");
                }
                var message = MultiplayerProtocol.Deserialize(buffer.AsSpan(0, result.Count))
                    ?? throw new InvalidOperationException("Server emitted invalid protocol JSON.");
                if (message is T expected) return expected;
                if (message is ErrorMessage error) throw new InvalidOperationException($"Server error {error.Code}: {error.Message}");
            }
        }

        public async Task LeaveAsync()
        {
            if (_socket.State != WebSocketState.Open) return;
            await SendAsync(new LeaveSessionMessage());
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try
            {
                var result = await _socket.ReceiveAsync(new ArraySegment<byte>(new byte[64]), timeout.Token);
                if (result.MessageType == WebSocketMessageType.Close && _socket.State == WebSocketState.CloseReceived)
                    await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Acknowledged", CancellationToken.None);
            }
            catch (OperationCanceledException) { }
        }

        public void Abort() => _socket.Abort();

        public void Dispose()
        {
            if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived) _socket.Abort();
            _socket.Dispose();
        }
    }
}
