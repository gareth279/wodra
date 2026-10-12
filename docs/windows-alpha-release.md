# Windows x64 alpha release

## Prerequisites

Use the trusted Windows development workstation for releases. Install the matching Godot **4.6.2 .NET** editor/export templates at `C:\Tools\Godot\Godot_v4.6.2-stable_mono_win64`, .NET SDK 10.0.204 or a later compatible feature band, licensed Visual Studio Build Tools with the x64 VC runtime redist, Git, and the licensed EOS C# SDK 1.19.2.1 at `%LOCALAPPDATA%\Wodra\EOS-SDK` (or set `EOS_SDK_ROOT`). The publisher checks the native EOS DLL, Epic's `ThirdPartyNotices` file, and the Visual C++ runtime files before building. The EOS DLL imports MSVCP140 and VCRUNTIME140/140_1, so the publisher includes those app-local dependencies and the matching Microsoft redistribution notice. Microsoft allows redistribution of unmodified redist files subject to Visual Studio license terms ([Microsoft redistribution guide](https://learn.microsoft.com/en-us/cpp/windows/redistributing-visual-cpp-files)). The EOS SDK and its Samples are never copied into the release.

Create `%LOCALAPPDATA%\Wodra\eos-client.json` from `eos-client.example.json` once, using only the restricted, user-required **Wodra Player** client policy. The zero-argument release command loads this config by default; `-EosClientConfigPath` can override its path. Client credentials are extractable from a distributed application. Do not use a trusted-server credential, and do not include identity caches. See [EOS Phase 4 setup](epic-online-services-phase4.md).

## Build and verify an alpha ZIP

From the repository root:

```powershell
dotnet build wodra.sln -c Debug -m:1
dotnet build wodra.sln -c Release -m:1
dotnet run --project tests/IndoorCycling.Physics.Tests -c Release
dotnet run --project tests/Wodra.Multiplayer.Protocol.Tests -c Release

.\publish.ps1
```

The version is derived from the latest Git tag and commit distance. Dirty local trees get a `.local` suffix; use `-Version 'v0.1.0-alpha.1'` to choose an explicit artifact version. The final output defaults to `..\wodra_publish` (for this checkout, `C:\Users\garet\OneDrive\wodra_publish`). It contains `Wodra-windows-x64\` as a ready-to-run export plus the versioned ZIP and its `.sha256` file. `-PublishRoot` overrides that final output directory. Builds, ZIP creation and smoke extraction use a unique staging directory under `%TEMP%`; only verified artifacts are copied into the output folder. Existing successful artifacts are backed up during replacement and restored if installation fails. The script does not delete unrelated files from the output folder.

The publisher serializes runs with a process mutex. It launches dotnet, Godot, and the extracted smoke test with separately captured output and errors, phase-specific logs, and timeouts; on timeout it terminates only the child process tree started by that invocation. Godot 4.6.2's managed export hook fails in this environment and has also stalled after saving the PCK. To avoid that unstable hook, the script exports a fresh PCK, stages the matching official Windows x64 .NET release template as the executable, and performs a fresh self-contained `dotnet publish` for managed files. It does not trust `.godot` cached outputs: it verifies the PCK, release template, fresh managed files, package ZIP, and extracted executable startup. `-SkipOfflineSmokeTest` skips that launch when a runtime environment cannot support it.

The expected routes are Oval to Gardens, South Dunedin Coastal, Rolling City and Valley, and Signal Hill Climbing. Each release carries route points/elevation, buildings, landmarks, street furniture, streets, terrain and vegetation sidecars. Their scenery is baked data, so neither the original OSM PBF nor LINZ rasters are needed on a player machine. The route verifier recomputes each geometry fingerprint and verifies any fingerprint stored in its route file.

The archive's EOS settings file is included from the default per-user config or from the explicit `-EosClientConfigPath` override. Configuration must remain outside the Git checkout. Its secret values are never printed by the packaging or verification steps.

The archive contains the Windows export, Godot's bundled .NET runtime and managed assemblies, EOS native runtime and its app-local Microsoft CRT dependencies, client config, route packages, and third-party notices. It does not contain the EOS SDK source/Samples, debug symbols, PDBs, route-authoring inputs, identity caches, local settings, FIT files, logs, or private user profile data. Epic documents the SDK package's `Samples`, `SDK`, and `ThirdPartyNotices` directories; only the native runtime and required notice file are included in Wodra's distribution ([EOS SDK download contents](https://dev.epicgames.com/docs/epic-online-services/eos-get-started/eos-get-started-reference)).

## GitHub Releases

Do not publish from CI runners that lack an approved EOS SDK distribution. The public repository does not contain that proprietary SDK, and the publisher requires it locally. Use a trusted Windows workstation after reviewing the tag, diff, EOS Player policy and generated archive. Hosted GitHub Actions can run source-only tests, but cannot produce the complete EOS-enabled release unless Epic's SDK is provisioned through a separately approved, private mechanism. Do not use a self-hosted runner that executes pull-request code from untrusted forks.

Install and authenticate the standard [GitHub CLI](https://cli.github.com/), create and push a reviewed version tag, then create a **draft prerelease** by adding the explicit switch and tag to the build command:

```powershell
# Commit and review the release source before tagging it.
git tag v0.1.0-alpha.1
git push origin v0.1.0-alpha.1

.\publish.ps1 `
  -Version v0.1.0-alpha.1 `
  -CreateGitHubDraft `
  -GitHubTag v0.1.0-alpha.1
```

The GitHub option requires that the tag already exists locally and creates a draft prerelease with the verified ZIP and checksum attached. This task does not publish a GitHub release. Review and publish the draft in GitHub after confirming the alpha audience and release notes.

## Licensing and outstanding validation

`THIRD_PARTY_NOTICES.txt` and `ThirdPartyNotices/EOS/ThirdPartySoftwareNotice.txt` travel with the archive. OSM and LINZ-derived data attribution remains with the route bundles. The repository currently has no root `LICENSE`/`COPYING` file; select and add Wodra's intended source license before calling a public release open source. The Wodra license must not be applied to Epic's proprietary EOS DLL.

An extracted executable startup test and archive-content validation do not prove successful EOS authentication or multiplayer. Verify Online Riding and lobby creation using the alpha config on a separate Windows profile/device; each profile should generate its own EOS Device ID. Do not copy EOS identity caches. Current outstanding Phase 4 behavior: logs can show **EOS lobby joined · 2 riders · P2P connected** while the DEBUG view still says **Human Rider: waiting for state · visual none**, so the second cyclist and Catch Up offer are not yet reliable. This is a separate state-message flow issue and is not addressed by release packaging. Two-client rendering remains a release acceptance check.
