param([string]$PhpExe = 'php')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$phpVersion = & $PhpExe -r 'echo PHP_VERSION_ID;'
if ($LASTEXITCODE -ne 0 -or [int]$phpVersion -lt 80200) {
    throw "The companion relay requires PHP 8.2 or newer; syntax and integration checks need the same supported runtime. Found PHP_VERSION_ID=$phpVersion."
}
Get-ChildItem -LiteralPath (Split-Path -Parent $PSScriptRoot) -Filter '*.php' -File -Recurse | ForEach-Object {
    & $PhpExe -l $_.FullName
    if ($LASTEXITCODE -ne 0) { throw "PHP syntax check failed for $($_.FullName)." }
}
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

    # Exercise the public API's setup-failure path with no private configuration.
    # A valid protocol-2 POST must remain machine-readable and retain protocol 2.
    $probeSocket = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    $probeSocket.Start(); $probePort = ([Net.IPEndPoint]$probeSocket.LocalEndpoint).Port; $probeSocket.Stop()
    $probeBase = "http://127.0.0.1:$probePort"
    $env:WODRA_COMPANION_CONFIG = Join-Path $tempRoot 'missing-private-config.php'
    $probeServer = Start-Process -FilePath $PhpExe -ArgumentList @('-d','display_errors=1','-S',"127.0.0.1:$probePort",'-t',(Join-Path $root 'public')) `
        -PassThru -WindowStyle Hidden -RedirectStandardOutput (Join-Path $tempRoot 'unconfigured-server.log') `
        -RedirectStandardError (Join-Path $tempRoot 'unconfigured-server-error.log')
    try {
        $probeReady = $false
        for ($i=0; $i -lt 50; $i++) {
            try { Invoke-WebRequest -Uri "$probeBase/" -TimeoutSec 2 | Out-Null; $probeReady=$true; break } catch { Start-Sleep -Milliseconds 100 }
        }
        if (-not $probeReady) { throw 'Unconfigured PHP API test server did not start.' }
        $health = Invoke-WebRequest -Uri "$probeBase/api.php" -Method Get -SkipHttpErrorCheck -TimeoutSec 5
        $healthJson = $health.Content | ConvertFrom-Json
        if ($health.StatusCode -ne 405 -or $healthJson.error -ne 'method_not_allowed' -or
            $health.Headers['Content-Type'] -notmatch 'application/json' -or $health.Content -match 'PHP (Warning|Deprecated|Notice|Fatal)') {
            throw 'GET API health response must be clean JSON 405 method_not_allowed before private configuration is loaded.'
        }
        $body = '{"protocol":2,"action":"remembered_claim","code":"0000000000"}'
        $response = Invoke-WebRequest -Uri "$probeBase/api.php" -Method Post -ContentType 'application/json' -Body $body -SkipHttpErrorCheck -TimeoutSec 5
        $responseJson = $response.Content | ConvertFrom-Json
        if ($response.StatusCode -ne 503 -or $responseJson.protocol -ne 2 -or $responseJson.error -ne 'not_configured' -or
            $response.Headers['Content-Type'] -notmatch 'application/json' -or $response.Content -match 'PHP (Warning|Deprecated|Notice|Fatal)') {
            throw 'Unauthenticated protocol-2 setup probe must return clean protocol-2 JSON not_configured.'
        }
        Write-Output 'PASS: unconfigured relay returns clean JSON health and protocol-2 setup errors.'
    } finally {
        if (-not $probeServer.HasExited) { Stop-Process -Id $probeServer.Id -Force }
        $env:WODRA_COMPANION_CONFIG = $config
    }
} finally {
    if (-not $server.HasExited) { Stop-Process -Id $server.Id -Force }
    Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item Env:WODRA_COMPANION_CONFIG,Env:WODRA_COMPANION_BASE,Env:WODRA_COMPANION_TEST_STORAGE -ErrorAction SilentlyContinue
}
