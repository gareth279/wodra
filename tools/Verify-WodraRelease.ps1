param(
    [Parameter(Mandatory = $true)]
    [string]$ZipPath
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'WodraBuiltInRoutePackages.ps1')
$routeDefinitions = @(Get-WodraBuiltInRouteDefinitions -RepositoryRoot (Split-Path $PSScriptRoot -Parent))
Add-Type -AssemblyName System.IO.Compression.FileSystem

$ZipPath = [System.IO.Path]::GetFullPath($ZipPath)
if (-not (Test-Path -LiteralPath $ZipPath -PathType Leaf)) {
    throw "Release archive not found: $ZipPath"
}

$required = @(
    'Wodra.exe',
    'Wodra.pck',
    'Assets/Branding/wodra-icon-512.png',
    'Assets/Branding/wodra.ico',
    'data_IndoorCycling.Game_windows_x86_64/IndoorCycling.Game.dll',
    'data_IndoorCycling.Game_windows_x86_64/IndoorCycling.Game.deps.json',
    'data_IndoorCycling.Game_windows_x86_64/IndoorCycling.Game.runtimeconfig.json',
    'data_IndoorCycling.Game_windows_x86_64/GodotSharp.dll',
    'data_IndoorCycling.Game_windows_x86_64/coreclr.dll',
    'data_IndoorCycling.Game_windows_x86_64/hostfxr.dll',
    'data_IndoorCycling.Game_windows_x86_64/Microsoft.Data.Sqlite.dll',
    'data_IndoorCycling.Game_windows_x86_64/e_sqlite3.dll',
    'data_IndoorCycling.Game_windows_x86_64/FitSDK.dll',
    'data_IndoorCycling.Game_windows_x86_64/IndoorCycling.Trainer.Windows.dll',
    'data_IndoorCycling.Game_windows_x86_64/EOSSDK-Win64-Shipping.dll',
    'MSVCP140.dll',
    'VCRUNTIME140.dll',
    'VCRUNTIME140_1.dll',
    'eos-client.json',
    'Routes/builtin-route-manifest.json',
    'THIRD_PARTY_NOTICES.txt',
    'ThirdPartyNotices/EOS/ThirdPartySoftwareNotice.txt',
    'ThirdPartyNotices/Microsoft/VisualCpp-Redist.txt'
)

