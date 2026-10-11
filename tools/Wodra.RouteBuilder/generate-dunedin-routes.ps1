param(
    [Parameter(Mandatory = $true)]
    [string]$PbfPath,
    [string]$OutputDirectory = 'routes/generated/additional',
    [switch]$NoInstall
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$pbf = (Resolve-Path -LiteralPath $PbfPath).Path
if ((Get-Item -LiteralPath $pbf).Length -lt 1) { throw "OSM PBF is empty: $pbf" }
$output = if ([IO.Path]::IsPathRooted($OutputDirectory)) {
    [IO.Path]::GetFullPath($OutputDirectory)
} else {
    [IO.Path]::GetFullPath((Join-Path $repoRoot $OutputDirectory))
}
$configs = @(
    'dunedin-south-coastal-loop',
    'dunedin-rolling-loop',
    'dunedin-signal-hill-climbing-loop'
)

Push-Location $repoRoot
try {
    foreach ($id in $configs) {
        $config = "routes/authoring/$id.json"
        & dotnet run --project tools/Wodra.RouteBuilder --no-restore -- build `
            --pbf $pbf --config $config --output $output --cache-dir routes/cache `
            --no-bundle-streets true
        if ($LASTEXITCODE -ne 0) { throw "RouteBuilder failed for $id (exit $LASTEXITCODE)." }

        $routePath = Join-Path $output "$id.route-points.json"
        $route = Get-Content -Raw -LiteralPath $routePath | ConvertFrom-Json
        if (-not $route.isValid -or $route.routeType -ne 'loop' -or $route.points.Count -lt 3) {
            throw "Generated package $id failed route-points validation."
        }
        $first = $route.points[0]
        $last = $route.points[-1]
        if ([math]::Abs($first.latitude - $last.latitude) -gt 0.000001 -or
            [math]::Abs($first.longitude - $last.longitude) -gt 0.000001) {
            throw "Generated package $id is not closed at its lap seam."
        }
        if ([math]::Abs($route.points[-1].distanceMeters - $route.actualOsmDistanceMeters) -gt 0.01) {
            throw "Generated package $id has inconsistent lap distance."
        }
    }

    if (-not $NoInstall) {
        if ([string]::IsNullOrWhiteSpace($env:LOCALAPPDATA)) { throw 'LOCALAPPDATA is not set; use -NoInstall to only generate packages.' }
        $packageRoot = Join-Path $env:LOCALAPPDATA 'Wodra/Routes'
        foreach ($id in $configs) {
            $destination = Join-Path $packageRoot $id
            New-Item -ItemType Directory -Force -Path $destination | Out-Null
            Get-ChildItem -LiteralPath $destination -File -Filter "$id.*" -ErrorAction SilentlyContinue |
                Remove-Item -Force
            $files = Get-ChildItem -LiteralPath $output -File | Where-Object {
                $_.Name -eq "$id.route-points.json" -or $_.Name -eq "$id.report.json" -or
                $_.Name -eq "$id.preview.html" -or $_.Name -eq "$id.elevation.csv" -or
                $_.Name -like "$id.*.json"
            }
            foreach ($file in $files) { Copy-Item -LiteralPath $file.FullName -Destination $destination -Force }
            Set-Content -LiteralPath (Join-Path $destination 'ATTRIBUTION.txt') -Encoding utf8 -Value @(
                'OpenStreetMap data © OpenStreetMap contributors, ODbL 1.0.',
                'Elevation data and provider attribution are recorded in the route report.',
                'Keep this attribution file with the route package when redistributing.'
            )
        }
        Write-Host "Installed validated route packages in $packageRoot"
    }
}
finally {
    Pop-Location
}
