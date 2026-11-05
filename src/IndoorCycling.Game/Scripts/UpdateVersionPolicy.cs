using Velopack;
using System;

public static class UpdateChannelPolicy
{
	public const string Alpha = "alpha";
	public const string Stable = "stable";

	public static string Normalize(string? value) =>
		string.Equals(value?.Trim(), Stable, StringComparison.OrdinalIgnoreCase) ? Stable : Alpha;

	public static bool IsValid(string? value) =>
		string.Equals(value?.Trim(), Alpha, StringComparison.OrdinalIgnoreCase) ||
		string.Equals(value?.Trim(), Stable, StringComparison.OrdinalIgnoreCase);

	public static string InitialForNewInstallation(string? installerChannel) =>
		IsValid(installerChannel) ? Normalize(installerChannel) : Alpha;

	public static string ResolvePersistedValue(string? savedValue, bool hasExistingSettings, string? installerChannel,
		bool hasExplicitSetting = true) =>
		hasExistingSettings && hasExplicitSetting ? Normalize(savedValue) : InitialForNewInstallation(installerChannel);

	public static bool IsStagedUpdateForChannel(string? stagedChannel, string? installerChannel, string selectedChannel) =>
		stagedChannel is null
			? string.Equals(Normalize(installerChannel), Normalize(selectedChannel), StringComparison.OrdinalIgnoreCase)
			: IsValid(stagedChannel) && string.Equals(Normalize(stagedChannel), Normalize(selectedChannel), StringComparison.OrdinalIgnoreCase);

	public static bool IsNewerThan(string installed, string candidate) =>
		SemanticVersion.Parse(candidate).CompareTo(SemanticVersion.Parse(installed)) > 0;
}

/// <summary>Defines the strict version rule used before Wodra presents an update offer.</summary>
public static class UpdateVersionPolicy
{
	public static bool IsNewer(SemanticVersion? installed, SemanticVersion candidate) =>
		installed is not null && candidate.CompareTo(installed) > 0;

	public static string? ExplainNoUpdate(string installed, string channel, SemanticVersion? candidate)
	{
		if (!string.Equals(channel, UpdateChannelPolicy.Stable, StringComparison.OrdinalIgnoreCase) ||
			!installed.Contains("-alpha.", StringComparison.OrdinalIgnoreCase)) return null;
		return candidate is null || candidate.CompareTo(SemanticVersion.Parse(installed)) < 0
			? "You’re running a newer Alpha version. There isn’t a newer Stable release available yet. Your current version will remain installed."
			: null;
	}
}
