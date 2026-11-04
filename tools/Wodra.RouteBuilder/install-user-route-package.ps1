param(
    [Parameter(Mandatory = $true)]
    [string] $PackageDirectory
)

$ErrorActionPreference = 'Stop'
$source = (Resolve-Path -LiteralPath $PackageDirectory).Path
$points = Get-ChildItem -LiteralPath $source -Filter '*.route-points.json' -File
if ($points.Count -ne 1) { throw 'Package directory must contain exactly one *.route-points.json file.' }
$metadata = Get-Content -LiteralPath $points[0].FullName -Raw | ConvertFrom-Json
if ($metadata.id -notmatch '^[a-z0-9]+(?:-[a-z0-9]+)*$' -or $points[0].BaseName -ne "$($metadata.id).route-points") {
    throw 'Route package ID must match the route-points filename and be a safe lowercase route ID.'
}

$destinationRoot = Join-Path $env:LOCALAPPDATA 'Wodra\Routes'
$destination = Join-Path $destinationRoot $metadata.id
if (Test-Path -LiteralPath $destination) { throw "Route package already exists at '$destination'; remove or back it up before reinstalling." }
New-Item -ItemType Directory -Path $destinationRoot -Force | Out-Null
Copy-Item -LiteralPath $source -Destination $destination -Recurse
Write-Host "Installed $($metadata.name) ($($metadata.id)) to $destination"
