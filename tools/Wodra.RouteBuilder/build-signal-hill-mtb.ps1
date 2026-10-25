param(
    [string] $OutputDirectory = 'routes/generated/signal-hill-big-easy-mtb',
    [string] $SourceOsmPath,
    [string] $VegetationSourcePath
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..')).Path
Push-Location $repoRoot
try {
    $output = [System.IO.Path]::GetFullPath($OutputDirectory)
    $cache = [System.IO.Path]::GetFullPath('routes/cache/elevation')
    $temporary = Join-Path $env:TEMP ('WodraSignalHillMtbBuild-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $temporary, $output | Out-Null
    $source = if ([string]::IsNullOrWhiteSpace($SourceOsmPath)) { Join-Path $temporary 'signal-hill-big-easy-mtb.osm' } else { (Resolve-Path -LiteralPath $SourceOsmPath).Path }
    $context = if ([string]::IsNullOrWhiteSpace($VegetationSourcePath)) { Join-Path $temporary 'signal-hill-big-easy-mtb-context.osm' } else { (Resolve-Path -LiteralPath $VegetationSourcePath).Path }
    $config = 'routes/authoring/signal-hill-big-easy-mtb.json'
    $assetDirectory = 'src/IndoorCycling.Game/Data/Routes'

    if ([string]::IsNullOrWhiteSpace($SourceOsmPath)) {
        python tools/Wodra.RouteBuilder/download_signal_hill_mtb_source.py --output $source
        if ($LASTEXITCODE -ne 0) { throw 'Could not fetch the reviewed OSM singletrack ways.' }
    }

    dotnet run --project tools/Wodra.RouteBuilder -- build --pbf $source --config $config `
        --output $output --route-only true --with-elevation true --cache-dir none
    if ($LASTEXITCODE -ne 0) { throw 'The route and LINZ elevation/terrain package did not build.' }

    if ([string]::IsNullOrWhiteSpace($VegetationSourcePath)) {
        python tools/Wodra.RouteBuilder/download_route_context.py --route (Join-Path $output 'signal-hill-big-easy-mtb.route-points.json') `
            --output $context --radius-meters 75 --sample-spacing-meters 400
        if ($LASTEXITCODE -ne 0) { throw 'Could not fetch the bounded OSM vegetation context.' }
    }

    dotnet run --project tools/Wodra.RouteBuilder -- build --pbf $source --config $config `
        --output $output --route-only true --with-elevation true --vegetation-source $context --cache-dir none
    if ($LASTEXITCODE -ne 0) { throw 'The approved route package and mapped vegetation sidecar did not build.' }

    foreach ($suffix in @('route-points.json', 'terrain.json', 'vegetation.json')) {
        $name = "signal-hill-big-easy-mtb.$suffix"
        Copy-Item -LiteralPath (Join-Path $output $name) -Destination (Join-Path $assetDirectory $name) -Force
    }
    Write-Host "Built and embedded Signal Hill MTB route assets from OSM plus LINZ cache '$cache'."
    Write-Host "Temporary raw OSM extracts are in '$temporary' and are not copied into the application bundle."
}
finally { Pop-Location }
