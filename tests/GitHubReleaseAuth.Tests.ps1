$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\tools\GitHubReleaseAuth.ps1')

$saved = Resolve-GitHubReleaseCredential -VpkToken '' -GhToken '' -SavedCredentialProvider { 'saved-test-value' }
if ($saved.Source -ne 'saved gh authentication' -or $saved.Token -ne 'saved-test-value') {
    throw 'Saved GitHub CLI credential selection did not work.'
}
$explicitGh = Resolve-GitHubReleaseCredential -VpkToken '' -GhToken 'gh-test-value' -SavedCredentialProvider { throw 'Provider must not run' }
if ($explicitGh.Source -ne 'GH_TOKEN' -or $explicitGh.Token -ne 'gh-test-value') { throw 'GH_TOKEN precedence failed.' }
$explicitVpk = Resolve-GitHubReleaseCredential -VpkToken 'vpk-test-value' -GhToken 'gh-test-value' -SavedCredentialProvider { throw 'Provider must not run' }
if ($explicitVpk.Source -ne 'VPK_TOKEN' -or $explicitVpk.Token -ne 'vpk-test-value') { throw 'VPK_TOKEN precedence failed.' }

$cases = @(
    @{ Input = 'HTTP 401: Bad credentials'; Expected = 'credential' },
    @{ Input = 'HTTP 403: Resource not accessible by integration'; Expected = 'permission' },
    @{ Input = 'proxy connection timed out'; Expected = 'connectivity' },
    @{ Input = 'unknown gh failure'; Expected = 'cli' }
)
foreach ($case in $cases) {
    if ((Get-GitHubReleaseAuthFailureKind $case.Input) -ne $case.Expected) {
        throw "GitHub authentication diagnostic classification failed for $($case.Expected)."
    }
    $safeMessage = Get-GitHubReleaseAuthFailureMessage $case.Expected
    if ($safeMessage -match 'test-value|Bad credentials|Resource not accessible|proxy connection') {
        throw 'Safe release diagnostics exposed raw command output or credential-like test values.'
    }
}

$testRoot = Join-Path $env:TEMP ('Wodra-GitHubAuth-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
$childScript = Join-Path $testRoot 'fake-upload-child.ps1'
$childLog = Join-Path $testRoot 'child-status.log'
$sentinel = 'non-production-release-credential-sentinel'
$previousVpk = $env:VPK_TOKEN
$previousGh = $env:GH_TOKEN
$previousGithub = $env:GITHUB_TOKEN
@'
[Console]::WriteLine($env:VPK_TOKEN)
if ($env:VPK_TOKEN -eq 'non-production-release-credential-sentinel' -and
    [string]::IsNullOrEmpty($env:GH_TOKEN) -and [string]::IsNullOrEmpty($env:GITHUB_TOKEN)) { exit 42 }
exit 43
'@ | Set-Content -LiteralPath $childScript -Encoding UTF8
try {
    $env:VPK_TOKEN = 'parent-vpk-value'
    $env:GH_TOKEN = 'parent-gh-value'
    $env:GITHUB_TOKEN = 'parent-github-value'
    $powerShell = (Get-Command powershell -ErrorAction Stop).Source
    try {
        Invoke-GitHubUploadProcess -FilePath $powerShell `
            -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $childScript) `
            -WorkingDirectory $testRoot -LogPath $childLog -TimeoutSeconds 20 -Credential $sentinel
        throw 'The simulated upload child was expected to exit with its sentinel result code.'
    }
    catch {
        if ($_.Exception.Message -match [regex]::Escape($sentinel)) { throw 'The simulated credential appeared in an exception.' }
    }
    $statusLog = Get-Content -LiteralPath $childLog -Raw
    if ($statusLog -match [regex]::Escape($sentinel) -or $statusLog -notmatch 'exit code 42') {
        throw 'The upload status log did not safely record child status or exposed the sentinel.'
    }
    if ($env:VPK_TOKEN -ne 'parent-vpk-value' -or $env:GH_TOKEN -ne 'parent-gh-value' -or
        $env:GITHUB_TOKEN -ne 'parent-github-value') {
        throw 'The upload helper changed the parent PowerShell environment.'
    }
}
finally {
    if ($null -eq $previousVpk) { Remove-Item Env:VPK_TOKEN -ErrorAction SilentlyContinue } else { $env:VPK_TOKEN = $previousVpk }
    if ($null -eq $previousGh) { Remove-Item Env:GH_TOKEN -ErrorAction SilentlyContinue } else { $env:GH_TOKEN = $previousGh }
    if ($null -eq $previousGithub) { Remove-Item Env:GITHUB_TOKEN -ErrorAction SilentlyContinue } else { $env:GITHUB_TOKEN = $previousGithub }
    Remove-Item -LiteralPath $testRoot -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Output 'PASS: explicit and saved credential selection, safe auth error classification'
Write-Output 'PASS: upload-child token scope and safe status logging'
