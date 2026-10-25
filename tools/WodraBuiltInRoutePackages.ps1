function Get-WodraBuiltInRouteDefinitions {
    param([Parameter(Mandatory = $true)][string]$RepositoryRoot)

    $routesRoot = Join-Path $RepositoryRoot 'routes\generated'
    $additionalRoutesRoot = Join-Path $routesRoot 'additional'
    $gameRoutesRoot = Join-Path $RepositoryRoot 'src\IndoorCycling.Game\Data\Routes'
    $roadFiles = @('route-points', 'buildings', 'landmarks', 'street-furniture', 'streets', 'terrain', 'vegetation')
    @(
        [pscustomobject]@{ Id = 'dunedin-one-way-oval-gardens'; SourceDirectory = $routesRoot; Files = $roadFiles },
        [pscustomobject]@{ Id = 'dunedin-south-coastal-loop'; SourceDirectory = $additionalRoutesRoot; Files = $roadFiles },
        [pscustomobject]@{ Id = 'dunedin-rolling-loop'; SourceDirectory = $additionalRoutesRoot; Files = $roadFiles },
        [pscustomobject]@{ Id = 'dunedin-signal-hill-climbing-loop'; SourceDirectory = $additionalRoutesRoot; Files = $roadFiles },
        [pscustomobject]@{ Id = 'signal-hill-big-easy-mtb'; SourceDirectory = $gameRoutesRoot; Files = @('route-points', 'terrain', 'vegetation') },
        [pscustomobject]@{ Id = 'wodra-xc-skills-loop'; SourceDirectory = $gameRoutesRoot; Files = @('route-points', 'terrain', 'vegetation', 'trail-features') }
    )
}

function Get-WodraRouteGeometryFingerprint {
    param([Parameter(Mandatory = $true)]$Route)

    $invariant = [Globalization.CultureInfo]::InvariantCulture
    $canonicalLines = foreach ($point in $Route.points) {
        @(
            ([double]$point.distanceMeters).ToString('F3', $invariant),
            $(if ($null -ne $point.localXMeters -and $null -ne $point.localZMeters) {
                'local:' + ([double]$point.localXMeters).ToString('F3', $invariant) + ',' + ([double]$point.localZMeters).ToString('F3', $invariant)
            } else {
                ([double]$point.latitude).ToString('F7', $invariant) + ',' + ([double]$point.longitude).ToString('F7', $invariant)
            }),
            ([double]$point.elevationMeters).ToString('F3', $invariant)
        ) -join ','
    }
    $canonical = [string]::Join("`n", [string[]]$canonicalLines)
    [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData(
        [Text.Encoding]::UTF8.GetBytes($canonical))).ToLowerInvariant()
}

