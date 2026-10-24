param([string]$PhpExe = 'php')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$port = 8799
$tempRoot = Join-Path $env:TEMP ('wodra-companion-test-' + [Guid]::NewGuid().ToString('N'))
$storage = Join-Path $tempRoot 'private'
$config = Join-Path $tempRoot 'config.php'
$log = Join-Path $tempRoot 'server.log'
$errorLog = Join-Path $tempRoot 'server-error.log'
New-Item -ItemType Directory -Path $tempRoot | Out-Null
$phpStorage = $storage.Replace('\','\\').Replace("'","\'")
$configText = "<?php`nreturn ['origin'=>'http://127.0.0.1:$port','storage'=>'$phpStorage','invite_ttl_seconds'=>180,'pair_ttl_seconds'=>1800,'allow_insecure_local'=>true];`n"
[IO.File]::WriteAllText($config, $configText)
$env:WODRA_COMPANION_CONFIG = $config
$env:WODRA_COMPANION_BASE = "http://127.0.0.1:$port"
$env:WODRA_COMPANION_TEST_STORAGE = $storage
$server = Start-Process -FilePath $PhpExe -ArgumentList @('-S',"127.0.0.1:$port",'-t',(Join-Path $root 'public')) -PassThru -WindowStyle Hidden -RedirectStandardOutput $log -RedirectStandardError $errorLog
try {
    $ready = $false
    for ($i=0; $i -lt 50; $i++) {
        try { Invoke-WebRequest -Uri "$($env:WODRA_COMPANION_BASE)/" -TimeoutSec 2 | Out-Null; $ready=$true; break } catch { Start-Sleep -Milliseconds 100 }
    }
    if (-not $ready) { throw 'PHP development server did not start.' }
    & $PhpExe (Join-Path $PSScriptRoot 'run.php')
    if ($LASTEXITCODE -ne 0) { throw "Relay test suite failed with exit $LASTEXITCODE." }
} finally {
    if (-not $server.HasExited) { Stop-Process -Id $server.Id -Force }
    Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item Env:WODRA_COMPANION_CONFIG,Env:WODRA_COMPANION_BASE,Env:WODRA_COMPANION_TEST_STORAGE -ErrorAction SilentlyContinue
}
