$ErrorActionPreference = "Stop"

# ------------------------------------------------------------
# Wodra release publisher
# ------------------------------------------------------------

$Godot = "C:\Tools\Godot\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64.exe"

$RepoDir = $PSScriptRoot
$GodotProjectDir = Join-Path $RepoDir "src\IndoorCycling.Game"
$Solution = Join-Path $RepoDir "wodra.sln"

# Output beside the repo:
# C:\Users\garet\OneDrive\wodra_publish
# C:\Users\garet\OneDrive\wodra_publish.zip
$ParentDir = Split-Path $RepoDir -Parent
$PublishDir = Join-Path $ParentDir "wodra_publish"
$ExePath = Join-Path $PublishDir "wodra.exe"
$ZipPath = Join-Path $ParentDir "wodra_publish.zip"

Write-Host ""
Write-Host "========================================"
Write-Host " Wodra Release Build"
Write-Host "========================================"
Write-Host ""

# ------------------------------------------------------------
# Validate paths
# ------------------------------------------------------------

if (-not (Test-Path $Godot)) {
    throw "Godot executable not found: $Godot"
}

if (-not (Test-Path $Solution)) {
    throw "Solution not found: $Solution"
}

$ProjectGodot = Join-Path $GodotProjectDir "project.godot"

if (-not (Test-Path $ProjectGodot)) {
    throw "Godot project not found: $ProjectGodot"
}

Write-Host "Repository:"
Write-Host "  $RepoDir"

Write-Host ""
Write-Host "Godot project:"
Write-Host "  $GodotProjectDir"

Write-Host ""
Write-Host "Publish folder:"
Write-Host "  $PublishDir"

# ------------------------------------------------------------
# Clean old publish output
# ------------------------------------------------------------

Write-Host ""
Write-Host "Cleaning previous publish..."

if (Test-Path $PublishDir) {
    Remove-Item $PublishDir -Recurse -Force
}

New-Item -ItemType Directory -Path $PublishDir | Out-Null

if (Test-Path $ZipPath) {
    Remove-Item $ZipPath -Force
}

# ------------------------------------------------------------
# Build C# solution
# ------------------------------------------------------------

Write-Host ""
Write-Host "Building Wodra Release..."
Write-Host ""

dotnet build $Solution -c Release -m:1

if ($LASTEXITCODE -ne 0) {
    throw "dotnet build failed with exit code $LASTEXITCODE"
}

# ------------------------------------------------------------
# Godot export
# ------------------------------------------------------------

Write-Host ""
Write-Host "Exporting Windows release..."
Write-Host ""

& $Godot `
    --headless `
    --path $GodotProjectDir `
    --export-release "Windows Desktop" `
    $ExePath

if ($LASTEXITCODE -ne 0) {
    throw "Godot export failed with exit code $LASTEXITCODE"
}

if (-not (Test-Path $ExePath)) {
    throw "Godot export completed but wodra.exe was not found at: $ExePath"
}

# ------------------------------------------------------------
# Create portable ZIP
# ------------------------------------------------------------

Write-Host ""
Write-Host "Creating portable ZIP..."
Write-Host ""

Compress-Archive `
    -Path (Join-Path $PublishDir "*") `
    -DestinationPath $ZipPath `
    -CompressionLevel Optimal

if (-not (Test-Path $ZipPath)) {
    throw "ZIP creation failed: $ZipPath was not created."
}

# ------------------------------------------------------------
# Done
# ------------------------------------------------------------

$ExeSizeMb = [math]::Round((Get-Item $ExePath).Length / 1MB, 1)
$ZipSizeMb = [math]::Round((Get-Item $ZipPath).Length / 1MB, 1)

Write-Host ""
Write-Host "========================================"
Write-Host " Wodra publish complete"
Write-Host "========================================"
Write-Host ""

Write-Host "Executable:"
Write-Host "  $ExePath"
Write-Host "  $ExeSizeMb MB"

Write-Host ""

Write-Host "ZIP to copy to trainer PC:"
Write-Host "  $ZipPath"
Write-Host "  $ZipSizeMb MB"

Write-Host ""