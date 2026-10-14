$ErrorActionPreference = 'Stop'
$packageRoot = Join-Path $PSScriptRoot 'wodra-strava-broker'
$output = Join-Path $PSScriptRoot 'wodra-strava-broker.zip'
if (-not (Test-Path -LiteralPath (Join-Path $packageRoot 'public/index.php'))) { throw 'Strava broker source package is incomplete.' }
if (Test-Path -LiteralPath $output) { Remove-Item -LiteralPath $output -Force }
Compress-Archive -Path (Join-Path $packageRoot '*') -DestinationPath $output -CompressionLevel Optimal
Write-Output $output