function Write-WodraBuiltInRouteBundle {
    param(
        [Parameter(Mandatory = $true)][string]$RepositoryRoot,
        [Parameter(Mandatory = $true)][string]$DestinationRoot,
        [Parameter(Mandatory = $true)][string]$Version
    )

    $definitions = @(Get-WodraBuiltInRouteDefinitions -RepositoryRoot $RepositoryRoot)
    $manifestRoutes = foreach ($definition in $definitions) {
        $destination = Join-Path $DestinationRoot $definition.Id
        New-Item -ItemType Directory -Path $destination -Force | Out-Null
        foreach ($kind in $definition.Files) {
            $fileName = "$($definition.Id).$kind.json"
            $source = Join-Path $definition.SourceDirectory $fileName
            if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
                throw "Built-in route dependency is missing: $source"
            }
            Copy-Item -LiteralPath $source -Destination (Join-Path $destination $fileName) -Force
        }

        $routePath = Join-Path $destination "$($definition.Id).route-points.json"
        try { $route = Get-Content -LiteralPath $routePath -Raw | ConvertFrom-Json -ErrorAction Stop }
        catch { throw "Built-in route '$($definition.Id)' route-points file is not valid JSON." }
        if ($route.id -ne $definition.Id -or $route.isValid -ne $true -or $route.points.Count -lt 2) {
            throw "Built-in route '$($definition.Id)' has invalid identity, validity, or geometry."
        }
        $fingerprint = Get-WodraRouteGeometryFingerprint -Route $route
        if (-not [string]::IsNullOrWhiteSpace([string]$route.geometryFingerprint) -and
            $route.geometryFingerprint -ne $fingerprint) {
            throw "Built-in route '$($definition.Id)' geometry fingerprint validation failed."
        }
        if ($definition.Id -ne 'dunedin-one-way-oval-gardens' -and
            [string]::IsNullOrWhiteSpace([string]$route.geometryFingerprint)) {
            throw "Built-in route '$($definition.Id)' has no stored geometry fingerprint."
        }
        $attribution = if (-not [string]::IsNullOrWhiteSpace([string]$route.sourceAttribution)) {
            [string]$route.sourceAttribution
        } elseif ($definition.Id -eq 'dunedin-one-way-oval-gardens') {
            '© OpenStreetMap contributors, ODbL 1.0; elevation: LINZ hosted Otago Regional Council LiDAR DEM, CC-BY-4.0'
        } else { throw "Built-in route '$($definition.Id)' has no source attribution." }
        foreach ($kind in @($definition.Files | Where-Object { $_ -ne 'route-points' })) {
            $sidecarPath = Join-Path $destination "$($definition.Id).$kind.json"
            try { $sidecar = Get-Content -LiteralPath $sidecarPath -Raw | ConvertFrom-Json -ErrorAction Stop }
            catch { throw "Built-in route '$($definition.Id)' sidecar '$kind' is not valid JSON." }
            if ($sidecar.id -ne $definition.Id) {
                throw "Built-in route '$($definition.Id)' sidecar '$kind' has a mismatched route ID."
            }
        }
        if ($definition.Id -eq 'signal-hill-big-easy-mtb' -and
            ($route.routeType -ne 'pointToPoint' -or $route.environment -ne 'MountainBike' -or
                $route.discipline -ne 'MountainBike' -or @($route.surfaceSegments).Count -lt 1)) {
            throw 'The bundled MTB route is missing point-to-point, MountainBike, or surface metadata.'
        }
        if ($definition.Id -eq 'wodra-xc-skills-loop') {
            if ($route.routeType -ne 'loop' -or $route.environment -ne 'MountainBike' -or
                $route.discipline -ne 'MountainBike' -or @($route.surfaceSegments).Count -lt 1 -or
                $route.sourceAttribution -notlike 'Fictional Wodra-authored*') {
                throw 'The fictional XC route is missing loop, MountainBike, authored-surface, or provenance metadata.'
            }
            $featurePath = Join-Path $destination "$($definition.Id).trail-features.json"
            $features = Get-Content -LiteralPath $featurePath -Raw | ConvertFrom-Json -ErrorAction Stop
            if ($features.id -ne $definition.Id -or $features.version -ne 1 -or @($features.features).Count -ne 3 -or
                @($features.berms).Count -lt 2 -or @($features.rollers).Count -lt 1) {
                throw 'The fictional XC feature package must include three optional jump lines, berms, and rollers.'
            }
        }

        [pscustomobject]@{
            Id = $definition.Id
            Name = [string]$route.name
            GeometryFingerprint = $fingerprint
            SourceAttribution = $attribution
        }
    }
    $manifest = [pscustomobject]@{ Version = $Version; Routes = @($manifestRoutes) }
    $manifestPath = Join-Path $DestinationRoot 'builtin-route-manifest.json'
    $json = $manifest | ConvertTo-Json -Depth 5
    [System.IO.File]::WriteAllText($manifestPath, $json + [Environment]::NewLine,
        [System.Text.UTF8Encoding]::new($false))
    $manifest
}
