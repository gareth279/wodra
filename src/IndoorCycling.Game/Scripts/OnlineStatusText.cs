/// <summary>Short, user-facing multiplayer availability text for startup controls.</summary>
public static class OnlineStatusText
{
	public static bool UsesEos(string? provider) =>
		string.Equals(provider, "EpicOnlineServices", System.StringComparison.OrdinalIgnoreCase) ||
		string.Equals(provider, "EOS", System.StringComparison.OrdinalIgnoreCase);

	public static string ForStartup(bool enabled, bool connected, bool? available,
		bool eosConfigured, bool usingEos, bool eosRuntimeUnavailable = false,
		bool eosConfigurationInvalid = false)
	{
		if (!enabled) return "Online Riding: Off";
		if (connected) return "Online";
		if (usingEos && eosConfigurationInvalid) return "EOS configuration invalid";
		if (usingEos && !eosConfigured) return "EOS not configured";
		if (available == true) return "Online ready";
		if (available == false)
		{
			if (usingEos && eosRuntimeUnavailable) return "EOS runtime unavailable";
			return "Online unavailable";
		}
		return usingEos ? "Connecting to EOS…" : "Checking online service…";
	}
}
