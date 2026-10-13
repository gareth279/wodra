param(
    [ValidateSet('Release')]
    [string]$Configuration = 'Release',
    [string]$Version,
    [string]$PublishRoot,
    [string]$VisualStudioRoot,
    [string]$EosClientConfigPath,
    [switch]$SkipOfflineSmokeTest,
    [switch]$CreateGitHubDraft,
    [string]$GitHubTag
)

$ErrorActionPreference = 'Stop'
$RepoDir = [System.IO.Path]::GetFullPath($PSScriptRoot)
$GodotProjectDir = Join-Path $RepoDir 'src\IndoorCycling.Game'
$Solution = Join-Path $RepoDir 'wodra.sln'
$Godot = 'C:\Tools\Godot\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64_console.exe'
$EosSdkRoot = if ([string]::IsNullOrWhiteSpace($env:EOS_SDK_ROOT)) {
    Join-Path $env:LOCALAPPDATA 'Wodra\EOS-SDK'
} else { [System.IO.Path]::GetFullPath($env:EOS_SDK_ROOT) }
$EosSdkRuntime = Join-Path $EosSdkRoot 'SDK\Bin\EOSSDK-Win64-Shipping.dll'
$EosSdkNotices = Join-Path $EosSdkRoot 'ThirdPartyNotices\ThirdPartySoftwareNotice.txt'
$vcRuntimeNames = @('MSVCP140.dll', 'VCRUNTIME140.dll', 'VCRUNTIME140_1.dll')
$ConfigPath = [string]$EosClientConfigPath
$mutex = [System.Threading.Mutex]::new($false, 'Local\WodraWindowsReleasePublisher')
$hasMutex = $false
$phaseClock = [System.Diagnostics.Stopwatch]::StartNew()
$phaseStart = [System.Diagnostics.Stopwatch]::StartNew()
$packageRoot = Join-Path ([System.IO.Path]::GetTempPath()) 'WodraReleasePipeline'
$runId = [guid]::NewGuid().ToString('N')
$workRoot = Join-Path $packageRoot "run-$runId"
$incomingArtifacts = @()
New-Item -ItemType Directory -Path $workRoot -Force | Out-Null

function Write-Phase([int]$Number, [string]$Name) {
    $script:phaseStart = [System.Diagnostics.Stopwatch]::StartNew()
    Write-Host "`n[$Number/7] $Name"
}

function Complete-Phase {
    Write-Host ("    completed in {0:n1}s" -f $script:phaseStart.Elapsed.TotalSeconds)
}

