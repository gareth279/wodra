using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Linq;

/// <summary>Resolves the EOS native runtime next to Wodra's managed assembly.</summary>
public static class EosNativeLibraryLoader
{
	private const string NativeLibraryName = "EOSSDK-Win64-Shipping";
	private static readonly object Sync = new();
	private static IntPtr _libraryHandle;
	private static string? _resolvedPath;
	private static Exception? _loadFailure;
	private static bool _resolverRegistered;
	private static bool _firstImportLogged;

	public static bool LoadFailed
	{
		get { lock (Sync) return _loadFailure is not null; }
	}

	public static bool IsLoaded
	{
		get { lock (Sync) return _libraryHandle != IntPtr.Zero; }
	}

	public static string? ResolvedPath
	{
		get { lock (Sync) return _resolvedPath; }
	}

	public static void RegisterFor(Assembly assembly)
	{
		lock (Sync)
		{
			if (_resolverRegistered) return;
			NativeLibrary.SetDllImportResolver(assembly, ResolveImport);
			_resolverRegistered = true;
			GameLogger.Log("eos_native_resolver_registered", new
			{
				assembly = assembly.GetName().Name,
				assemblyPath = assembly.Location,
				processArchitecture = RuntimeInformation.ProcessArchitecture.ToString()
			});
		}
	}

	public static IntPtr EnsureLoaded()
	{
		lock (Sync)
		{
			if (_libraryHandle != IntPtr.Zero) return _libraryHandle;
			if (_loadFailure is not null)
				throw CreateUnavailableException(_loadFailure);

			var candidatePaths = CandidatePaths();
			foreach (var candidate in candidatePaths)
			{
				if (!File.Exists(candidate)) continue;
				try
				{
					_libraryHandle = NativeLibrary.Load(candidate);
					_resolvedPath = Path.GetFullPath(candidate);
					GameLogger.Log("eos_native_runtime_loaded", new
					{
						expectedDllExists = true,
						resolvedPath = _resolvedPath,
						processArchitecture = RuntimeInformation.ProcessArchitecture.ToString()
					});
					return _libraryHandle;
				}
				catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or IOException)
				{
					_loadFailure = ex;
					LogLoadFailure(candidatePaths, candidate, ex);
					throw CreateUnavailableException(ex);
				}
			}

			_loadFailure = new FileNotFoundException($"{NativeLibraryName}.dll was not found beside the managed game assembly or in the application directory.");
			LogLoadFailure(candidatePaths, expectedPath: candidatePaths.FirstOrDefault(), _loadFailure);
			throw CreateUnavailableException(_loadFailure);
		}
	}

	private static IntPtr ResolveImport(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
	{
		if (!string.Equals(Path.GetFileNameWithoutExtension(libraryName), NativeLibraryName, StringComparison.OrdinalIgnoreCase))
			return IntPtr.Zero;
		var handle = EnsureLoaded();
		lock (Sync)
		{
			if (!_firstImportLogged)
			{
				_firstImportLogged = true;
				GameLogger.Log("eos_native_import_resolved", new
				{
					requestedName = libraryName,
					assembly = assembly.GetName().Name,
					resolvedPath = _resolvedPath,
					processArchitecture = RuntimeInformation.ProcessArchitecture.ToString()
				});
			}
		}
		return handle;
	}

	private static string[] CandidatePaths()
	{
		var assemblyDirectory = Path.GetDirectoryName(typeof(EosNativeLibraryLoader).Assembly.Location);
		return new[]
			{
				string.IsNullOrWhiteSpace(assemblyDirectory) ? null : Path.Combine(assemblyDirectory, NativeLibraryName + ".dll"),
				Path.Combine(AppContext.BaseDirectory, NativeLibraryName + ".dll"),
				Path.Combine(Environment.CurrentDirectory, NativeLibraryName + ".dll")
			}
			.WherePath()
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToArray();
	}

	private static void LogLoadFailure(string[] candidates, string? expectedPath, Exception error)
	{
		var expectedDllExists = expectedPath is not null && File.Exists(expectedPath);
		GameLogger.Log("eos_native_runtime_load_failed", new
		{
			expectedDllExists,
			expectedPath,
			searchedPaths = candidates,
			processArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
			nativeDependencyFailure = expectedDllExists && error is DllNotFoundException,
			errorType = error.GetType().FullName,
			error = error.Message
		});
	}

	private static DllNotFoundException CreateUnavailableException(Exception inner) =>
		new("EOS runtime unavailable. See the Wodra log entry eos_native_runtime_load_failed for the searched path and native loader error.", inner);

	private static System.Collections.Generic.IEnumerable<string> WherePath(this string?[] paths) =>
		paths.Where(path => !string.IsNullOrWhiteSpace(path)).Select(path => Path.GetFullPath(path!));
}
