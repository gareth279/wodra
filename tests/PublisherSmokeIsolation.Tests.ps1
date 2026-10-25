$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\tools\PublisherSmokeIsolation.ps1')

$repoRoot = Split-Path $PSScriptRoot -Parent
$publisher = Get-Content -LiteralPath (Join-Path $repoRoot 'publish.ps1') -Raw
$bootstrap = Get-Content -LiteralPath (Join-Path $repoRoot 'src\IndoorCycling.Game\Scripts\GameBootstrap.cs') -Raw
$guard = Get-Content -LiteralPath (Join-Path $repoRoot 'src\IndoorCycling.Game\Scripts\UserDataIsolationGuard.cs') -Raw

if ($publisher -notmatch "PublisherSmokeIsolation\.ps1" -or
    $publisher -notmatch "New-WodraSmokeIsolation" -or
    ([regex]::Matches($publisher, 'Invoke-WithWodraSmokeIsolation').Count -lt 2) -or
    $publisher -notmatch "--require-userdata-under" -or
    $publisher -notmatch "AllowedExitCodes @\(41\)") {
    throw 'The extracted-app publisher smoke no longer establishes and verifies disposable user-data isolation.'
}
$tokens = $null
$parseErrors = $null
[void][System.Management.Automation.Language.Parser]::ParseFile((Join-Path $repoRoot 'publish.ps1'), [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count -gt 0) { throw "Publisher script has PowerShell syntax errors: $($parseErrors[0].Message)" }

$settingsIndex = $bootstrap.IndexOf('AppSettings.LoadOrCreate()', [StringComparison]::Ordinal)
$historyIndex = $bootstrap.IndexOf('new SqliteRideHistoryRepository', [StringComparison]::Ordinal)
$guardIndex = $bootstrap.IndexOf('UserDataIsolationGuard.TryReadRequiredRoot', [StringComparison]::Ordinal)
if ($guardIndex -lt 0 -or $settingsIndex -le $guardIndex -or $historyIndex -le $guardIndex -or
    $bootstrap.IndexOf('OS.GetUserDataDir()', $guardIndex, [StringComparison]::Ordinal) -lt $guardIndex -or
    $bootstrap.IndexOf('OS.GetUserDataDir()', $guardIndex, [StringComparison]::Ordinal) -gt $settingsIndex -or
    $bootstrap -notmatch 'Environment\.Exit\(41\)') {
    throw 'The user-data guard must resolve and validate user:// before settings/history initialization.'
}
if ($guard -notmatch 'RequiredRootArgument' -or $guard -notmatch 'PathComparison' -or
    $guard -notmatch 'Path.DirectorySeparatorChar') {
    throw 'The bootstrap guard must use an explicit argument and a separator-bounded path comparison.'
}

$root = Join-Path ([System.IO.Path]::GetTempPath()) ('WodraPublisherIsolation-' + [guid]::NewGuid().ToString('N'))
$isolation = New-WodraSmokeIsolation -Root $root
New-Item -ItemType Directory -Path $isolation.Root, $isolation.AppData,
    $isolation.LocalAppData, $isolation.Temp | Out-Null
$original = @{
    APPDATA = [Environment]::GetEnvironmentVariable('APPDATA', 'Process')
    LOCALAPPDATA = [Environment]::GetEnvironmentVariable('LOCALAPPDATA', 'Process')
    TEMP = [Environment]::GetEnvironmentVariable('TEMP', 'Process')
    TMP = [Environment]::GetEnvironmentVariable('TMP', 'Process')
}
try {
    $observed = Invoke-WithWodraSmokeIsolation -Isolation $isolation -Action {
        param($directories)
        [pscustomobject]@{
            APPDATA = [Environment]::GetEnvironmentVariable('APPDATA', 'Process')
            LOCALAPPDATA = [Environment]::GetEnvironmentVariable('LOCALAPPDATA', 'Process')
            TEMP = [Environment]::GetEnvironmentVariable('TEMP', 'Process')
            TMP = [Environment]::GetEnvironmentVariable('TMP', 'Process')
            Root = $directories.Root
        }
    }
    if ($observed.APPDATA -ne $isolation.AppData -or $observed.LOCALAPPDATA -ne $isolation.LocalAppData -or
        $observed.TEMP -ne $isolation.Temp -or $observed.TMP -ne $isolation.Temp) {
        throw 'Publisher child environment did not use the dedicated disposable paths.'
    }
    $childPath = Join-Path $root 'read-child-environment.ps1'
    $childOutputPath = Join-Path $root 'child-environment.txt'
    @'
"$env:APPDATA`n$env:LOCALAPPDATA`n$env:TEMP`n$env:TMP" | Set-Content -LiteralPath $args[0]
'@ | Set-Content -LiteralPath $childPath -Encoding UTF8
    Invoke-WithWodraSmokeIsolation -Isolation $isolation -Action {
        param($directories)
        $child = Start-Process -FilePath (Get-Command pwsh -ErrorAction Stop).Source `
            -ArgumentList @('-NoProfile', '-File', $childPath, $childOutputPath) -PassThru -Wait -WindowStyle Hidden
        if ($child.ExitCode -ne 0) { throw 'The child process inheritance check failed.' }
        $child.Dispose()
    }
    $childValues = @(Get-Content -LiteralPath $childOutputPath)
    if ($childValues.Count -ne 4 -or $childValues[0] -ne $isolation.AppData -or
        $childValues[1] -ne $isolation.LocalAppData -or $childValues[2] -ne $isolation.Temp -or
        $childValues[3] -ne $isolation.Temp) {
        throw 'A spawned process did not inherit all disposable user-data environment overrides.'
    }
    if (-not (Test-WodraPathInside -Path (Join-Path $isolation.UserDataRoot 'Godot\app_userdata\Wodra') -Root $isolation.UserDataRoot) -or
        (Test-WodraPathInside -Path ($isolation.UserDataRoot + '-owner') -Root $isolation.UserDataRoot)) {
        throw 'Publisher path containment accepted a root escape or rejected a child path.'
    }
    if ([Environment]::GetEnvironmentVariable('APPDATA', 'Process') -ne $original.APPDATA -or
        [Environment]::GetEnvironmentVariable('LOCALAPPDATA', 'Process') -ne $original.LOCALAPPDATA -or
        [Environment]::GetEnvironmentVariable('TEMP', 'Process') -ne $original.TEMP -or
        [Environment]::GetEnvironmentVariable('TMP', 'Process') -ne $original.TMP) {
        throw 'Publisher smoke isolation did not restore the packaging process environment.'
    }
}
finally {
    Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Output 'PASS: extracted-app smoke uses unique disposable APPDATA/LOCALAPPDATA/TEMP and restores the publisher environment'
Write-Output 'PASS: packaged startup requires and verifies separator-bounded user:// containment before settings/history services'
