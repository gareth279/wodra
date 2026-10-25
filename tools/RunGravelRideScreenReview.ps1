param(
    [Parameter(Mandatory = $true)]
    [string] $GodotExe,
    [string] $ScenePath = 'res://Tests/GravelRideScreenReviewTest.tscn',
    [switch] $FullLap,
    [switch] $Headless,
    [switch] $ForceWindowsDisplay,
    [switch] $VisualCheckpoints
)

$ErrorActionPreference = 'Stop'
$godot = (Resolve-Path -LiteralPath $GodotExe).Path
$project = (Resolve-Path -LiteralPath 'src/IndoorCycling.Game').Path
$reviewRoot = Join-Path $env:TEMP ('WodraGravelReview-' + [guid]::NewGuid().ToString('N'))
$appData = Join-Path $reviewRoot 'appdata'
$localAppData = Join-Path $reviewRoot 'localappdata'
$tempRoot = Join-Path $reviewRoot 'temp'
New-Item -ItemType Directory -Path $appData, $localAppData, $tempRoot | Out-Null

$old = @{
    APPDATA = $env:APPDATA
    LOCALAPPDATA = $env:LOCALAPPDATA
    TEMP = $env:TEMP
    TMP = $env:TMP
    WODRA_REVIEW_ROOT = $env:WODRA_REVIEW_ROOT
    WODRA_REVIEW_OUTPUT = $env:WODRA_REVIEW_OUTPUT
    WODRA_GRAVEL_FULL_LAP = $env:WODRA_GRAVEL_FULL_LAP
    WODRA_GRAVEL_VISUAL_CHECKPOINTS = $env:WODRA_GRAVEL_VISUAL_CHECKPOINTS
}
try {
    $env:APPDATA = $appData
    $env:LOCALAPPDATA = $localAppData
    $env:TEMP = $tempRoot
    $env:TMP = $tempRoot
    $env:WODRA_REVIEW_ROOT = $reviewRoot
    $env:WODRA_REVIEW_OUTPUT = Join-Path $reviewRoot 'artifacts'
    $env:WODRA_GRAVEL_FULL_LAP = if ($FullLap) { '1' } else { '0' }
    $env:WODRA_GRAVEL_VISUAL_CHECKPOINTS = if ($VisualCheckpoints) { '1' } else { '0' }
    New-Item -ItemType Directory -Path $env:WODRA_REVIEW_OUTPUT | Out-Null

    $preflight = @('--headless', '--path', $project, '--script', 'res://Tests/VerifyReviewUserData.gd')
    & $godot @preflight
    if ($LASTEXITCODE -ne 0) { throw "Isolated user-data preflight failed (exit $LASTEXITCODE); production scene was not started." }

    # Standard Godot imports (models, textures, fonts) are project cache files;
    # generate them only after the disposable app-data environment is active.
    & $godot --headless --editor --path $project --import --quit
    if ($LASTEXITCODE -ne 0) { throw "Godot project import failed (exit $LASTEXITCODE); production scene was not started." }

    $sceneArgs = @('--path', $project, '--scene', $ScenePath)
    if ($Headless) { $sceneArgs = @('--headless') + $sceneArgs }
    if ($ForceWindowsDisplay) { $sceneArgs = @('--display-driver', 'windows', '--rendering-driver', 'opengl3') + $sceneArgs }
    & $godot @sceneArgs
    if ($LASTEXITCODE -ne 0) { throw "Gravel production-scene review failed (exit $LASTEXITCODE); artifacts: $env:WODRA_REVIEW_OUTPUT" }
    Write-Host "Gravel review artifacts: $env:WODRA_REVIEW_OUTPUT"
    Write-Host "Disposable user data: $reviewRoot"
}
finally {
    foreach ($name in $old.Keys) {
        if ($null -eq $old[$name]) { Remove-Item "Env:$name" -ErrorAction SilentlyContinue }
        else { Set-Item "Env:$name" $old[$name] }
    }
}
