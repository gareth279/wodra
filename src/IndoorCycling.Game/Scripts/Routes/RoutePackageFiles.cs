using System;
using System.IO;
using System.Reflection;

/// <summary>Resolves built-in route resources first, then optional per-user route-package files.</summary>
public static class RoutePackageFiles
{
	public static string UserPackagesDirectory => Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Wodra", "Routes");

	public static Stream? Open(Assembly assembly, string resourceName, string routeId)
	{
		var embedded = assembly.GetManifestResourceStream(resourceName);
		if (embedded is not null) return embedded;
		if (string.IsNullOrWhiteSpace(routeId) || Path.GetFileName(routeId) != routeId
			|| routeId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
			return null;
		var marker = ".Routes.";
		var markerIndex = resourceName.IndexOf(marker, StringComparison.Ordinal);
		if (markerIndex < 0) return null;
		var filename = resourceName[(markerIndex + marker.Length)..];
		var candidate = Path.Combine(UserPackagesDirectory, routeId, filename);
		return File.Exists(candidate) ? File.OpenRead(candidate) : null;
	}

	public static bool Exists(Assembly assembly, string resourceName, string routeId)
	{
		using var stream = Open(assembly, resourceName, routeId);
		return stream is not null;
	}
}