$archive = [System.IO.Compression.ZipFile]::OpenRead($ZipPath)
try {
    $entries = @{}
    foreach ($entry in $archive.Entries) { $entries[$entry.FullName] = $entry }
    foreach ($name in $required) {
        if (-not $entries.ContainsKey($name)) { throw "Release archive is missing required file: $name" }
    }

    $configReader = [System.IO.StreamReader]::new($entries['eos-client.json'].Open())
    try { $config = $configReader.ReadToEnd() | ConvertFrom-Json -ErrorAction Stop }
    catch { throw 'Packaged EOS client config is not valid JSON.' }
    finally { $configReader.Dispose() }

    $allowedFields = @('ProductId', 'SandboxId', 'DeploymentId', 'ClientId', 'ClientSecret')
    $configProperties = @($config.PSObject.Properties.Name)
    $missingFields = @($allowedFields | Where-Object {
        $null -eq $config.PSObject.Properties[$_] -or [string]::IsNullOrWhiteSpace([string]$config.$_)
    })
    $unexpectedFields = @($configProperties | Where-Object { $_ -notin $allowedFields })
    if ($missingFields.Count -gt 0) { throw "Packaged EOS config is missing required fields: $($missingFields -join ', ')" }
    if ($unexpectedFields.Count -gt 0) { throw 'Packaged EOS config contains fields outside the five public client fields.' }
    Remove-Variable config

    $forbidden = @($archive.Entries | Where-Object {
        $_.FullName -match '(^|/)(Samples|EOS-SDK|\.godot|\.mono|logs|settings|user_settings)(/|$)' -or
        $_.FullName -match '\.(pdb|fit|tcx|gpx|osm\.pbf|pbf|log|cs)$' -or
        $_.FullName -match '(^|/)eos-cache(/|$)'
    })
    if ($forbidden.Count -gt 0) {
        throw "Release archive contains a development-only or private file: $($forbidden[0].FullName)"
    }

    $invariant = [Globalization.CultureInfo]::InvariantCulture
    $manifestReader = [System.IO.StreamReader]::new($entries['Routes/builtin-route-manifest.json'].Open())
    try { $manifest = $manifestReader.ReadToEnd() | ConvertFrom-Json -ErrorAction Stop }
    catch { throw 'Built-in route manifest is not valid JSON.' }
    finally { $manifestReader.Dispose() }
    if ($manifest.Routes.Count -ne $routeDefinitions.Count) { throw "Route manifest does not list exactly $($routeDefinitions.Count) built-in routes." }
    foreach ($definition in $routeDefinitions) {
        $routeId = $definition.Id
        foreach ($file in $definition.Files) {
            $entryName = "Routes/$routeId/$routeId.$file.json"
            if (-not $entries.ContainsKey($entryName)) { throw "Release archive is missing route dependency: $entryName" }
            if ($file -ne 'route-points') {
                $sidecarReader = [System.IO.StreamReader]::new($entries[$entryName].Open())
                try { $sidecar = $sidecarReader.ReadToEnd() | ConvertFrom-Json -ErrorAction Stop }
                catch { throw "Route sidecar '$entryName' is not valid JSON." }
                finally { $sidecarReader.Dispose() }
                if ($sidecar.id -ne $routeId) { throw "Route sidecar '$entryName' has a mismatched route ID." }
                Remove-Variable sidecar
            }
        }

        $routeEntry = $entries["Routes/$routeId/$routeId.route-points.json"]
        $reader = [System.IO.StreamReader]::new($routeEntry.Open())
        try { $route = $reader.ReadToEnd() | ConvertFrom-Json -ErrorAction Stop }
        catch { throw "Route package '$routeId' contains invalid route JSON." }
        finally { $reader.Dispose() }
        if ($route.id -ne $routeId -or $route.isValid -ne $true -or $route.points.Count -lt 2) {
            throw "Route package '$routeId' has invalid route identity, validity, or geometry."
        }
        if ($routeId -ne 'dunedin-one-way-oval-gardens' -and
            ([string]::IsNullOrWhiteSpace([string]$route.geometryFingerprint) -or
             [string]::IsNullOrWhiteSpace([string]$route.sourceAttribution))) {
            throw "Route package '$routeId' is missing its fingerprint or source attribution."
        }
        if ($routeId -eq 'signal-hill-big-easy-mtb' -and
            ($route.routeType -ne 'pointToPoint' -or $route.environment -ne 'MountainBike' -or
                $route.discipline -ne 'MountainBike' -or @($route.surfaceSegments).Count -lt 1)) {
            throw 'Packaged MTB route is missing point-to-point, MountainBike, or surface metadata.'
        }
        if ($routeId -eq 'wodra-xc-skills-loop' -and
            ($route.routeType -ne 'loop' -or $route.environment -ne 'MountainBike' -or
                $route.discipline -ne 'MountainBike' -or @($route.surfaceSegments).Count -lt 1 -or
                $route.sourceAttribution -notlike 'Fictional Wodra-authored*')) {
            throw 'Packaged fictional XC route is missing loop, MountainBike, authored-surface, or provenance metadata.'
        }

        $actualFingerprint = Get-WodraRouteGeometryFingerprint -Route $route
        if (-not [string]::IsNullOrWhiteSpace([string]$route.geometryFingerprint) -and
            $route.geometryFingerprint -ne $actualFingerprint) {
            throw "Route package '$routeId' fingerprint does not match its geometry."
        }
        $manifestRoute = @($manifest.Routes | Where-Object { $_.Id -eq $routeId }) | Select-Object -First 1
        if ($null -eq $manifestRoute -or $manifestRoute.GeometryFingerprint -ne $actualFingerprint -or
            [string]::IsNullOrWhiteSpace([string]$manifestRoute.SourceAttribution)) {
            throw "Route manifest fingerprint or attribution is invalid for '$routeId'."
        }
        Write-Host "Route verified: $routeId ($($route.points.Count) points, geometry $($actualFingerprint.Substring(0, 12))…)"
    }
    Remove-Variable manifest

    Write-Host "Release archive verified: $ZipPath"
    Write-Host "Validated executable, managed runtime, EOS client config, notices, and six route packages."
}
finally { $archive.Dispose() }
