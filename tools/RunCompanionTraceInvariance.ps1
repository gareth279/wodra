param(
	[string]$GodotPath = $env:GODOT_PATH,
	[switch]$KeepTestData
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$projectPath = Join-Path $repoRoot 'src/IndoorCycling.Game'
if ([string]::IsNullOrWhiteSpace($GodotPath)) {
	$GodotPath = 'C:\Tools\Godot\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64_console.exe'
}
if (-not (Test-Path -LiteralPath $GodotPath -PathType Leaf)) { throw "Pinned Godot console executable not found: $GodotPath" }

# Prove the pinned engine resolves the Wodra identity inside a fresh APPDATA
# before starting the actual production RideScreen regression.
$originalAppData = [Environment]::GetEnvironmentVariable('APPDATA', 'Process')
if ([string]::IsNullOrWhiteSpace($originalAppData)) { throw 'APPDATA is unavailable; refusing to start Wodra without a verifiable isolated data root.' }
$ownerUserData = [IO.Path]::GetFullPath((Join-Path $originalAppData 'Godot/app_userdata/Wodra'))
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ("WodraCompanionTrace-" + [Guid]::NewGuid().ToString('N'))
$isolatedAppData = Join-Path $testRoot 'Roaming'
$expectedUserData = [IO.Path]::GetFullPath((Join-Path $isolatedAppData 'Godot/app_userdata/Wodra'))
if ([string]::Equals($ownerUserData, $expectedUserData, [StringComparison]::OrdinalIgnoreCase) -or
	$expectedUserData.StartsWith($ownerUserData.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
	throw "Isolation preflight rejected overlapping user-data paths: $expectedUserData"
}
if (Test-Path -LiteralPath $expectedUserData) { throw "Isolation preflight expected a fresh user-data directory but it already exists: $expectedUserData" }
New-Item -ItemType Directory -Path $isolatedAppData -Force | Out-Null

$probeProject = Join-Path $testRoot 'probe'
New-Item -ItemType Directory -Path $probeProject -Force | Out-Null
Set-Content -LiteralPath (Join-Path $probeProject 'project.godot') -Encoding utf8 -Value @'
config_version=5
[application]
config/name="Wodra"
'@
Set-Content -LiteralPath (Join-Path $probeProject 'probe.gd') -Encoding utf8 -Value @'
extends SceneTree
func _initialize():
    print("WODRA_USER_DATA_DIR=" + OS.get_user_data_dir())
    quit(0)
'@
$previousAppData = [Environment]::GetEnvironmentVariable('APPDATA', 'Process')
$success = $false
try {
	[Environment]::SetEnvironmentVariable('APPDATA', $isolatedAppData, 'Process')
	$probeLog = Join-Path $testRoot 'user-data-probe.log'
	& $GodotPath --headless --path $probeProject --script (Join-Path $probeProject 'probe.gd') --log-file $probeLog
	if ($LASTEXITCODE -ne 0) { throw "Godot user-data isolation probe failed with exit code $LASTEXITCODE." }
	$probeText = Get-Content -LiteralPath $probeLog -Raw
	$match = [Regex]::Match($probeText, 'WODRA_USER_DATA_DIR=(.+)')
	if (-not $match.Success) { throw "Godot isolation probe did not report OS.get_user_data_dir(). Log: $probeLog" }
	$actualUserData = [IO.Path]::GetFullPath($match.Groups[1].Value.Trim())
	if (-not [string]::Equals($actualUserData, $expectedUserData, [StringComparison]::OrdinalIgnoreCase)) {
		throw "Godot resolved user data outside the isolated test root. Expected '$expectedUserData', got '$actualUserData'. Refusing to launch Wodra."
	}
	if ([string]::Equals($actualUserData, $ownerUserData, [StringComparison]::OrdinalIgnoreCase)) {
		throw 'Godot resolved the owner profile path. Refusing to launch Wodra.'
	}
	Write-Host "Isolation verified before Wodra launch: $actualUserData"
	$importLog = Join-Path $testRoot 'godot-import.log'
	& $GodotPath --headless --editor --path $projectPath --import --quit --log-file $importLog
	if ($LASTEXITCODE -ne 0) { throw "Godot project import failed with exit code $LASTEXITCODE. Log: $importLog" }
	$rideLog = Join-Path $testRoot 'companion-trace-invariance.log'
	& $GodotPath --headless --path $projectPath --scene res://Tests/CompanionTraceInvarianceReviewTest.tscn --log-file $rideLog
	if ($LASTEXITCODE -ne 0) { throw "Companion trace regression failed with exit code $LASTEXITCODE. Log: $rideLog" }
	$rideText = Get-Content -LiteralPath $rideLog -Raw
	if ($rideText -notmatch '\[CompanionTraceInvarianceReview\] PASS:' -or $rideText -match '\[CompanionTraceInvarianceReview\] FAIL:|SCRIPT ERROR:') {
		throw "Companion trace regression did not report a clean pass. Log: $rideLog"
	}
	Write-Host 'Companion enabled/disabled, relay success/failure/timeout traces passed.'
	Write-Host "Isolated logs and test profile: $testRoot"
	$success = $true
}
finally {
	[Environment]::SetEnvironmentVariable('APPDATA', $previousAppData, 'Process')
	if ($success -and -not $KeepTestData) { Remove-Item -LiteralPath $testRoot -Recurse -Force }
	elseif (-not $success) { Write-Warning "Preserving isolated failure evidence at $testRoot" }
}
