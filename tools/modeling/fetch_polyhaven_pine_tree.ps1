param(
    [string]$DownloadDirectory = (Join-Path $env:LOCALAPPDATA 'Wodra\LicensedAssets\PolyHavenTreeSmall02'),
    [string]$BlenderExe = 'C:\Program Files\Blender Foundation\Blender 5.2\blender.exe'
)

$ErrorActionPreference = 'Stop'
$api = Invoke-RestMethod -Uri 'https://api.polyhaven.com/files/tree_small_02'
$asset = $api.gltf.'1k'.gltf
New-Item -ItemType Directory -Force -Path $DownloadDirectory | Out-Null

function Save-VerifiedDownload([string]$Url, [string]$ExpectedMd5, [string]$Destination) {
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Destination) | Out-Null
    if (-not (Test-Path -LiteralPath $Destination)) {
        Invoke-WebRequest -Uri $Url -OutFile $Destination
    }
    $actual = (Get-FileHash -LiteralPath $Destination -Algorithm MD5).Hash.ToLowerInvariant()
    if ($actual -ne $ExpectedMd5.ToLowerInvariant()) {
        throw "Poly Haven asset checksum mismatch for $Destination"
    }
}

$gltfPath = Join-Path $DownloadDirectory 'tree_small_02_1k.gltf'
Save-VerifiedDownload $asset.url $asset.md5 $gltfPath
foreach ($entry in $asset.include.PSObject.Properties) {
    $relative = $entry.Name -replace '/', [IO.Path]::DirectorySeparatorChar
    $destination = Join-Path $DownloadDirectory $relative
    Save-VerifiedDownload $entry.Value.url $entry.Value.md5 $destination
}

if (-not (Test-Path -LiteralPath $BlenderExe)) {
    throw "Blender 5.2 executable not found: $BlenderExe"
}
$prepareScript = Join-Path $PSScriptRoot 'prepare_polyhaven_pine_tree.py'
& $BlenderExe --background --python $prepareScript -- $gltfPath
if ($LASTEXITCODE -ne 0) { throw "Blender tree preparation failed with exit code $LASTEXITCODE" }
