param(
    [Parameter(Mandatory = $true)]
    [string] $GodotExe,
    [switch] $Headless
)

$ErrorActionPreference = 'Stop'
$godot = (Resolve-Path -LiteralPath $GodotExe).Path
$project = (Resolve-Path -LiteralPath 'src/IndoorCycling.Game').Path
$reviewRoot = Join-Path $env:TEMP ('WodraMountainBikeReview-' + [guid]::NewGuid().ToString('N'))
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
}
try {
    $env:APPDATA = $appData
    $env:LOCALAPPDATA = $localAppData
    $env:TEMP = $tempRoot
    $env:TMP = $tempRoot
    $env:WODRA_REVIEW_ROOT = $reviewRoot
    $env:WODRA_REVIEW_OUTPUT = Join-Path $reviewRoot 'artifacts'
    New-Item -ItemType Directory -Path $env:WODRA_REVIEW_OUTPUT | Out-Null

    & $godot --headless --path $project --script 'res://Tests/VerifyReviewUserData.gd'
    if ($LASTEXITCODE -ne 0) { throw "Isolated user-data preflight failed (exit $LASTEXITCODE); production scene was not started." }
    & $godot --headless --editor --path $project --import --quit
    if ($LASTEXITCODE -ne 0) { throw "Godot project import failed (exit $LASTEXITCODE); production scene was not started." }
    $modeArgs = @()
    if ($Headless) { $modeArgs += '--headless' }
    & $godot @modeArgs --path $project --scene 'res://Tests/MountainBikeRideScreenReviewTest.tscn'
    if ($LASTEXITCODE -ne 0) { throw "Mountain-bike production-scene review failed (exit $LASTEXITCODE); artifacts: $env:WODRA_REVIEW_OUTPUT" }
    Write-Host "Mountain-bike review artifacts: $env:WODRA_REVIEW_OUTPUT"
    Write-Host "Disposable user data: $reviewRoot"
}
finally {
    foreach ($name in $old.Keys) {
        if ($null -eq $old[$name]) { Remove-Item "Env:$name" -ErrorAction SilentlyContinue }
        else { Set-Item "Env:$name" $old[$name] }
    }
}
