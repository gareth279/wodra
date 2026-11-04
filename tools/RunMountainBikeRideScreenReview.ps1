param(
    [Parameter(Mandatory = $true)]
    [string] $GodotExe,
    [switch] $Headless,
    [switch] $XcVisualOnly
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
    WODRA_REVIEW_XC_VISUAL_ONLY = $env:WODRA_REVIEW_XC_VISUAL_ONLY
}
try {
    $env:APPDATA = $appData
    $env:LOCALAPPDATA = $localAppData
    $env:TEMP = $tempRoot
    $env:TMP = $tempRoot
    $env:WODRA_REVIEW_ROOT = $reviewRoot
    $env:WODRA_REVIEW_OUTPUT = Join-Path $reviewRoot 'artifacts'
    if ($XcVisualOnly) { $env:WODRA_REVIEW_XC_VISUAL_ONLY = '1' }
    New-Item -ItemType Directory -Path $env:WODRA_REVIEW_OUTPUT | Out-Null

    & $godot --headless --path $project --script 'res://Tests/VerifyReviewUserData.gd'
    if ($LASTEXITCODE -ne 0) { throw "Isolated user-data preflight failed (exit $LASTEXITCODE); production scene was not started." }
    & $godot --headless --editor --path $project --import --quit
    if ($LASTEXITCODE -ne 0) { throw "Godot project import failed (exit $LASTEXITCODE); production scene was not started." }
    $modeArgs = @()
    if ($Headless) { $modeArgs += '--headless' }
    $sceneArguments = @($modeArgs) + @('--path', $project, '--scene', 'res://Tests/MountainBikeRideScreenReviewTest.tscn')
    $sceneProcess = Start-Process -FilePath $godot -ArgumentList $sceneArguments -Wait -PassThru
    if ($sceneProcess.ExitCode -ne 0) { throw "Mountain-bike production-scene review failed (exit $($sceneProcess.ExitCode)); artifacts: $env:WODRA_REVIEW_OUTPUT" }
    if (-not $Headless) {
        $ffmpeg = Get-Command ffmpeg -ErrorAction SilentlyContinue
        $frames = @(Get-ChildItem -LiteralPath $env:WODRA_REVIEW_OUTPUT -Filter 'xc-jump-frame-*.png' -File | Sort-Object Name)
        if ($null -ne $ffmpeg -and $frames.Count -ge 36) {
            & $ffmpeg.Source -y -framerate 15 -i (Join-Path $env:WODRA_REVIEW_OUTPUT 'xc-jump-frame-%05d.png') `
                -c:v libx264 -pix_fmt yuv420p (Join-Path $env:WODRA_REVIEW_OUTPUT 'xc-jumps-ridescreen.mp4') *> $null
            if ($LASTEXITCODE -ne 0) { throw "Could not encode the RideScreen jump-motion clip; frames: $($frames.Count)" }
            Write-Host "RideScreen jump motion video: $(Join-Path $env:WODRA_REVIEW_OUTPUT 'xc-jumps-ridescreen.mp4') ($($frames.Count) frames)"
        } else { throw "RideScreen jump-motion evidence is incomplete (ffmpeg available=$($null -ne $ffmpeg), frames=$($frames.Count))." }
    }
    Write-Host "Mountain-bike review artifacts: $env:WODRA_REVIEW_OUTPUT"
    Write-Host "Disposable user data: $reviewRoot"
}
finally {
    foreach ($name in $old.Keys) {
        if ($null -eq $old[$name]) { Remove-Item "Env:$name" -ErrorAction SilentlyContinue }
        else { Set-Item "Env:$name" $old[$name] }
    }
}
