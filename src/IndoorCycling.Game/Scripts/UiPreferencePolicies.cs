/// <summary>Device types offered by the existing optional controller adapter.</summary>
public enum ControllerDeviceTypePreference
{
	AutoDetect = 0,
	EliteSterzoSmart = 1
}

/// <summary>Determines where Settings returns without changing ride state.</summary>
public static class SettingsReturnPolicy
{
	public static bool ShouldReturnToConnection(bool enteredFromConnectionScreen, bool rideActive) =>
		enteredFromConnectionScreen && !rideActive;
}
