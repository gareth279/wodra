using System;
using System.Collections.Generic;

/// <summary>
/// EOS lobby and P2P callbacks may arrive in either order. This gate remembers
/// an inbound request until lobby membership confirms the peer.
/// </summary>
public sealed class EosPeerMembershipGate
{
	private readonly object _lock = new();
	private readonly HashSet<string> _members = new(StringComparer.Ordinal);
	private readonly HashSet<string> _requestsAwaitingMembership = new(StringComparer.Ordinal);

	public bool OnIncomingRequest(string peerId)
	{
		if (string.IsNullOrWhiteSpace(peerId)) return false;
		lock (_lock)
		{
			if (_members.Contains(peerId)) return true;
			_requestsAwaitingMembership.Add(peerId);
			return false;
		}
	}

	/// <returns>True when an inbound request was pending and can now be accepted.</returns>
	public bool AddMember(string peerId)
	{
		if (string.IsNullOrWhiteSpace(peerId)) return false;
		lock (_lock)
		{
			_members.Add(peerId);
			return _requestsAwaitingMembership.Remove(peerId);
		}
	}

	public void RemoveMember(string peerId)
	{
		lock (_lock)
		{
			_members.Remove(peerId);
			_requestsAwaitingMembership.Remove(peerId);
		}
	}

	public void Clear()
	{
		lock (_lock)
		{
			_members.Clear();
			_requestsAwaitingMembership.Clear();
		}
	}
}
