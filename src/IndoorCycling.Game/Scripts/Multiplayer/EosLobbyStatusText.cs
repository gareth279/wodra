/// <summary>Readable lobby and P2P state for the in-ride multiplayer indicator.</summary>
public static class EosLobbyStatusText
{
	public static string ForMembership(int riderCount, int connectedPeerCount, bool p2pFailed)
	{
		riderCount = System.Math.Max(1, riderCount);
		connectedPeerCount = System.Math.Clamp(connectedPeerCount, 0, riderCount - 1);
		if (riderCount == 1) return "EOS lobby joined · 1 rider";
		if (p2pFailed) return $"EOS lobby joined · {riderCount} riders · P2P failed";
		if (connectedPeerCount == riderCount - 1) return $"EOS lobby joined · {riderCount} riders · P2P connected";
		return connectedPeerCount == 0
			? $"EOS lobby joined · {riderCount} riders · P2P pending"
			: $"EOS lobby joined · {riderCount} riders · P2P pending ({connectedPeerCount}/{riderCount - 1})";
	}
}
