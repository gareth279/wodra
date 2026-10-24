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

const string sourceCommit = "64185339fbe9a14b13535ae629e4f9fb4c79d3c5";
var developmentIdentity = BuildIdentityPolicy.Resolve(
	null, null, $"1.0.0+{sourceCommit}", "Debug", "alpha");
if (developmentIdentity.ApplicationVersion != "Development (version unavailable)" ||
	developmentIdentity.BuildType != "Development build (unpackaged)" ||
	developmentIdentity.UpdateChannel != "alpha (configured feed; package channel unavailable)" ||
	developmentIdentity.Configuration != "Debug" || developmentIdentity.SourceCommit != sourceCommit)
	throw new InvalidOperationException("Development build identity must use source metadata and must not look like a published version.");
Console.WriteLine("PASS development build identity is not presented as a published version");

var packagedIdentity = BuildIdentityPolicy.Resolve(
	"0.1.0-alpha.64", "alpha", $"0.1.0-alpha.64+{sourceCommit}", "Release", "alpha");
if (packagedIdentity.ApplicationVersion != "0.1.0-alpha.64" ||
	packagedIdentity.BuildType != "Packaged release" || packagedIdentity.UpdateChannel != "alpha" ||
	packagedIdentity.Configuration != "Release" || packagedIdentity.SourceCommit != sourceCommit)
	throw new InvalidOperationException("Packaged build identity must match the installed package and source revision.");
Console.WriteLine("PASS packaged identity uses the installed package version and channel");

var portableIdentity = BuildIdentityPolicy.Resolve(
	null, "alpha", $"0.1.0-alpha.64+{sourceCommit}", "Release", "alpha", isPortablePackage: true);
if (portableIdentity.ApplicationVersion != "0.1.0-alpha.64" ||
	portableIdentity.BuildType != "Packaged portable build" || portableIdentity.UpdateChannel != "alpha")
	throw new InvalidOperationException("Portable package identity must use its packaged version and channel metadata.");
Console.WriteLine("PASS portable package identity uses embedded package metadata");

var unavailableIdentity = BuildIdentityPolicy.Resolve(null, null, null, null, "alpha");
if (unavailableIdentity.SourceCommit != "Unavailable" || unavailableIdentity.Configuration != "Unavailable")
	throw new InvalidOperationException("Missing source and configuration metadata must be clearly unavailable.");
var copyText = packagedIdentity.ToPlainText();
if (!copyText.Contains(sourceCommit, StringComparison.Ordinal) ||
	copyText.Contains("C:\\", StringComparison.OrdinalIgnoreCase) ||
	copyText.Contains("AppData", StringComparison.OrdinalIgnoreCase) ||
	copyText.Contains("profile", StringComparison.OrdinalIgnoreCase))
	throw new InvalidOperationException("Copied build information must include the source and exclude local paths and profile details.");
Console.WriteLine("PASS copied build information is compact and excludes local paths and profile details");
