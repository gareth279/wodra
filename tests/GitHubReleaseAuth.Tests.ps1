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

Write-Output 'PASS: explicit and saved credential selection, safe auth error classification'
