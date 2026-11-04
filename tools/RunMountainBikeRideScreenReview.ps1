param(
    [Parameter(Mandatory = $true)]
    [string] $GodotExe,
    [switch] $Headless,
    [switch] $XcVisualOnly,
    [switch] $XcCaptureOnly,
    [switch] $SideCamera,
    [string] $EvidenceDirectory
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
    WODRA_REVIEW_XC_CAPTURE_ONLY = $env:WODRA_REVIEW_XC_CAPTURE_ONLY
    WODRA_REVIEW_XC_SIDE_CAMERA = $env:WODRA_REVIEW_XC_SIDE_CAMERA
}
try {
    $env:APPDATA = $appData
    $env:LOCALAPPDATA = $localAppData
    $env:TEMP = $tempRoot
    $env:TMP = $tempRoot
    $env:WODRA_REVIEW_ROOT = $reviewRoot
    $env:WODRA_REVIEW_OUTPUT = Join-Path $reviewRoot 'artifacts'
    if ($XcVisualOnly) { $env:WODRA_REVIEW_XC_VISUAL_ONLY = '1' }
    if ($XcCaptureOnly) {
        $env:WODRA_REVIEW_XC_VISUAL_ONLY = '1'
        $env:WODRA_REVIEW_XC_CAPTURE_ONLY = '1'
    }
    if ($SideCamera) { $env:WODRA_REVIEW_XC_SIDE_CAMERA = '1' }
    New-Item -ItemType Directory -Path $env:WODRA_REVIEW_OUTPUT | Out-Null

    $preflight = Start-Process -FilePath $godot -ArgumentList @('--headless', '--path', $project,
        '--script', 'res://Tests/VerifyReviewUserData.gd') -Wait -PassThru -WindowStyle Hidden
    if ($preflight.ExitCode -ne 0) { throw "Isolated user-data preflight failed (exit $($preflight.ExitCode)); production scene was not started." }
    $import = Start-Process -FilePath $godot -ArgumentList @('--headless', '--editor', '--path', $project,
        '--import', '--quit') -Wait -PassThru -WindowStyle Hidden
    if ($import.ExitCode -ne 0) { throw "Godot project import failed (exit $($import.ExitCode)); production scene was not started." }
    $modeArgs = @()
    if ($Headless) { $modeArgs += '--headless' }
    $reviewLog = Join-Path $reviewRoot 'mountain-bike-review.log'
    $sceneArguments = @($modeArgs) + @('--path', $project, '--scene', 'res://Tests/MountainBikeRideScreenReviewTest.tscn', '--log-file', $reviewLog)
    $sceneProcess = Start-Process -FilePath $godot -ArgumentList $sceneArguments -Wait -PassThru
    if (Test-Path -LiteralPath $reviewLog) {
        Get-Content -LiteralPath $reviewLog | Write-Output
    }
    if ($sceneProcess.ExitCode -ne 0) { throw "Mountain-bike production-scene review failed (exit $($sceneProcess.ExitCode)); log: $reviewLog; artifacts: $env:WODRA_REVIEW_OUTPUT" }
    if (-not $Headless) {
        $ffmpeg = Get-Command ffmpeg -ErrorAction SilentlyContinue
        if ($null -eq $ffmpeg) { throw 'ffmpeg is required to create continuous RideScreen jump videos.' }
        $angle = if ($SideCamera) { 'side' } else { 'chase' }
        foreach ($featureId in @('north-tabletop', 'ridge-tabletop', 'lower-tabletop')) {
            $frames = @(Get-ChildItem -LiteralPath $env:WODRA_REVIEW_OUTPUT -Filter "xc-jump-$angle-$featureId-frame-*.png" -File | Sort-Object Name)
            if ($frames.Count -lt 30) { throw "RideScreen $angle video for $featureId is incomplete ($($frames.Count) frames)." }
            $video = Join-Path $env:WODRA_REVIEW_OUTPUT "xc-jumps-$angle-$featureId.mp4"
            $pattern = Join-Path $env:WODRA_REVIEW_OUTPUT "xc-jump-$angle-$featureId-frame-%05d.png"
            $encoderStdout = Join-Path $reviewRoot "ffmpeg-$angle-$featureId.stdout.log"
            $encoderStderr = Join-Path $reviewRoot "ffmpeg-$angle-$featureId.stderr.log"
            $encoder = Start-Process -FilePath $ffmpeg.Source -ArgumentList @('-y', '-framerate', '15', '-i', $pattern,
                '-c:v', 'libx264', '-pix_fmt', 'yuv420p', '-movflags', '+faststart', $video) -Wait -PassThru -WindowStyle Hidden `
                -RedirectStandardOutput $encoderStdout -RedirectStandardError $encoderStderr
            if ($encoder.ExitCode -ne 0) { throw "Could not encode the RideScreen $angle video for $featureId; frames: $($frames.Count); stderr: $encoderStderr" }
            Write-Host "RideScreen $angle jump video: $video ($($frames.Count) frames)"
        }
        if ($EvidenceDirectory) {
            $captureLap = if ($XcCaptureOnly) { 1 } else { 2 }
            $evidencePath = [System.IO.Path]::GetFullPath($EvidenceDirectory)
            New-Item -ItemType Directory -Path $evidencePath -Force | Out-Null
            Get-ChildItem -LiteralPath $env:WODRA_REVIEW_OUTPUT -File |
                Where-Object { $_.Name -like "xc-jumps-$angle-*.mp4" -or $_.Name -like "xc-lap-$captureLap-$angle-*.png" -or $_.Name -eq 'xc-feature-score-gaming-on.png' -or $_.Name -eq 'fictional-xc-motion.csv' } |
                Copy-Item -Destination $evidencePath -Force
        }
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
