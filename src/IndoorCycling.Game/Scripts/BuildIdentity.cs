using System;
using System.Linq;
using System.Reflection;
using Velopack.Locators;

/// <summary>Version, channel and source identity shared by About, updates and diagnostics.</summary>
public sealed record BuildIdentityDetails(
	string ApplicationVersion,
	string UpdateChannel,
	string BuildType,
	string Configuration,
	string SourceCommit)
{
	public string ToPlainText() =>
		$"Application version: {ApplicationVersion}\n" +
		$"Update channel: {UpdateChannel}\n" +
		$"Build: {BuildType}\n" +
		$"Configuration: {Configuration}\n" +
		$"Source commit: {SourceCommit}";
}

public static class BuildIdentity
{
	/// <summary>The existing update feed used by startup update checks.</summary>
	public const string UpdateChannelForChecks = "alpha";

	public static BuildIdentityDetails Current => ResolveCurrent();

	private static BuildIdentityDetails ResolveCurrent()
	{
		string? installedVersion = null;
		string? installedChannel = null;
		var isPortablePackage = false;
		try
		{
			if (VelopackLocator.IsCurrentSet)
			{
				var locator = VelopackLocator.Current;
				installedVersion = locator.CurrentlyInstalledVersion?.ToString();
				installedChannel = locator.Channel;
				isPortablePackage = locator.IsPortable;
			}
		}
		catch
		{
			// About and diagnostics remain available if Velopack metadata cannot be read.
		}

		var assembly = Assembly.GetExecutingAssembly();
		var informationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
		var configuration = assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration;
		return BuildIdentityPolicy.Resolve(
			installedVersion,
			installedChannel,
			informationalVersion,
			configuration,
			UpdateChannelForChecks,
			isPortablePackage);
	}
}

/// <summary>Pure formatting policy so development and packaged identities can be regression-tested.</summary>
public static class BuildIdentityPolicy
{
	public static BuildIdentityDetails Resolve(
		string? installedVersion,
		string? installedChannel,
		string? informationalVersion,
		string? configuration,
		string configuredUpdateChannel,
		bool isPortablePackage = false)
	{
		var hasInstalledVersion = !string.IsNullOrWhiteSpace(installedVersion);
		var packaged = hasInstalledVersion || isPortablePackage;
		var version = hasInstalledVersion
			? installedVersion!.Trim()
			: isPortablePackage ? GetPackageVersion(informationalVersion) : "Development (version unavailable)";
		var channel = packaged && !string.IsNullOrWhiteSpace(installedChannel)
			? installedChannel!.Trim()
			: packaged ? "Unavailable" : $"{configuredUpdateChannel} (configured feed; package channel unavailable)";
		var buildType = packaged
			? isPortablePackage ? "Packaged portable build" : "Packaged release"
			: "Development build (unpackaged)";
		var normalizedConfiguration = string.IsNullOrWhiteSpace(configuration) ? "Unavailable" : configuration.Trim();
		var sourceCommit = GetSourceCommit(informationalVersion);
		return new BuildIdentityDetails(version, channel, buildType, normalizedConfiguration, sourceCommit);
	}

	private static string GetPackageVersion(string? informationalVersion)
	{
		if (string.IsNullOrWhiteSpace(informationalVersion)) return "Unavailable (package version unavailable)";
		var metadataSeparator = informationalVersion.IndexOf('+');
		var version = metadataSeparator >= 0 ? informationalVersion[..metadataSeparator] : informationalVersion;
		return string.IsNullOrWhiteSpace(version) ? "Unavailable (package version unavailable)" : version;
	}

	private static string GetSourceCommit(string? informationalVersion)
	{
		if (string.IsNullOrWhiteSpace(informationalVersion)) return "Unavailable";
		var separator = informationalVersion.LastIndexOf('+');
		if (separator < 0 || separator == informationalVersion.Length - 1) return "Unavailable";
		var metadata = informationalVersion[(separator + 1)..];
		return metadata.Length is >= 7 and <= 40 && metadata.All(IsHexDigit)
			? metadata.ToLowerInvariant()
			: "Unavailable";
	}

	private static bool IsHexDigit(char value) =>
		value is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';
}
