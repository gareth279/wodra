using System.Net.WebSockets;
using System.Net;
using System.Net.Http.Json;
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
        Console.WriteLine("PASS: multiplayer protocol validation, public route presence and two-client server lifecycle");
    }

    private static void ProtocolRoundTripsAndRejectsInvalidData()
    {
        if (RiderDisplayName.Resolve(" Sue ", "Gareth") != "Sue" ||
            RiderDisplayName.Resolve("Test", "Gareth") != "Test" ||
            RiderDisplayName.Resolve("  ", "Legacy") != "Legacy")
            throw new InvalidOperationException("Selected profile display-name resolution failed.");
        var encoded = MultiplayerProtocol.Serialize(new JoinSessionMessage
        {
            RouteId = RouteId, RouteFingerprint = Fingerprint, RoomCode = "test-room", DisplayName = RiderDisplayName.Resolve("Test", "Gareth")
        });
        if (MultiplayerProtocol.Deserialize(encoded) is not JoinSessionMessage { DisplayName: "Test" })
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
        using var http = new HttpClient();
        var presenceUri = new Uri(httpUri, $"presence?routeId={RouteId}&routeFingerprint={Fingerprint}");
        if (await ReadActiveRidersAsync(http, presenceUri) != 0)
            throw new InvalidOperationException("An empty public route session reported riders.");
        using var first = new SimulatedClient();
        using var second = new SimulatedClient();
        using var incompatible = new SimulatedClient();

        await first.JoinAsync(wsUri, "Test", Fingerprint);
        await second.JoinAsync(wsUri, "Sue", Fingerprint);
        if (first.RiderId == second.RiderId) throw new InvalidOperationException("Server assigned a duplicate rider identity.");
        var firstJoined = await first.ReadUntilAsync<RiderJoinedMessage>();
        var secondJoined = await second.ReadUntilAsync<RiderJoinedMessage>();
        if (firstJoined.Rider.DisplayName != "Sue" || secondJoined.Rider.DisplayName != "Test")
            throw new InvalidOperationException("Connected riders were not discovered by each other.");

        await first.SendAsync(new RiderStateMessage
        {
            RiderId = first.RiderId, DisplayName = "spoofed", DistanceMeters = 2130,
            SpeedKph = 25.5, CadenceRpm = 85, PowerWatts = 264, Sequence = 1
        });
        var liveState = await second.ReadUntilAsync<RiderStateMessage>();
        if (liveState.DistanceMeters != 2130 || liveState.DisplayName != "Test" || liveState.PowerWatts != 264)
            throw new InvalidOperationException("Rider state was not relayed or server-side name sanitization failed.");
        await Task.Delay(MultiplayerProtocol.MinimumUpdateIntervalMs + 10);
        await second.SendAsync(new RiderStateMessage
        {
            RiderId = second.RiderId, DisplayName = "spoofed", DistanceMeters = 2200,
            SpeedKph = 23, CadenceRpm = 82, PowerWatts = 210, Sequence = 1
        });
        var sueState = await first.ReadUntilAsync<RiderStateMessage>();
        if (sueState.DistanceMeters != 2200 || sueState.DisplayName != "Sue")
            throw new InvalidOperationException("Sue's selected profile name did not reach Test through rider state.");
        if (await ReadActiveRidersAsync(http, presenceUri) != 2)
            throw new InvalidOperationException("Public route presence did not include both active profile riders.");

        var repositionMessage = new RiderRepositionedMessage
        {
            RouteId = RouteId, RouteFingerprint = Fingerprint,
            RiderId = first.RiderId, DisplayName = "spoofed", DistanceMeters = 3120,
            SpeedKph = 35, CadenceRpm = 85, PowerWatts = 264, Sequence = 2
        };
        if (MultiplayerProtocol.Deserialize(MultiplayerProtocol.Serialize(repositionMessage)) is not RiderRepositionedMessage)
            throw new InvalidOperationException("Rider reposition protocol roundtrip failed.");
        if (MultiplayerProtocol.ValidateReposition(repositionMessage with { RouteFingerprint = new string('b', 64) },
                first.RiderId, RouteId, Fingerprint) is null)
            throw new InvalidOperationException("A reposition for another route was accepted.");
        await first.SendAsync(repositionMessage);
        var reposition = await second.ReadUntilAsync<RiderRepositionedMessage>();
        if (reposition.DistanceMeters != 3120 || reposition.SpeedKph != 35 || reposition.DisplayName != "Test")
            throw new InvalidOperationException("Explicit rider reposition was not relayed or name sanitization failed.");

        await Task.Delay(MultiplayerProtocol.MinimumUpdateIntervalMs + 10);
        await second.SendAsync(new RiderStateMessage
        {
            RiderId = second.RiderId, DisplayName = "spoofed", DistanceMeters = 1550, SpeedKph = 18, CadenceRpm = 72,
            PowerWatts = 180, Paused = true, Sequence = 2
        });
        var paused = await first.ReadUntilAsync<RiderStateMessage>();
        if (!paused.Paused || paused.DistanceMeters != 1550)
            throw new InvalidOperationException("Rider pause state was not relayed independently.");
        if (await ReadActiveRidersAsync(http, presenceUri) != 1)
            throw new InvalidOperationException("Paused riders should not count as active route presence.");

        await incompatible.ConnectAndSendJoinAsync(wsUri, "Rider Three", new string('b', 64));
        var rejected = await incompatible.ReadUntilAsync<JoinRejectedMessage>();
        if (!rejected.Reason.Contains("fingerprint", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("A mismatched route fingerprint was not rejected.");

        await first.LeaveAsync();
        var left = await second.ReadUntilAsync<RiderLeftMessage>();
        if (left.RiderId != first.RiderId) throw new InvalidOperationException("Rider departure was not broadcast.");

        using var reconnected = new SimulatedClient();
        await reconnected.JoinAsync(wsUri, "Test", Fingerprint);
        var rejoinAtExisting = await second.ReadUntilAsync<RiderJoinedMessage>();
        var rejoinAtNew = await reconnected.ReadUntilAsync<RiderJoinedMessage>();
        if (rejoinAtExisting.Rider.DisplayName != "Test" || rejoinAtNew.Rider.DisplayName != "Sue")
            throw new InvalidOperationException("Reconnecting rider was not discovered by both clients.");

        var droppedRiderId = reconnected.RiderId;
        reconnected.Abort();
        var dropped = await second.ReadUntilAsync<RiderLeftMessage>();
        if (dropped.RiderId != droppedRiderId) throw new InvalidOperationException("Abrupt disconnect did not remove the rider.");
        await second.LeaveAsync();
        if (await ReadActiveRidersAsync(http, presenceUri) != 0)
            throw new InvalidOperationException("Disconnected riders remained in public route presence.");
        await incompatible.LeaveAsync();
        await app.StopAsync();
    }

    private static async Task<int> ReadActiveRidersAsync(HttpClient http, Uri uri)
    {
        var payload = await http.GetFromJsonAsync<RoutePresenceResponse>(uri)
            ?? throw new InvalidOperationException("Route presence returned an empty response.");
        return payload.ActiveRiders;
    }

    private sealed record RoutePresenceResponse(string RouteId, string RouteFingerprint, int ActiveRiders);

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
                RouteId = RouteId, RouteFingerprint = fingerprint, RoomCode = "public", DisplayName = name
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
