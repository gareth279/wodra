using System;
using System.IO;

/// <summary>Validates an explicitly requested disposable Godot user-data root before bootstrap services start.</summary>
public static class UserDataIsolationGuard
{
	public const string RequiredRootArgument = "--require-userdata-under";

	public static bool TryReadRequiredRoot(string[] arguments, out string? requiredRoot, out string error)
	{
		requiredRoot = null;
		error = string.Empty;
		var found = -1;
		for (var i = 0; i < arguments.Length; i++)
		{
			if (!string.Equals(arguments[i], RequiredRootArgument, StringComparison.Ordinal)) continue;
			if (found >= 0)
			{
				error = $"{RequiredRootArgument} may only be specified once.";
				return false;
			}
			found = i;
		}

		if (found < 0) return true;
		if (found + 1 >= arguments.Length || string.IsNullOrWhiteSpace(arguments[found + 1]) ||
			arguments[found + 1].StartsWith("--", StringComparison.Ordinal))
		{
			error = $"{RequiredRootArgument} requires an absolute directory path.";
			return false;
		}

		try
		{
			if (!Path.IsPathFullyQualified(arguments[found + 1]))
			{
				error = $"{RequiredRootArgument} requires an absolute directory path.";
				return false;
			}
			requiredRoot = Path.GetFullPath(arguments[found + 1]);
			return true;
		}
		catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
		{
			error = $"{RequiredRootArgument} is not a valid directory path.";
			return false;
		}
	}

	public static bool IsInsideRequiredRoot(string actualUserDataDirectory, string requiredRoot)
	{
		if (string.IsNullOrWhiteSpace(actualUserDataDirectory) || string.IsNullOrWhiteSpace(requiredRoot)) return false;
		try
		{
			var actual = Path.TrimEndingDirectorySeparator(Path.GetFullPath(actualUserDataDirectory));
			var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(requiredRoot));
			if (string.Equals(actual, root, PathComparison)) return false;
			var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
			return actual.StartsWith(prefix, PathComparison);
		}
		catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
		{
			return false;
		}
	}

	private static StringComparison PathComparison => OperatingSystem.IsWindows()
		? StringComparison.OrdinalIgnoreCase
		: StringComparison.Ordinal;
}
