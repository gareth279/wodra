$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem

$packageRoot = Join-Path $PSScriptRoot 'wodra-strava-broker'
$output = Join-Path $PSScriptRoot 'wodra-strava-broker.zip'
$files = @(
    'public/.htaccess',
    'public/index.php',
    'public/deployment.example.php',
    'private/Broker.php',
    'private/config.example.php',
    'tests/run.php',
    'README.md'
)
foreach ($relativePath in $files) {
    if (-not (Test-Path -LiteralPath (Join-Path $packageRoot $relativePath) -PathType Leaf)) {
        throw "Required Strava broker package file is missing: $relativePath"
    }
}

if (Test-Path -LiteralPath $output) { Remove-Item -LiteralPath $output -Force }
$archive = [System.IO.Compression.ZipFile]::Open($output, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($relativePath in $files) {
        $sourcePath = Join-Path $packageRoot $relativePath
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
            $archive, $sourcePath, ($relativePath -replace '\\', '/'), [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
    $names = @($archive.Entries | ForEach-Object FullName)
    if ($names -contains 'private/config.php' -or $names -contains 'public/deployment.php' -or
        $names | Where-Object { $_ -match '(^|/)(link-sessions|rate|runtime|tokens)(/|\.|$)' }) {
        throw 'Refusing to package live configuration or runtime storage.'
    }
}
finally { $archive.Dispose() }
Write-Output $output
