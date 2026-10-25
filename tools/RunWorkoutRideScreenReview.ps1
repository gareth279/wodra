param(
    [Parameter(Mandatory = $true)]
    [string] $GodotExe,
    [switch] $Headless
)

$ErrorActionPreference = 'Stop'
$godot = (Resolve-Path -LiteralPath $GodotExe).Path
$project = (Resolve-Path -LiteralPath 'src/IndoorCycling.Game').Path
$reviewRoot = Join-Path $env:TEMP ('WodraWorkoutReview-' + [guid]::NewGuid().ToString('N'))
$appData = Join-Path $reviewRoot 'appdata'
$localAppData = Join-Path $reviewRoot 'localappdata'
$tempRoot = Join-Path $reviewRoot 'temp'
$output = Join-Path $reviewRoot 'artifacts'
New-Item -ItemType Directory -Path $appData, $localAppData, $tempRoot, $output | Out-Null

$old = @{
    APPDATA = $env:APPDATA; LOCALAPPDATA = $env:LOCALAPPDATA; TEMP = $env:TEMP; TMP = $env:TMP
    WODRA_REVIEW_ROOT = $env:WODRA_REVIEW_ROOT; WODRA_REVIEW_OUTPUT = $env:WODRA_REVIEW_OUTPUT
}
try {
    $env:APPDATA = $appData; $env:LOCALAPPDATA = $localAppData; $env:TEMP = $tempRoot; $env:TMP = $tempRoot
    $env:WODRA_REVIEW_ROOT = $reviewRoot; $env:WODRA_REVIEW_OUTPUT = $output

    & $godot --headless --path $project --script 'res://Tests/VerifyReviewUserData.gd'
    if ($LASTEXITCODE -ne 0) { throw "Isolated user-data preflight failed (exit $LASTEXITCODE); app scene was not started." }
    & dotnet build (Join-Path $project 'IndoorCycling.Game.csproj') -c Debug --no-restore -m:1 -p:EnableEpicOnlineServices=false -p:NuGetAudit=false
    if ($LASTEXITCODE -ne 0) { throw "Godot C# build failed (exit $LASTEXITCODE); app scene was not started." }
    & $godot --headless --editor --path $project --import --quit
    if ($LASTEXITCODE -ne 0) { throw "Godot project import failed (exit $LASTEXITCODE); app scene was not started." }
    $sceneArgs = @('--path', $project, '--scene', 'res://Tests/WorkoutRideScreenReviewTest.tscn')
    if ($Headless) { $sceneArgs = @('--headless') + $sceneArgs }
    & $godot @sceneArgs
    if ($LASTEXITCODE -ne 0) { throw "Workout production scene review failed (exit $LASTEXITCODE); artifacts: $output" }
    Write-Host "Workout review artifacts: $output"
    Write-Host "Disposable user data: $reviewRoot"
}
finally {
    foreach ($name in $old.Keys) {
        if ($null -eq $old[$name]) { Remove-Item "Env:$name" -ErrorAction SilentlyContinue }
        else { Set-Item "Env:$name" $old[$name] }
    }
}
