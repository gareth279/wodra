$ErrorActionPreference = 'Stop'
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
. (Join-Path $repositoryRoot 'tools\WodraBuiltInRoutePackages.ps1')
$temporaryRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('WodraBuiltInRoutePackageTest-' + [guid]::NewGuid().ToString('N'))
$routeRoot = Join-Path $temporaryRoot 'Routes'
$releaseRoot = Join-Path $temporaryRoot 'release'
$zipPath = Join-Path $temporaryRoot 'fixture.zip'
New-Item -ItemType Directory -Path $temporaryRoot, $releaseRoot | Out-Null
try {
    $manifest = Write-WodraBuiltInRouteBundle -RepositoryRoot $repositoryRoot -DestinationRoot $routeRoot -Version '0.1.0-alpha.70'
    if ($manifest.Routes.Count -ne 6) { throw "Expected six built-in route entries, got $($manifest.Routes.Count)." }
    $mtbId = 'signal-hill-big-easy-mtb'
    $mtb = @($manifest.Routes | Where-Object Id -eq $mtbId | Select-Object -First 1)[0]
    if ($null -eq $mtb -or [string]::IsNullOrWhiteSpace($mtb.GeometryFingerprint) -or
        [string]::IsNullOrWhiteSpace($mtb.SourceAttribution)) {
        throw 'The MTB release manifest entry is missing its geometry fingerprint or attribution.'
    }
    $mtbDirectory = Join-Path $routeRoot $mtbId
    $mtbFiles = @(Get-ChildItem -LiteralPath $mtbDirectory -File | Select-Object -ExpandProperty Name | Sort-Object)
    $expectedMtbFiles = @(
        "$mtbId.route-points.json",
        "$mtbId.terrain.json",
        "$mtbId.vegetation.json"
    ) | Sort-Object
    if (($mtbFiles -join '|') -ne ($expectedMtbFiles -join '|')) {
        throw "The MTB package contains an unexpected sidecar set: $($mtbFiles -join ', ')"
    }
    $xcId = 'wodra-xc-skills-loop'
    $xc = @($manifest.Routes | Where-Object Id -eq $xcId | Select-Object -First 1)[0]
    if ($null -eq $xc -or $xc.SourceAttribution -notlike 'Fictional Wodra-authored*') {
        throw 'The fictional XC route manifest entry is missing its attribution.'
    }
    $xcDirectory = Join-Path $routeRoot $xcId
    $xcFiles = @(Get-ChildItem -LiteralPath $xcDirectory -File | Select-Object -ExpandProperty Name | Sort-Object)
    $expectedXcFiles = @("$xcId.route-points.json", "$xcId.terrain.json", "$xcId.vegetation.json", "$xcId.trail-features.json") | Sort-Object
    if (($xcFiles -join '|') -ne ($expectedXcFiles -join '|')) {
        throw "The fictional XC package contains an unexpected sidecar set: $($xcFiles -join ', ')"
    }
    $xcFeatures = Get-Content -LiteralPath (Join-Path $xcDirectory "$xcId.trail-features.json") -Raw | ConvertFrom-Json
    if (@($xcFeatures.features).Count -ne 3 -or @($xcFeatures.berms).Count -lt 2 -or @($xcFeatures.rollers).Count -lt 1) {
        throw 'The fictional XC feature package does not contain its three line choices, berms and rollers.'
    }
    foreach ($feature in $xcFeatures.features) {
        if ([math]::Abs($feature.path[0].routeDistanceMeters - $feature.startMeters) -gt 0.01 -or
            [math]::Abs($feature.path[-1].routeDistanceMeters - $feature.endMeters) -gt 0.01 -or
            [math]::Abs($feature.path[0].lateralMeters) -gt 0.01 -or
            [math]::Abs($feature.path[-1].lateralMeters) -gt 0.01 -or
            [math]::Abs($feature.path[0].surfaceLiftMeters) -gt 0.01 -or
            [math]::Abs($feature.path[-1].surfaceLiftMeters) -gt 0.01) {
            throw "XC line '$($feature.id)' does not make a level, centerline join at both ends."
        }
    }
    $xcRoute = Get-Content -LiteralPath (Join-Path $xcDirectory "$xcId.route-points.json") -Raw | ConvertFrom-Json
    $xcVegetation = Get-Content -LiteralPath (Join-Path $xcDirectory "$xcId.vegetation.json") -Raw | ConvertFrom-Json
    if ([math]::Abs($xcRoute.actualRouteDistanceMeters - 1600) -gt 0.01 -or
        [math]::Abs($xcRoute.totalAscentMeters - 26) -gt 1 -or
        @($xcVegetation.trees | ForEach-Object { [math]::Floor($_.nearestRouteDistanceMeters / 200) } | Sort-Object -Unique).Count -lt 6) {
        throw 'The XC package must retain its complete loop profile and route-spaced vegetation chunks.'
    }

    $requiredFiles = @(
        'Wodra.exe', 'Wodra.pck', 'Assets/Branding/wodra-icon-512.png', 'Assets/Branding/wodra.ico',
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
        'MSVCP140.dll', 'VCRUNTIME140.dll', 'VCRUNTIME140_1.dll',
        'THIRD_PARTY_NOTICES.txt',
        'ThirdPartyNotices/EOS/ThirdPartySoftwareNotice.txt',
        'ThirdPartyNotices/Microsoft/VisualCpp-Redist.txt'
    )
    foreach ($file in $requiredFiles) {
        $path = Join-Path $releaseRoot ($file -replace '/', '\')
        New-Item -ItemType Directory -Path (Split-Path $path -Parent) -Force | Out-Null
        [System.IO.File]::WriteAllText($path, 'release verifier fixture')
    }
    $eos = [pscustomobject]@{
        ProductId = 'fixture-product'
        SandboxId = 'fixture-sandbox'
        DeploymentId = 'fixture-deployment'
        ClientId = 'fixture-client'
        ClientSecret = 'fixture-secret'
    } | ConvertTo-Json
    [System.IO.File]::WriteAllText((Join-Path $releaseRoot 'eos-client.json'), $eos)
    Copy-Item -LiteralPath $routeRoot -Destination (Join-Path $releaseRoot 'Routes') -Recurse
    [System.IO.Compression.ZipFile]::CreateFromDirectory($releaseRoot, $zipPath)

    & (Join-Path $repositoryRoot 'tools\Verify-WodraRelease.ps1') -ZipPath $zipPath
    Write-Host 'PASS: release package includes and verifies all six routes, including MTB and fictional XC sidecars.'
}
finally {
    if (Test-Path -LiteralPath $temporaryRoot) { Remove-Item -LiteralPath $temporaryRoot -Recurse -Force }
}
