using Wodra.Multiplayer.Protocol;

if (EosSessionRecoveryPolicy.DecideMembership(identityMatches: true, lobbyHandleAvailable: true, localUserListed: true)
        != EosMembershipRecovery.MembershipRetained ||
    EosSessionRecoveryPolicy.DecideMembership(identityMatches: true, lobbyHandleAvailable: false, localUserListed: false)
        != EosMembershipRecovery.RejoinRequired ||
    EosSessionRecoveryPolicy.DecideMembership(identityMatches: true, lobbyHandleAvailable: true, localUserListed: false)
        != EosMembershipRecovery.RejoinRequired ||
    EosSessionRecoveryPolicy.DecideMembership(identityMatches: false, lobbyHandleAvailable: true, localUserListed: true)
        != EosMembershipRecovery.IdentityChanged)
    throw new InvalidOperationException("EOS auth recovery must reconcile authoritative local lobby membership and identity before deciding to reuse or rejoin.");
if (!EosSessionRecoveryPolicy.ShouldRetryAuthentication(1) || EosSessionRecoveryPolicy.ShouldRetryAuthentication(3) ||
    EosSessionRecoveryPolicy.RetryDelay(1) != TimeSpan.FromSeconds(2) ||
    EosSessionRecoveryPolicy.RetryDelay(2) != TimeSpan.FromSeconds(5))
    throw new InvalidOperationException("EOS authentication recovery retry policy must remain bounded with increasing backoff.");
if (EosSessionRecoveryPolicy.DecideMembership(identityMatches: true, lobbyHandleAvailable: true, localUserListed: true)
    == EosMembershipRecovery.RejoinRequired)
    throw new InvalidOperationException("A simulated 59-minute reauthentication for an already-member rider must restore the existing lobby without a duplicate JoinLobby call.");

var gate = new PeerSequenceGate();
if (!gate.TryAcceptSnapshot("peer-a", 1) || gate.TryAcceptSnapshot("peer-a", 1))
    throw new InvalidOperationException("Duplicate rider snapshots were not rejected.");
if (!gate.TryAcceptSnapshot("peer-a", 4))
    throw new InvalidOperationException("A fresh rider snapshot was rejected.");

// Unreliable channel 0 snapshots can overtake the reliable channel 1 reposition.
if (!gate.TryAcceptReposition("peer-a", 3) || gate.TryAcceptReposition("peer-a", 3))
    throw new InvalidOperationException("A delayed reposition or its duplicate was handled incorrectly.");
if (gate.TryAcceptSnapshot("peer-a", 3) || !gate.TryAcceptSnapshot("peer-a", 5))
    throw new InvalidOperationException("Snapshots did not respect the reposition sequence barrier.");

gate.Remove("peer-a");
if (!gate.TryAcceptSnapshot("peer-a", 1))
    throw new InvalidOperationException("Peer sequence state was not reset on departure.");

const string riderId = "peer-a";
const string routeId = "dunedin-one-way-oval-gardens";
const string fingerprint = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
var reposition = new RiderRepositionedMessage
{
    RiderId = riderId,
    RouteId = routeId,
    RouteFingerprint = fingerprint,
    DistanceMeters = 2130,
    SpeedKph = 35,
    CadenceRpm = 85,
    PowerWatts = 264,
    Sequence = 2
};
if (MultiplayerProtocol.ValidateReposition(reposition, riderId, routeId, fingerprint) is not null ||
    MultiplayerProtocol.ValidateReposition(reposition with { RouteFingerprint = new string('b', 64) }, riderId, routeId, fingerprint) is null ||
    MultiplayerProtocol.ValidateState(new RiderStateMessage { RiderId = riderId, DistanceMeters = double.NaN }, riderId) is null)
    throw new InvalidOperationException("EOS protocol payload compatibility or range validation failed.");

Console.WriteLine("PASS: multiplayer snapshot ordering and duplicate rejection");
Console.WriteLine("PASS: reliable reposition sequence barrier and peer departure cleanup");
Console.WriteLine("PASS: route fingerprint, rider identity, and telemetry range validation");
Console.WriteLine("PASS: EOS reauthentication preserves confirmed membership, rejoins only when absent, rejects identity changes, and bounds retries");
