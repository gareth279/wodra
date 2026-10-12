using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Wodra.Multiplayer.Protocol;

namespace IndoorCycling.Game.Multiplayer;

public sealed class WebSocketMultiplayerProvider : IMultiplayerProvider
{
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly ClientWebSocket _socket = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly ConcurrentQueue<ProtocolMessage> _events = new();
    private readonly Channel<RiderStateMessage> _outgoing = Channel.CreateBounded<RiderStateMessage>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
        SingleWriter = false
    });
    private CancellationTokenSource? _lifetime;
    private Task? _receiveTask;
    private Task? _sendTask;
    private volatile string _status = "Disconnected";
    private int _disposed;

    public string Status => _status;
    public bool IsConnected => _socket.State == WebSocketState.Open;
    public bool HasClient => Volatile.Read(ref _disposed) == 0;
    public string RiderId { get; private set; } = "";
    public string DisplayName { get; private set; } = "";

    public async Task<bool> CheckAvailabilityAsync(string serverAddress, CancellationToken cancellationToken = default)
    {
        _status = "Connecting…";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            using var response = await Http.GetAsync(BuildHttpUri(serverAddress, "healthz"),
                HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            var available = response.IsSuccessStatusCode;
            _status = available ? "Available" : "Server unavailable";
            return available;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or InvalidOperationException)
        {
            _status = "Server unavailable";
            return false;
        }
    }

    public async Task<RoutePresenceResult> GetRoutePresenceAsync(string serverAddress, string routeId,
        string routeFingerprint, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            var path = $"presence?routeId={Uri.EscapeDataString(routeId)}&routeFingerprint={Uri.EscapeDataString(routeFingerprint)}";
            using var response = await Http.GetAsync(BuildHttpUri(serverAddress, path),
                HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode) return RoutePresenceResult.Unavailable($"Presence unavailable ({(int)response.StatusCode}).");
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token);
            if (!json.RootElement.TryGetProperty("activeRiders", out var activeRiders) ||
                !activeRiders.TryGetInt32(out var count))
                return RoutePresenceResult.Unavailable("Presence response was invalid.");
            return new RoutePresenceResult(true, Math.Max(0, count));
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or InvalidOperationException or JsonException)
        {
            return RoutePresenceResult.Unavailable(ex.Message);
        }
    }

    public async Task JoinRouteSessionAsync(string serverAddress, string displayName, string sessionId,
        string routeId, string routeFingerprint, CancellationToken cancellationToken = default)
    {
        if (routeFingerprint.Length != 64)
            throw new InvalidOperationException("The selected route has no valid geometry fingerprint.");
        var uri = NormalizeAddress(serverAddress);
        DisplayName = displayName.Trim();
        _status = "Connecting…";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            await _socket.ConnectAsync(uri, timeout.Token);
            await SendMessageAsync(new JoinSessionMessage
            {
                RouteId = routeId,
                RouteFingerprint = routeFingerprint,
                RoomCode = sessionId.Trim(),
                DisplayName = DisplayName
            }, timeout.Token);
            var response = await ReceiveMessageAsync(_socket, timeout.Token);
            if (response is JoinRejectedMessage rejected)
                throw new InvalidOperationException(rejected.Reason);
            if (response is not JoinAcceptedMessage accepted)
                throw new InvalidOperationException("Server did not accept the multiplayer session.");
            RiderId = accepted.RiderId;
            _status = "Connected";
            _lifetime = new CancellationTokenSource();
            _receiveTask = ReceiveLoopAsync(_lifetime.Token);
            _sendTask = SendLoopAsync(_lifetime.Token);
        }
        catch
        {
            _status = "Connection failed";
            if (_socket.State is WebSocketState.Open or WebSocketState.Connecting)
            {
                try { await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Join failed", CancellationToken.None); }
                catch (WebSocketException) { }
            }
            throw;
        }
    }

    public bool TryQueueState(RiderStateMessage state) =>
        IsConnected && _outgoing.Writer.TryWrite(state with { RiderId = RiderId, DisplayName = DisplayName });

    public Task PublishRepositionAsync(RiderRepositionedMessage state, CancellationToken cancellationToken = default) =>
        SendMessageAsync(state with { RiderId = RiderId, DisplayName = DisplayName }, cancellationToken);

    public IReadOnlyList<ProtocolMessage> DrainEvents()
    {
        var result = new List<ProtocolMessage>();
        while (_events.TryDequeue(out var message)) result.Add(message);
        return result;
    }

    public async Task LeaveAsync()
    {
        if (_socket.State == WebSocketState.Open)
        {
            using var sendTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            try { await SendMessageAsync(new LeaveSessionMessage(), sendTimeout.Token); }
            catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or OperationCanceledException) { }
        }
        _status = "Disconnected";
        _lifetime?.Cancel();
        _outgoing.Writer.TryComplete();
        await AwaitLoopsAsync();
        if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            using var closeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            try { await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Leaving session", closeTimeout.Token); }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException) { }
        }
    }

    public Task LeaveSessionAsync() => LeaveAsync();

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && _socket.State == WebSocketState.Open)
            {
                var message = await ReceiveMessageAsync(_socket, cancellationToken);
                if (message is null) break;
                _events.Enqueue(message);
            }
            if (!cancellationToken.IsCancellationRequested) _status = "Disconnected from server";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex) when (ex is WebSocketException or IOException or InvalidDataException)
        {
            _status = $"Network lost: {ex.Message}";
        }
    }

    private async Task SendLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var state in _outgoing.Reader.ReadAllAsync(cancellationToken))
                await SendMessageAsync(state, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex) when (ex is WebSocketException or IOException or ObjectDisposedException)
        {
            _status = $"Network lost: {ex.Message}";
        }
    }

    private async Task SendMessageAsync(ProtocolMessage message, CancellationToken cancellationToken)
    {
        var bytes = MultiplayerProtocol.Serialize(message);
        if (bytes.Length > MultiplayerProtocol.MaximumMessageBytes) throw new InvalidDataException("Message exceeds protocol limit.");
        await _sendGate.WaitAsync(cancellationToken);
        try { await _socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken); }
        finally { _sendGate.Release(); }
    }

    private static async Task<ProtocolMessage?> ReceiveMessageAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[2048];
        using var stream = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            if (result.MessageType != WebSocketMessageType.Text) throw new InvalidDataException("Server sent a non-text message.");
            if (stream.Length + result.Count > MultiplayerProtocol.MaximumMessageBytes)
                throw new InvalidDataException("Server message exceeds protocol limit.");
            stream.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);
        return MultiplayerProtocol.Deserialize(stream.GetBuffer().AsSpan(0, checked((int)stream.Length)))
            ?? throw new InvalidDataException("Server sent a malformed or unsupported protocol message.");
    }

    private static Uri NormalizeAddress(string address)
    {
        var value = address.Trim();
        if (!value.Contains("://", StringComparison.Ordinal)) value = "ws://" + value;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) throw new InvalidOperationException("Enter a valid multiplayer server address.");
        if (uri.Scheme is "http" or "https")
            uri = new UriBuilder(uri) { Scheme = uri.Scheme == "https" ? "wss" : "ws", Path = string.IsNullOrWhiteSpace(uri.AbsolutePath) || uri.AbsolutePath == "/" ? "/ws" : uri.AbsolutePath }.Uri;
        if (uri.Scheme is not ("ws" or "wss")) throw new InvalidOperationException("Server address must use ws:// or wss://.");
        if (uri.AbsolutePath is "" or "/") uri = new UriBuilder(uri) { Path = "/ws" }.Uri;
        return uri;
    }

    private static Uri BuildHttpUri(string address, string path)
    {
        var socketUri = NormalizeAddress(address);
        var builder = new UriBuilder(socketUri)
        {
            Scheme = socketUri.Scheme == "wss" ? "https" : "http",
            Path = "/" + path.TrimStart('/'),
            Query = ""
        };
        return builder.Uri;
    }

    private async Task AwaitLoopsAsync()
    {
        var tasks = new[] { _receiveTask, _sendTask }.Where(task => task is not null).Cast<Task>().ToArray();
        try { await Task.WhenAll(tasks); }
        catch (OperationCanceledException) { }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await LeaveAsync();
        _lifetime?.Dispose();
        _socket.Dispose();
        _sendGate.Dispose();
    }
}
