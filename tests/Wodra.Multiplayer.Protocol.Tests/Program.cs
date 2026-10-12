using Wodra.Multiplayer.Protocol;

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
