/// <summary>Readable lobby and P2P state for the in-ride multiplayer indicator.</summary>
public static class EosLobbyStatusText
{
	public static string ForMembership(int riderCount, int connectedPeerCount, int peersWithFreshState, bool p2pFailed)
	{
		riderCount = System.Math.Max(1, riderCount);
		connectedPeerCount = System.Math.Clamp(connectedPeerCount, 0, riderCount - 1);
		peersWithFreshState = System.Math.Clamp(peersWithFreshState, 0, connectedPeerCount);
		if (riderCount == 1) return "EOS lobby joined · 1 rider";
		if (p2pFailed) return $"EOS lobby joined · {riderCount} riders · P2P failed";
		if (peersWithFreshState == riderCount - 1) return $"EOS lobby joined · {riderCount} riders · rider-state exchange active";
		if (connectedPeerCount == riderCount - 1) return $"EOS lobby joined · {riderCount} riders · waiting for rider state";
		return connectedPeerCount == 0
			? $"EOS lobby joined · {riderCount} riders · peers discovered"
			: $"EOS lobby joined · {riderCount} riders · P2P pending ({connectedPeerCount}/{riderCount - 1})";
	}
}
