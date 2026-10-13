using Velopack;

var cases = new (string Name, string Installed, string Candidate, bool Expected)[]
{
    ("newer alpha is offered", "0.1.0-alpha.1", "0.1.0-alpha.2", true),
    ("same version is suppressed", "0.1.0-alpha.2", "0.1.0-alpha.2", false),
    ("older version is suppressed", "0.1.0-alpha.3", "0.1.0-alpha.2", false),
    ("stable release outranks alpha", "0.1.0-alpha.9", "0.1.0", true),
    ("higher prerelease ordinal is newer", "0.1.0-alpha.9", "0.1.0-alpha.10", true),
};

foreach (var test in cases)
{
    var actual = UpdateVersionPolicy.IsNewer(SemanticVersion.Parse(test.Installed), SemanticVersion.Parse(test.Candidate));
    if (actual != test.Expected)
        throw new InvalidOperationException($"{test.Name}: expected {test.Expected}, got {actual}.");
    Console.WriteLine($"PASS {test.Name}");
}

if (UpdateVersionPolicy.IsNewer(null, SemanticVersion.Parse("0.1.0-alpha.1")))
    throw new InvalidOperationException("An unavailable installed version must not trigger an update prompt.");
Console.WriteLine("PASS unavailable installed version is suppressed");

foreach (var hook in new[] { "--veloapp-install", "--veloapp-obsolete", "--veloapp-updated", "--veloapp-uninstall" })
    if (!VelopackLifecycleArguments.IsLifecycleArgument(hook))
        throw new InvalidOperationException($"Lifecycle argument '{hook}' was not recognized.");
Console.WriteLine("PASS all Velopack lifecycle arguments are recognized before scene startup");
if (VelopackLifecycleArguments.IsLifecycleArgument("--headless"))
    throw new InvalidOperationException("Ordinary Godot arguments must not be treated as Velopack lifecycle hooks.");
Console.WriteLine("PASS ordinary launch arguments remain ordinary");
