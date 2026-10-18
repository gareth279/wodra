using System;
using System.IO;
using System.Reflection;

/// <summary>Resolves built-in route resources first, then optional per-user route-package files.</summary>
public static class RoutePackageFiles
{
	public static string UserPackagesDirectory => Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Wodra", "Routes");

	/// <summary>Portable route packages shipped next to the Windows application.</summary>
	public static string PackagedPackagesDirectory
	{
		get
		{
			var overridePath = Environment.GetEnvironmentVariable("WODRA_PACKAGED_ROUTES_DIR");
			return string.IsNullOrWhiteSpace(overridePath)
				? ResolvePackagedPackagesDirectory(Environment.ProcessPath, AppContext.BaseDirectory)
				: Path.GetFullPath(overridePath);
		}
	}

	public static string ResolvePackagedPackagesDirectory(string? processPath, string? applicationBaseDirectory)
	{
		var processDirectory = string.IsNullOrWhiteSpace(processPath) ? null : Path.GetDirectoryName(processPath);
		var root = string.IsNullOrWhiteSpace(processDirectory) ? applicationBaseDirectory : processDirectory;
		return Path.Combine(string.IsNullOrWhiteSpace(root) ? AppContext.BaseDirectory : root, "Routes");
	}

	public static string[] PackageDirectories => [PackagedPackagesDirectory, UserPackagesDirectory];

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
		foreach (var packageDirectory in PackageDirectories)
		{
			var candidate = Path.Combine(packageDirectory, routeId, filename);
			if (File.Exists(candidate)) return File.OpenRead(candidate);
		}
		return null;
	}

	public static bool Exists(Assembly assembly, string resourceName, string routeId)
	{
		using var stream = Open(assembly, resourceName, routeId);
		return stream is not null;
	}
}