function Invoke-TrackedProcess {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(Mandatory = $true)][string[]]$ArgumentList,
        [Parameter(Mandatory = $true)][string]$WorkingDirectory,
        [Parameter(Mandatory = $true)][string]$LogPath,
        [Parameter(Mandatory = $true)][int]$TimeoutSeconds,
        [Parameter(Mandatory = $true)][string]$Label,
        [string[]]$RemoveEnvironmentVariables = @(),
        [string]$StopAfterOutputPattern,
        [int]$StopAfterOutputGraceSeconds = 5
    )
    $quoteArgument = {
        param([string]$Value)
        if ($Value.Length -gt 0 -and $Value -notmatch '[\s"]') { return $Value }
        $builder = [System.Text.StringBuilder]::new()
        [void]$builder.Append('"')
        $slashes = 0
        foreach ($character in $Value.ToCharArray()) {
            if ($character -eq '\') { $slashes++; continue }
            if ($character -eq '"') {
                [void]$builder.Append(('\' * (($slashes * 2) + 1))).Append('"')
                $slashes = 0
                continue
            }
            if ($slashes -gt 0) { [void]$builder.Append(('\' * $slashes)); $slashes = 0 }
            [void]$builder.Append($character)
        }
        if ($slashes -gt 0) { [void]$builder.Append(('\' * ($slashes * 2))) }
        [void]$builder.Append('"')
        return $builder.ToString()
    }
    $encodedArguments = (@($ArgumentList | ForEach-Object { & $quoteArgument $_ }) -join ' ')
    $stdoutPath = "$LogPath.stdout"
    $stderrPath = "$LogPath.stderr"
    foreach ($path in @($stdoutPath, $stderrPath, $LogPath)) {
        if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Force }
    }
    $environmentPrefix = if ($RemoveEnvironmentVariables.Count -gt 0) {
        'Environment overrides removed: ' + ($RemoveEnvironmentVariables -join ', ') + [Environment]::NewLine
    } else { '' }
    Set-Content -LiteralPath $LogPath -Value "Starting $Label`n$environmentPrefix" -NoNewline
    $child = $null
    $timedOut = $false
    $stoppedAfterCompleteOutput = $false
    $completeOutputSeenAt = $null
    try {
        $child = Start-Process -FilePath $FilePath -ArgumentList $encodedArguments -WorkingDirectory $WorkingDirectory `
            -RedirectStandardOutput $stdoutPath -RedirectStandardError $stderrPath -PassThru -WindowStyle Hidden -ErrorAction Stop
        $timer = [System.Diagnostics.Stopwatch]::StartNew()
        while (-not $child.WaitForExit(1000)) {
            if (-not [string]::IsNullOrWhiteSpace($StopAfterOutputPattern) -and
                (Test-Path -LiteralPath $stdoutPath -PathType Leaf)) {
                $currentOutput = Get-Content -LiteralPath $stdoutPath -Raw -ErrorAction SilentlyContinue
                $plainOutput = [regex]::Replace([string]$currentOutput, "`e\[[0-?]*[ -/]*[@-~]", '')
                if ($plainOutput -match $StopAfterOutputPattern) {
                    if ($null -eq $completeOutputSeenAt) { $completeOutputSeenAt = $timer.Elapsed }
                    elseif (($timer.Elapsed - $completeOutputSeenAt).TotalSeconds -ge $StopAfterOutputGraceSeconds) {
                        Write-Warning "$Label wrote its complete-output marker but remained alive; stopping the owned process tree."
                        try { $child.Kill($true); [void]$child.WaitForExit(15000) } catch { }
                        $stoppedAfterCompleteOutput = $true
                        break
                    }
                }
            }
            if ($timer.Elapsed.TotalSeconds -ge $TimeoutSeconds) {
                $timedOut = $true
                Write-Warning "$Label is still running after $TimeoutSeconds seconds; stopping its owned process tree."
                try { $child.Kill($true); [void]$child.WaitForExit(15000) } catch { }
                break
            }
            if ([math]::Floor($timer.Elapsed.TotalSeconds) -gt 0 -and
                ($timer.Elapsed.TotalSeconds % 15) -lt 1) {
                Write-Host "    $Label still running ($([int]$timer.Elapsed.TotalSeconds)s); log: $LogPath"
            }
        }
        if (-not $child.HasExited) {
            throw "$Label timed out after $TimeoutSeconds seconds. Its child process tree was terminated. Log: $LogPath"
        }
        $stdoutText = if (Test-Path -LiteralPath $stdoutPath) { Get-Content -LiteralPath $stdoutPath -Raw } else { '' }
        $stderrText = if (Test-Path -LiteralPath $stderrPath) { Get-Content -LiteralPath $stderrPath -Raw } else { '' }
        $completion = if ($timedOut) { "TIMEOUT after $TimeoutSeconds seconds; exit code: $($child.ExitCode)" }
            elseif ($stoppedAfterCompleteOutput) { "Stopped after verified output-complete marker; exit code: $($child.ExitCode)" }
            else { "Exit code: $($child.ExitCode)" }
        Add-Content -LiteralPath $LogPath -Value "`n$completion`n--- stdout ---`n$stdoutText`n--- stderr ---`n$stderrText"
        if ($timedOut) {
            Get-Content -LiteralPath $LogPath -Tail 80
            throw "$Label timed out after $TimeoutSeconds seconds. Its child process tree was terminated. Log: $LogPath"
        }
        if ($child.ExitCode -ne 0 -and -not $stoppedAfterCompleteOutput) {
            Get-Content -LiteralPath $LogPath -Tail 80
            throw "$Label failed with exit code $($child.ExitCode). Log: $LogPath"
        }
        $reportedExitCode = if ($stoppedAfterCompleteOutput) { 0 } else { $child.ExitCode }
        return [pscustomobject]@{ ExitCode = $reportedExitCode; StdOut = $stdoutText; StdErr = $stderrText;
            StoppedAfterOutputMarker = $stoppedAfterCompleteOutput }
    }
    finally {
        if ($null -ne $child) { $child.Dispose() }
    }
}

function Copy-WithRetry([string]$Source, [string]$Destination) {
    for ($attempt = 1; $attempt -le 4; $attempt++) {
        try { Copy-Item -LiteralPath $Source -Destination $Destination -Recurse -Force -ErrorAction Stop; return }
        catch {
            if ($attempt -eq 4) {
                $cause = $_.Exception.Message
                throw "Copy failed after $attempt attempts: '$Source' -> '$Destination'. $cause"
            }
            Start-Sleep -Milliseconds (500 * $attempt)
        }
    }
}

try {
    try { $hasMutex = $mutex.WaitOne(0) }
    catch [System.Threading.AbandonedMutexException] { $hasMutex = $true }
    if (-not $hasMutex) { throw 'Another Wodra release packaging process is already running.' }

    Write-Host 'Wodra Windows Release'
    Write-Phase 1 'Validating prerequisites...'

    if ([string]::IsNullOrWhiteSpace($Version)) {
        $gitExe = (Get-Command git -ErrorAction Stop).Source
        $versionResult = Invoke-TrackedProcess -FilePath $gitExe `
            -ArgumentList @('-C', $RepoDir, 'describe', '--tags', '--long', '--dirty') `
            -WorkingDirectory $RepoDir -LogPath (Join-Path $workRoot 'git-version.log') `
            -TimeoutSeconds 30 -Label 'Git version detection'
        $description = ($versionResult.StdOut -split "`r?`n" | Select-Object -First 1).Trim()
        if ([string]::IsNullOrWhiteSpace($description)) {
            throw 'Cannot derive a release version from Git. Supply -Version explicitly.'
        }
        if ($description -match '^(.+)-(\d+)-g([0-9a-f]+)(-dirty)?$') {
            $localSuffix = if ($Matches[4]) { '.local' } else { '' }
            $Version = "$($Matches[1])-alpha.$($Matches[2])+g$($Matches[3])$localSuffix"
        } else {
            $Version = $description
        }
    }
    if ($Version -notmatch '^[A-Za-z0-9][A-Za-z0-9._+-]*$') {
        throw 'Version may contain only letters, digits, dot, underscore, plus, and hyphen.'
    }
    if ([string]::IsNullOrWhiteSpace($ConfigPath)) {
        if ([string]::IsNullOrWhiteSpace($env:LOCALAPPDATA)) { throw 'LOCALAPPDATA is unavailable; supply -EosClientConfigPath.' }
        $ConfigPath = Join-Path $env:LOCALAPPDATA 'Wodra\eos-client.json'
    }
    $ConfigPath = [System.IO.Path]::GetFullPath($ConfigPath)
    $repoPrefix = $RepoDir.TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
    if ($ConfigPath.StartsWith($repoPrefix, [System.StringComparison]::OrdinalIgnoreCase) -or
        $ConfigPath.Equals($RepoDir, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw 'EOS client config source must be outside the Git checkout.'
    }
    if (-not (Test-Path -LiteralPath $ConfigPath -PathType Leaf)) {
        throw "EOS client config source does not exist: $ConfigPath"
    }
    try { $clientConfig = Get-Content -LiteralPath $ConfigPath -Raw | ConvertFrom-Json -ErrorAction Stop }
    catch { throw 'EOS client config source is not valid JSON.' }
    $approvedFields = @('ProductId', 'SandboxId', 'DeploymentId', 'ClientId', 'ClientSecret')
    $missing = @($approvedFields | Where-Object {
        $null -eq $clientConfig.PSObject.Properties[$_] -or [string]::IsNullOrWhiteSpace([string]$clientConfig.$_)
    })
    $unexpected = @($clientConfig.PSObject.Properties.Name | Where-Object { $_ -notin $approvedFields })
    if ($missing.Count -gt 0) { throw "EOS client config is missing required fields: $($missing -join ', ')" }
    if ($unexpected.Count -gt 0) { throw 'EOS client config contains fields outside the approved five client fields.' }
    Remove-Variable clientConfig

    foreach ($path in @($Solution, (Join-Path $GodotProjectDir 'project.godot'), $Godot,
            $EosSdkRuntime, $EosSdkNotices, (Join-Path $RepoDir 'THIRD_PARTY_NOTICES.txt'),
            (Join-Path $RepoDir 'tools\Verify-WodraRelease.ps1'))) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Required release prerequisite is missing: $path" }
    }

    $godotVersionResult = Invoke-TrackedProcess -FilePath $Godot -ArgumentList @('--version') `
        -WorkingDirectory $GodotProjectDir -LogPath (Join-Path $workRoot 'godot-version.log') `
        -TimeoutSeconds 20 -Label 'Godot version detection'
    $versionMatch = [regex]::Match($godotVersionResult.StdOut, '(\d+\.\d+\.\d+)\.stable\.mono')
    if (-not $versionMatch.Success) { throw 'Could not identify the installed Godot .NET release-template version.' }
    $templateDirectory = Join-Path (Join-Path $env:APPDATA 'Godot\export_templates') "$($versionMatch.Groups[1].Value).stable.mono"
    $GodotReleaseTemplate = Join-Path $templateDirectory 'windows_release_x86_64.exe'
    if (-not (Test-Path -LiteralPath $GodotReleaseTemplate -PathType Leaf)) {
        throw "The matching Godot Windows x64 .NET release template is missing: $GodotReleaseTemplate"
    }

    if ([string]::IsNullOrWhiteSpace($VisualStudioRoot)) {
        $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
        if (Test-Path -LiteralPath $vswhere -PathType Leaf) {
            $vsResult = Invoke-TrackedProcess -FilePath $vswhere `
                -ArgumentList @('-latest', '-products', '*', '-property', 'installationPath') `
                -WorkingDirectory $RepoDir -LogPath (Join-Path $workRoot 'vswhere.log') `
                -TimeoutSeconds 30 -Label 'Visual Studio location detection'
            $VisualStudioRoot = ($vsResult.StdOut -split "`r?`n" | Select-Object -First 1).Trim()
        }
        if ([string]::IsNullOrWhiteSpace($VisualStudioRoot)) {
            $VisualStudioRoot = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\2022\BuildTools'
        }
    }
    $VisualStudioRoot = [System.IO.Path]::GetFullPath($VisualStudioRoot)
    $vcRedistRoot = Join-Path $VisualStudioRoot 'VC\Redist\MSVC'
    if (-not (Test-Path -LiteralPath $vcRedistRoot -PathType Container)) {
        throw "Licensed Visual C++ x64 redist folder was not found under Visual Studio: $vcRedistRoot"
    }
    $vcRedistVersion = Get-ChildItem -LiteralPath $vcRedistRoot -Directory |
        Where-Object { $_.Name -match '^\d+\.\d+\.\d+$' } |
        Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
    if ($null -eq $vcRedistVersion) { throw "No installed Visual C++ x64 redist version found under: $vcRedistRoot" }
    $vcRuntimeDir = Join-Path $vcRedistVersion.FullName 'x64\Microsoft.VC143.CRT'
    $vcRedistList = Join-Path $VisualStudioRoot 'Licenses\1033\Redist.txt'
    foreach ($name in $vcRuntimeNames) {
        if (-not (Test-Path -LiteralPath (Join-Path $vcRuntimeDir $name) -PathType Leaf)) {
            throw "Required Visual C++ runtime dependency is missing: $(Join-Path $vcRuntimeDir $name)"
        }
    }
    if (-not (Test-Path -LiteralPath $vcRedistList -PathType Leaf)) {
        throw "Visual C++ redistribution notice was not found: $vcRedistList"
    }

    $routesRoot = Join-Path $RepoDir 'routes\generated'
    $routeSources = [ordered]@{
        'dunedin-one-way-oval-gardens' = $routesRoot
        'dunedin-south-coastal-loop' = Join-Path $routesRoot 'additional'
        'dunedin-rolling-loop' = Join-Path $routesRoot 'additional'
        'dunedin-signal-hill-climbing-loop' = Join-Path $routesRoot 'additional'
    }
    $routeFiles = @('route-points', 'buildings', 'landmarks', 'street-furniture', 'streets', 'terrain', 'vegetation')
    foreach ($routeId in $routeSources.Keys) {
        foreach ($kind in $routeFiles) {
            $source = Join-Path $routeSources[$routeId] "$routeId.$kind.json"
            if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Built-in route dependency is missing: $source" }
        }
    }
    foreach ($asset in @('Scenes\RideScreen.tscn', 'Assets\Models\WodraCyclist.glb')) {
        if (-not (Test-Path -LiteralPath (Join-Path $GodotProjectDir $asset) -PathType Leaf)) {
            throw "Required Godot runtime asset is missing: $asset"
        }
    }

    if ([string]::IsNullOrWhiteSpace($PublishRoot)) {
        $PublishRoot = Join-Path (Split-Path $RepoDir -Parent) 'wodra_publish'
    }
    $PublishDir = [System.IO.Path]::GetFullPath($PublishRoot)
    if ($PublishDir.Equals($RepoDir, [System.StringComparison]::OrdinalIgnoreCase) -or
        $RepoDir.StartsWith($PublishDir.TrimEnd('\') + '\', [System.StringComparison]::OrdinalIgnoreCase)) {
        throw 'PublishRoot must be outside the repository.'
    }
    $PublishDirStage = Join-Path $workRoot 'Wodra-windows-x64'
    New-Item -ItemType Directory -Path $PublishDirStage -Force | Out-Null
    $ExePath = Join-Path $PublishDirStage 'Wodra.exe'
    $PckPath = Join-Path $PublishDirStage 'Wodra.pck'
    $ManagedDirName = 'data_IndoorCycling.Game_windows_x86_64'
    $ManagedDataDir = Join-Path $PublishDirStage $ManagedDirName
    $ManagedGameAssemblyPath = Join-Path $ManagedDataDir 'IndoorCycling.Game.dll'
    $ArchivePath = Join-Path $workRoot "Wodra-$Version-windows-x64.zip"
    $ChecksumPath = "$ArchivePath.sha256"
    $safeVersion = $Version -replace '[^A-Za-z0-9._+-]', '_'
    $buildLog = Join-Path $workRoot "build-$safeVersion.log"
    $exportLog = Join-Path $workRoot "godot-export-$safeVersion.log"
    Write-Host "Output: $PublishDir"
    Write-Host 'EOS configuration: Loaded (values hidden)'
    Complete-Phase

    Write-Phase 2 "Building $Configuration..."
    [void](Invoke-TrackedProcess -FilePath ((Get-Command dotnet -ErrorAction Stop).Source) `
        -ArgumentList @('build', $Solution, '-c', $Configuration, '-m:1', '-p:EnableEpicOnlineServices=true',
            "-p:EosSdkRoot=$EosSdkRoot", '-p:NuGetAudit=false') `
        -WorkingDirectory $RepoDir -LogPath $buildLog -TimeoutSeconds 900 -Label 'Release solution build')
    Complete-Phase

    Write-Phase 3 'Exporting Godot resources...'
    try {
        $packExportResult = Invoke-TrackedProcess -FilePath $Godot `
            -ArgumentList @('--headless', '--path', $GodotProjectDir, '--export-pack', 'Windows Desktop', $PckPath) `
            -WorkingDirectory $GodotProjectDir -LogPath $exportLog -TimeoutSeconds 45 -Label 'Godot Windows x64 resource export' `
            -StopAfterOutputPattern '\[\s*DONE\s*\]\s+savepack' -StopAfterOutputGraceSeconds 8
        if ($packExportResult.StoppedAfterOutputMarker) {
            Write-Warning "Godot completed the fresh PCK but stalled in its post-export .NET hook; the owned process was stopped and independent managed publish plus smoke testing will validate the package. See $exportLog"
        }
    }
    catch {
        $exportLogText = if (Test-Path -LiteralPath $exportLog) { Get-Content -LiteralPath $exportLog -Raw } else { '' }
        $plainExportLog = [regex]::Replace($exportLogText, "`e\[[0-?]*[ -/]*[@-~]", '')
        $packInfo = Get-Item -LiteralPath $PckPath -ErrorAction SilentlyContinue
        if ($plainExportLog -match '\[\s*DONE\s*\]\s+savepack' -and $null -ne $packInfo -and $packInfo.Length -gt 1000000) {
            Write-Warning "Godot finished writing the fresh PCK but did not exit cleanly after its .NET export hook. The owned process was stopped; the fresh .NET publish and extracted-game smoke test will validate the package. See $exportLog"
        } else {
            throw
        }
    }
    if (-not (Test-Path -LiteralPath $PckPath -PathType Leaf)) {
        throw "Godot did not create the Windows PCK. A successful .NET build alone is insufficient. Full log: $exportLog"
    }
    Copy-WithRetry $GodotReleaseTemplate $ExePath
    if (-not (Test-Path -LiteralPath $ExePath -PathType Leaf)) {
        throw "Could not stage the matching Godot Windows x64 release template: $GodotReleaseTemplate"
    }
    Complete-Phase

    # Godot 4.6.2 may report the managed export hook as failed after its
    # dotnet-publish subprocess has completed. Always make the managed runtime
    # handoff explicit and fresh instead of accepting cached files from .godot.
    if (Select-String -LiteralPath $exportLog -Pattern 'Export \.NET Project: Failed to build project' -Quiet) {
        Write-Warning 'Godot reported its known managed export-hook error during pack export; the release template is staged separately, and a fresh explicit dotnet publish plus extracted-game smoke test validates the managed runtime.'
    }
    if (Test-Path -LiteralPath $ManagedDataDir) {
        Remove-Item -LiteralPath $ManagedDataDir -Recurse -Force
    }
    New-Item -ItemType Directory -Path $ManagedDataDir | Out-Null
    $managedPublishLog = Join-Path $workRoot "managed-publish-$safeVersion.log"
    $GameProject = Join-Path $GodotProjectDir 'IndoorCycling.Game.csproj'
    Write-Phase 4 'Publishing .NET runtime...'
    [void](Invoke-TrackedProcess -FilePath ((Get-Command dotnet -ErrorAction Stop).Source) `
        -ArgumentList @('publish', $GameProject, '-c', 'ExportRelease', '-r', 'win-x64', '--self-contained', 'true',
            '-p:GodotTargetPlatform=Windows', '-p:EnableEpicOnlineServices=true', "-p:EosSdkRoot=$EosSdkRoot",
            '-p:NuGetAudit=false', '-p:DebugSymbols=false', '-p:IncludeSymbols=false', '-o', $ManagedDataDir) `
        -WorkingDirectory $RepoDir -LogPath $managedPublishLog -TimeoutSeconds 900 -Label 'Fresh self-contained managed publish')
    $requiredManaged = @(
        'IndoorCycling.Game.dll', 'IndoorCycling.Game.deps.json', 'IndoorCycling.Game.runtimeconfig.json',
        'GodotSharp.dll', 'coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll',
        'EOSSDK-Win64-Shipping.dll', 'Microsoft.Data.Sqlite.dll', 'e_sqlite3.dll', 'FitSDK.dll'
    ) | ForEach-Object { Join-Path $ManagedDataDir $_ }
    $missingManaged = @($requiredManaged | Where-Object { -not (Test-Path -LiteralPath $_ -PathType Leaf) })
    if ($missingManaged.Count -gt 0) {
        throw "Fresh managed publish is missing required runtime files: $($missingManaged -join ', ')"
    }
    Complete-Phase

    # The standalone EOS runtime and its supplied third-party legal notices are
    # the only files copied from the installed proprietary SDK package.
    Write-Phase 5 'Packaging four routes and EOS...'
    $noticesTarget = Join-Path $PublishDirStage 'ThirdPartyNotices\EOS'
    New-Item -ItemType Directory -Path $noticesTarget -Force | Out-Null
    Copy-Item -LiteralPath $EosSdkNotices -Destination (Join-Path $noticesTarget 'ThirdPartySoftwareNotice.txt')
    Copy-Item -LiteralPath (Join-Path $RepoDir 'THIRD_PARTY_NOTICES.txt') -Destination $PublishDirStage
    Copy-Item -LiteralPath $ConfigPath -Destination (Join-Path $PublishDirStage 'eos-client.json') -Force
    foreach ($name in $vcRuntimeNames) {
        Copy-Item -LiteralPath (Join-Path $vcRuntimeDir $name) -Destination $PublishDirStage
    }
    $msNotices = Join-Path $PublishDirStage 'ThirdPartyNotices\Microsoft'
    New-Item -ItemType Directory -Path $msNotices -Force | Out-Null
    Copy-Item -LiteralPath $vcRedistList -Destination (Join-Path $msNotices 'VisualCpp-Redist.txt')

    $builtInRoot = Join-Path $PublishDirStage 'Routes'
    foreach ($routeId in $routeSources.Keys) {
        $destination = Join-Path $builtInRoot $routeId
        New-Item -ItemType Directory -Path $destination -Force | Out-Null
        foreach ($kind in $routeFiles) {
            Copy-Item -LiteralPath (Join-Path $routeSources[$routeId] "$routeId.$kind.json") -Destination $destination
        }
    }

    $invariant = [Globalization.CultureInfo]::InvariantCulture
    $routeManifest = foreach ($routeId in $routeSources.Keys) {
        $routePath = Join-Path (Join-Path $builtInRoot $routeId) "$routeId.route-points.json"
        $route = Get-Content -LiteralPath $routePath -Raw | ConvertFrom-Json -ErrorAction Stop
        $canonicalLines = foreach ($point in $route.points) {
            @(
                ([double]$point.distanceMeters).ToString('F3', $invariant),
                ([double]$point.latitude).ToString('F7', $invariant),
                ([double]$point.longitude).ToString('F7', $invariant),
                ([double]$point.elevationMeters).ToString('F3', $invariant)
            ) -join ','
        }
        $canonical = [string]::Join("`n", [string[]]$canonicalLines)
        $fingerprint = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData(
            [Text.Encoding]::UTF8.GetBytes($canonical))).ToLowerInvariant()
        if (-not [string]::IsNullOrWhiteSpace([string]$route.geometryFingerprint) -and
            $route.geometryFingerprint -ne $fingerprint) {
            throw "Built-in route '$routeId' geometry fingerprint validation failed."
        }
        $attribution = if (-not [string]::IsNullOrWhiteSpace([string]$route.sourceAttribution)) {
            [string]$route.sourceAttribution
        } elseif ($routeId -eq 'dunedin-one-way-oval-gardens') {
            '© OpenStreetMap contributors, ODbL 1.0; elevation: LINZ hosted Otago Regional Council LiDAR DEM, CC-BY-4.0'
        } else { throw "Built-in route '$routeId' has no source attribution." }
        [pscustomobject]@{
            Id = $routeId
            Name = [string]$route.name
            GeometryFingerprint = $fingerprint
            SourceAttribution = $attribution
        }
    }
    [pscustomobject]@{ Version = $Version; Routes = @($routeManifest) } |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $builtInRoot 'builtin-route-manifest.json')

    # Symbols are development artifacts and can expose local source paths.
    Get-ChildItem -LiteralPath $PublishDirStage -Filter '*.pdb' -File -Recurse | Remove-Item -Force
    Complete-Phase

    Write-Phase 6 'Verifying archive and smoke testing...'
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory(
        $PublishDirStage, $ArchivePath, [System.IO.Compression.CompressionLevel]::Optimal, $false)
    # This is a PowerShell script, so its success/failure is communicated by
    # terminating errors rather than $LASTEXITCODE (which belongs to the most
    # recent native process and may still contain an unrelated exit code).
    try {
        & (Join-Path $RepoDir 'tools\Verify-WodraRelease.ps1') -ZipPath $ArchivePath -ErrorAction Stop
    }
    catch {
        throw "Release archive verification failed: $($_.Exception.Message)"
    }

    if (-not $SkipOfflineSmokeTest) {
        $extractDir = Join-Path $workRoot "smoke-$safeVersion-$([guid]::NewGuid().ToString('N'))"
        New-Item -ItemType Directory -Path $extractDir | Out-Null
        try {
            [System.IO.Compression.ZipFile]::ExtractToDirectory($ArchivePath, $extractDir)
            $smokeExe = Join-Path $extractDir 'Wodra.exe'
            $smokeLog = Join-Path $workRoot "offline-smoke-$safeVersion.log"
            [void](Invoke-TrackedProcess -FilePath $smokeExe `
                -ArgumentList @('--headless', '--quit-after', '2', '--log-file', $smokeLog) `
                -WorkingDirectory $extractDir -LogPath (Join-Path $workRoot "smoke-process-$safeVersion.log") `
                -TimeoutSeconds 45 -Label 'Extracted Wodra offline smoke test' `
                -RemoveEnvironmentVariables @('WODRA_EOS_PRODUCT_ID', 'WODRA_EOS_SANDBOX_ID',
                    'WODRA_EOS_DEPLOYMENT_ID', 'WODRA_EOS_CLIENT_ID', 'WODRA_EOS_CLIENT_SECRET', 'WODRA_EOS_CACHE_DIR'))
            Write-Host 'Extracted executable startup smoke test passed (--headless --quit-after 2).'
            if ((Test-Path -LiteralPath $smokeLog -PathType Leaf) -and
                (Select-String -LiteralPath $smokeLog -Pattern 'UnauthorizedAccessException|readonly database' -Quiet)) {
                Write-Warning "The smoke process could not write this sandbox account's Godot user data. Startup exited normally, but settings/history persistence needs a writable user profile for full verification."
            }
        }
        finally { Remove-Item -LiteralPath $extractDir -Recurse -Force -ErrorAction SilentlyContinue }
    }

    $hash = (Get-FileHash -LiteralPath $ArchivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    Set-Content -LiteralPath $ChecksumPath -Value "$hash  $(Split-Path $ArchivePath -Leaf)"
    Write-Host "SHA-256 verified: $hash"
    Complete-Phase

    Write-Phase 7 'Copying release to publish directory...'
    New-Item -ItemType Directory -Path $PublishDir -Force | Out-Null
    $finalExport = Join-Path $PublishDir 'Wodra-windows-x64'
    $finalArchive = Join-Path $PublishDir (Split-Path $ArchivePath -Leaf)
    $finalChecksum = "$finalArchive.sha256"
    $incomingExport = Join-Path $PublishDir ".Wodra-windows-x64.incoming-$runId"
    $incomingArchive = Join-Path $PublishDir ".$(Split-Path $ArchivePath -Leaf).incoming-$runId"
    $incomingChecksum = Join-Path $PublishDir ".$(Split-Path $ChecksumPath -Leaf).incoming-$runId"
    $incomingArtifacts = @($incomingExport, $incomingArchive, $incomingChecksum)
    New-Item -ItemType Directory -Path $incomingExport -Force | Out-Null
    foreach ($item in Get-ChildItem -LiteralPath $PublishDirStage -Force) {
        Copy-WithRetry $item.FullName (Join-Path $incomingExport $item.Name)
    }
    Copy-WithRetry $ArchivePath $incomingArchive
    Copy-WithRetry $ChecksumPath $incomingChecksum
    if (-not (Test-Path -LiteralPath (Join-Path $incomingExport 'Wodra.exe') -PathType Leaf) -or
        -not (Test-Path -LiteralPath (Join-Path (Join-Path $incomingExport $ManagedDirName) 'EOSSDK-Win64-Shipping.dll') -PathType Leaf) -or
        -not (Test-Path -LiteralPath (Join-Path $incomingExport 'eos-client.json') -PathType Leaf)) {
        throw "Copied release directory is incomplete: $incomingExport"
    }
    if ((Get-FileHash -LiteralPath $incomingArchive -Algorithm SHA256).Hash.ToLowerInvariant() -ne $hash) {
        throw "Copied ZIP hash does not match staged archive: $incomingArchive"
    }
    $checksumLine = Get-Content -LiteralPath $incomingChecksum -Raw
    if ($checksumLine.Trim() -ne "$hash  $(Split-Path $ArchivePath -Leaf)") {
        throw "Copied checksum file does not match the verified archive: $incomingChecksum"
    }

    $replacements = @(
        [pscustomobject]@{ Incoming = $incomingExport; Target = $finalExport },
        [pscustomobject]@{ Incoming = $incomingArchive; Target = $finalArchive },
        [pscustomobject]@{ Incoming = $incomingChecksum; Target = $finalChecksum }
    )
    $backups = [System.Collections.Generic.List[object]]::new()
    $installed = [System.Collections.Generic.List[string]]::new()
    try {
        foreach ($replacement in $replacements) {
            if (Test-Path -LiteralPath $replacement.Target) {
                $backup = "$($replacement.Target).previous-$runId"
                Move-Item -LiteralPath $replacement.Target -Destination $backup -ErrorAction Stop
                $backups.Add([pscustomobject]@{ Backup = $backup; Target = $replacement.Target })
            }
            Move-Item -LiteralPath $replacement.Incoming -Destination $replacement.Target -ErrorAction Stop
            $installed.Add($replacement.Target)
        }
    }
    catch {
        $installedTargets = $installed.ToArray()
        [array]::Reverse($installedTargets)
        foreach ($target in $installedTargets) {
            Remove-Item -LiteralPath $target -Recurse -Force -ErrorAction SilentlyContinue
        }
        $backupEntries = $backups.ToArray()
        [array]::Reverse($backupEntries)
        foreach ($backupEntry in $backupEntries) {
            if (Test-Path -LiteralPath $backupEntry.Backup) {
                Move-Item -LiteralPath $backupEntry.Backup -Destination $backupEntry.Target -ErrorAction SilentlyContinue
            }
        }
        throw "Could not install release files in '$PublishDir'. Previous files were restored where possible. Cause: $($_.Exception.Message)"
    }
    foreach ($backupEntry in $backups) {
        Remove-Item -LiteralPath $backupEntry.Backup -Recurse -Force -ErrorAction SilentlyContinue
    }
    Write-Host "    executable: $finalExport"
    Write-Host "    ZIP: $finalArchive"
    Write-Host "    SHA-256: $finalChecksum"
    Complete-Phase
    foreach ($temporary in @($incomingExport, $incomingArchive, $incomingChecksum)) {
        if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Recurse -Force -ErrorAction SilentlyContinue }
    }

    if ($CreateGitHubDraft) {
        if ([string]::IsNullOrWhiteSpace($GitHubTag)) { throw 'Pass -GitHubTag with an existing, reviewed Git tag to create a GitHub draft.' }
        if ($GitHubTag -ne $Version) { throw 'For GitHub publishing, -Version must exactly match -GitHubTag.' }
        $ghCommand = Get-Command gh -ErrorAction SilentlyContinue
        if ($null -eq $ghCommand) { throw 'GitHub CLI (gh) is required for -CreateGitHubDraft.' }
        $gitExe = (Get-Command git -ErrorAction Stop).Source
        $tagResult = Invoke-TrackedProcess -FilePath $gitExe `
            -ArgumentList @('-C', $RepoDir, 'rev-parse', '--verify', "refs/tags/$GitHubTag^{commit}") `
            -WorkingDirectory $RepoDir -LogPath (Join-Path $workRoot 'git-release-tag.log') `
            -TimeoutSeconds 30 -Label 'GitHub tag verification'
        $tagCommit = $tagResult.StdOut.Trim()
        $headResult = Invoke-TrackedProcess -FilePath $gitExe -ArgumentList @('-C', $RepoDir, 'rev-parse', 'HEAD') `
            -WorkingDirectory $RepoDir -LogPath (Join-Path $workRoot 'git-head.log') `
            -TimeoutSeconds 30 -Label 'Git commit verification'
        $headCommit = $headResult.StdOut.Trim()
        if ($tagCommit -ne $headCommit) { throw 'The GitHub release tag must point to the exact commit being packaged.' }
        $statusResult = Invoke-TrackedProcess -FilePath $gitExe `
            -ArgumentList @('-C', $RepoDir, 'status', '--porcelain') -WorkingDirectory $RepoDir `
            -LogPath (Join-Path $workRoot 'git-clean-tree.log') -TimeoutSeconds 30 -Label 'Git working tree verification'
        if (-not [string]::IsNullOrWhiteSpace($statusResult.StdOut)) {
            throw 'The GitHub release source tree must be clean. Commit the reviewed release pipeline and assets before publishing.'
        }
        [void](Invoke-TrackedProcess -FilePath $ghCommand.Source `
            -ArgumentList @('release', 'create', $GitHubTag, $finalArchive, $finalChecksum, '--draft', '--prerelease',
                '--verify-tag', '--title', "Wodra $GitHubTag", '--notes',
                'Windows x64 alpha build. See the release notes for verification scope and known issues.') `
            -WorkingDirectory $RepoDir -LogPath (Join-Path $workRoot 'github-draft.log') `
            -TimeoutSeconds 600 -Label 'GitHub draft release upload')
    }

    Write-Host ("`nSUCCESS (total {0:n1}s)" -f $phaseClock.Elapsed.TotalSeconds)
    Write-Host "Executable: $finalExport\Wodra.exe"
    Write-Host "ZIP: $finalArchive"
    Write-Host "SHA256: $finalChecksum"
}
finally {
    foreach ($temporary in $incomingArtifacts) {
        if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Recurse -Force -ErrorAction SilentlyContinue }
    }
    if ($hasMutex) { $mutex.ReleaseMutex() }
    $mutex.Dispose()
}
