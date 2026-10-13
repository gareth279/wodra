using Velopack;

/// <summary>Defines the strict version rule used before Wodra presents an update offer.</summary>
public static class UpdateVersionPolicy
{
	public static bool IsNewer(SemanticVersion? installed, SemanticVersion candidate) =>
		installed is not null && candidate.CompareTo(installed) > 0;
}
