using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wodra.Multiplayer.Protocol;

namespace IndoorCycling.Game.Multiplayer;

/// <summary>Minimal game-facing multiplayer contract. Providers own transport and service details.</summary>
public interface IMultiplayerProvider : IAsyncDisposable
{
	string Status { get; }
	bool IsConnected { get; }
	string RiderId { get; }
	string DisplayName { get; }
	Task<bool> CheckAvailabilityAsync(string serverAddress, CancellationToken cancellationToken = default);
	Task<RoutePresenceResult> GetRoutePresenceAsync(string serverAddress, string routeId,
		string routeFingerprint, CancellationToken cancellationToken = default);
	Task JoinRouteSessionAsync(string serverAddress, string displayName, string sessionId,
		string routeId, string routeFingerprint, CancellationToken cancellationToken = default);
	bool TryQueueState(RiderStateMessage state);
	Task PublishRepositionAsync(RiderRepositionedMessage state, CancellationToken cancellationToken = default);
	IReadOnlyList<ProtocolMessage> DrainEvents();
	Task LeaveSessionAsync();
}

public sealed record RoutePresenceResult(bool IsAvailable, int? ActiveRiders, string? Message = null)
{
	public static RoutePresenceResult Unavailable(string? message = null) => new(false, null, message);
}
