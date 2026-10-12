namespace Wodra.Multiplayer.Protocol;

/// <summary>
/// Tracks snapshot and reliable reposition sequence streams separately while
/// preventing a delayed snapshot from undoing an already received reposition.
/// </summary>
public sealed class PeerSequenceGate
{
    private readonly Dictionary<string, long> _lastSnapshots = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _lastRepositions = new(StringComparer.Ordinal);

    public bool TryAcceptSnapshot(string riderId, long sequence)
    {
        if (string.IsNullOrEmpty(riderId) || sequence < 0) return false;
        var reposition = _lastRepositions.GetValueOrDefault(riderId, -1);
        if (sequence <= Math.Max(reposition, _lastSnapshots.GetValueOrDefault(riderId, -1))) return false;
        _lastSnapshots[riderId] = sequence;
        return true;
    }

    public bool TryAcceptReposition(string riderId, long sequence)
    {
        if (string.IsNullOrEmpty(riderId) || sequence < 0 || sequence <= _lastRepositions.GetValueOrDefault(riderId, -1))
            return false;
        _lastRepositions[riderId] = sequence;
        return true;
    }

    public void Remove(string riderId)
    {
        _lastSnapshots.Remove(riderId);
        _lastRepositions.Remove(riderId);
    }

    public void Clear()
    {
        _lastSnapshots.Clear();
        _lastRepositions.Clear();
    }
}
