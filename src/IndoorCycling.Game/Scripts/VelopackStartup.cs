using System;
using System.Linq;
using System.Runtime.CompilerServices;
using Velopack;

/// <summary>
/// Godot owns the executable entry point. A module initializer runs as soon as
/// the game assembly is loaded, before Godot instantiates the main scene, so
/// Velopack lifecycle launches cannot construct the game UI or start services.
/// </summary>
internal static class VelopackStartup
{
	#pragma warning disable CA2255 // Required: Velopack must run before Godot scene startup.
	[ModuleInitializer]
	internal static void Initialize()
	{
		var args = Environment.GetCommandLineArgs().Skip(1).ToArray();
		var lifecycleHook = args.FirstOrDefault(VelopackLifecycleArguments.IsLifecycleArgument);
		try
		{
			VelopackApp.Build()
				.SetArgs(args)
				.SetAutoApplyOnStartup(false)
				.Run();

			// Run is expected to terminate for these hooks. Retain this guard so a
			// future Velopack change cannot fall through into ordinary game startup.
			if (lifecycleHook is not null)
				Environment.Exit(0);
		}
		catch (Exception ex)
		{
			Console.Error.WriteLine($"Wodra Velopack startup hook failed ({lifecycleHook ?? "normal launch"}): {ex.Message}");
			if (lifecycleHook is not null)
				Environment.Exit(1);
			// Update integration failure must not prevent offline gameplay.
		}
	}
	#pragma warning restore CA2255
}

public static class VelopackLifecycleArguments
{
	public static bool IsLifecycleArgument(string argument) => argument is
		"--veloapp-install" or "--veloapp-obsolete" or "--veloapp-updated" or "--veloapp-uninstall";
}
