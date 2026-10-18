using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

public sealed record ExplorerNavigationResult(bool ExplorerVerified, bool FileSelected, bool FolderOpened, string Message);

/// <summary>Navigates Explorer with its structured Shell automation API and verifies the visible folder and selection.</summary>
public static class ExplorerNavigation
{
	public static string NormalizePath(string path)
	{
		if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A path is required.", nameof(path));
		return Path.GetFullPath(path.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar));
	}

	public static ExplorerNavigationResult OpenAndVerify(string filePath, TimeSpan? timeout = null)
	{
		if (!OperatingSystem.IsWindows()) return new(false, false, false, "Windows Explorer is not available on this platform.");
		string fullPath;
		try { fullPath = NormalizePath(filePath); }
		catch (Exception ex) { return new(false, false, false, $"The export path is invalid: {ex.Message}"); }
		if (!File.Exists(fullPath)) return new(false, false, false, "The exported ZIP could not be found at its saved path.");
		var folder = Path.GetDirectoryName(fullPath);
		if (string.IsNullOrWhiteSpace(folder)) return new(false, false, false, "The export folder could not be resolved.");

		var duration = timeout ?? TimeSpan.FromSeconds(5);
		if (NavigateAndVerify(folder, fullPath, duration))
			return new(true, true, true, "Explorer opened the export folder and selected the ZIP.");
		var opened = RequestExplorerFolder(folder);
		if (NavigateAndVerify(folder, fullPath, duration))
			return new(true, true, true, "Explorer opened the export folder and selected the ZIP.");
		if (NavigateAndVerify(folder, null, TimeSpan.FromMilliseconds(800)))
			return new(true, false, true, "Explorer opened the export folder. The ZIP was not selected; use Copy path to locate it.");
		return new(false, false, opened, opened
			? "Windows accepted the folder request, but Explorer could not confirm the displayed location. Use Copy path to locate the ZIP."
			: "Explorer could not open the export folder. Use Copy path to locate the ZIP.");
	}

	public static ExplorerNavigationResult OpenFolderAndVerify(string folderPath, TimeSpan? timeout = null)
	{
		if (!OperatingSystem.IsWindows()) return new(false, false, false, "Windows Explorer is not available on this platform.");
		string folder;
		try { folder = NormalizePath(folderPath); }
		catch (Exception ex) { return new(false, false, false, $"The folder path is invalid: {ex.Message}"); }
		if (!Directory.Exists(folder)) return new(false, false, false, "The export folder does not exist.");
		var duration = timeout ?? TimeSpan.FromSeconds(5);
		if (NavigateAndVerify(folder, null, duration)) return new(true, false, true, "Explorer opened the export folder.");
		var opened = RequestExplorerFolder(folder);
		var verified = NavigateAndVerify(folder, null, duration);
		return new(verified, false, verified || opened, verified
			? "Explorer opened the export folder."
			: opened ? "Windows accepted the folder request, but Explorer could not confirm the displayed location."
			: "Windows could not open the export folder.");
	}

	private static bool RequestExplorerFolder(string folder)
	{
		try
		{
			var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
			start.ArgumentList.Add(folder);
			return Process.Start(start) is not null;
		}
		catch { return false; }
	}

	private static bool NavigateAndVerify(string folder, string? selectedFile, TimeSpan timeout)
	{
		var until = DateTime.UtcNow + timeout;
		do
		{
			if (NavigateShellWindow(folder, selectedFile)) return true;
			Thread.Sleep(120);
		} while (DateTime.UtcNow < until);
		return false;
	}

	private static bool NavigateShellWindow(string folder, string? selectedFile)
	{
		object? shellObject = null;
		object? windowsObject = null;
		try
		{
			var shellType = Type.GetTypeFromProgID("Shell.Application");
			if (shellType is null) return false;
			shellObject = Activator.CreateInstance(shellType);
			dynamic shell = shellObject!;
			windowsObject = shell.Windows();
			dynamic windows = windowsObject!;
			var count = (int)windows.Count;
			for (var index = 0; index < count; index++)
			{
				object? windowObject = null;
				object? documentObject = null;
				object? namespaceObject = null;
				object? itemObject = null;
				object? selectedItemsObject = null;
				try
				{
					windowObject = windows.Item(index);
					if (windowObject is null) continue;
					dynamic window = windowObject;
					var fullName = (string?)window.FullName;
					if (string.IsNullOrWhiteSpace(fullName) || !fullName.EndsWith("explorer.exe", StringComparison.OrdinalIgnoreCase)) continue;
					if (!LocationMatches((string?)window.LocationURL, folder)) window.Navigate2(new Uri(folder).AbsoluteUri);
					if (!LocationMatches((string?)window.LocationURL, folder)) continue;
					if (selectedFile is null) return true;
					documentObject = window.Document;
					dynamic document = documentObject!;
					namespaceObject = shell.Namespace(folder);
					dynamic folderNamespace = namespaceObject!;
					itemObject = folderNamespace.ParseName(Path.GetFileName(selectedFile));
					if (itemObject is null) continue;
					document.SelectItem(itemObject, 1 | 4 | 8); // Select, deselect other items, and ensure visibility.
					selectedItemsObject = document.SelectedItems();
					dynamic selectedItems = selectedItemsObject!;
					for (var selectedIndex = 0; selectedIndex < (int)selectedItems.Count; selectedIndex++)
					{
						object? selectedObject = null;
						try
						{
							selectedObject = selectedItems.Item(selectedIndex);
							if (selectedObject is not null && PathEquals((string?)((dynamic)selectedObject).Path, selectedFile)) return true;
						}
						finally { ReleaseCom(selectedObject); }
					}
				}
				catch { }
				finally
				{
					ReleaseCom(selectedItemsObject); ReleaseCom(itemObject); ReleaseCom(namespaceObject);
					ReleaseCom(documentObject); ReleaseCom(windowObject);
				}
			}
		}
		catch { }
		finally { ReleaseCom(windowsObject); ReleaseCom(shellObject); }
		return false;
	}

	private static bool LocationMatches(string? location, string expectedFolder)
	{
		if (string.IsNullOrWhiteSpace(location)) return false;
		try
		{
			var local = Uri.TryCreate(location, UriKind.Absolute, out var uri) && uri.IsFile ? uri.LocalPath : location;
			return PathEquals(local, expectedFolder);
		}
		catch { return false; }
	}

	private static bool PathEquals(string? left, string right)
	{
		if (string.IsNullOrWhiteSpace(left)) return false;
		try { return string.Equals(NormalizePath(left), NormalizePath(right), StringComparison.OrdinalIgnoreCase); }
		catch { return false; }
	}

	private static void ReleaseCom(object? value)
	{
		if (OperatingSystem.IsWindows() && value is not null && Marshal.IsComObject(value))
			try { Marshal.FinalReleaseComObject(value); } catch { }
	}
}
