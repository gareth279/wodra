function Resolve-GitHubReleaseCredential {
    [CmdletBinding()]
    param(
        [AllowNull()][string]$VpkToken,
        [AllowNull()][string]$GhToken,
        [Parameter()][scriptblock]$SavedCredentialProvider
    )

    if (-not [string]::IsNullOrWhiteSpace($VpkToken)) {
        return [pscustomobject]@{ Token = $VpkToken; Source = 'VPK_TOKEN' }
    }
    if (-not [string]::IsNullOrWhiteSpace($GhToken)) {
        return [pscustomobject]@{ Token = $GhToken; Source = 'GH_TOKEN' }
    }
    if ($null -eq $SavedCredentialProvider) {
        throw 'No explicit GitHub credential or saved GitHub CLI credential provider is available.'
    }

    $savedToken = [string](& $SavedCredentialProvider)
    if ([string]::IsNullOrWhiteSpace($savedToken)) {
        throw 'GitHub CLI did not return a saved github.com credential.'
    }
    return [pscustomobject]@{ Token = $savedToken.Trim(); Source = 'saved gh authentication' }
}

function Get-GitHubReleaseAuthFailureKind {
    [CmdletBinding()]
    param([AllowNull()][string]$Diagnostic)

    if ($Diagnostic -match '(?i)\b401\b|bad credentials|invalid token|not logged in|authentication required|requires authentication') {
        return 'credential'
    }
    if ($Diagnostic -match '(?i)\b403\b|resource not accessible|insufficient permission|not authorized|permission denied') {
        return 'permission'
    }
    if ($Diagnostic -match '(?i)proxy|connect(?:ion)?|resolve|DNS|TLS|timed? ?out|network|name resolution|could not reach|unable to access') {
        return 'connectivity'
    }
    return 'cli'
}

function Get-GitHubReleaseAuthFailureMessage {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][ValidateSet('credential', 'permission', 'connectivity', 'cli')][string]$Kind)

    switch ($Kind) {
        'credential' { return 'GitHub rejected the selected credential. Refresh the existing github.com login or replace the explicit release credential.' }
        'permission' { return 'The selected GitHub credential is valid but lacks repository write permission for release publication.' }
        'connectivity' { return 'GitHub could not be reached through the current network/proxy path; credential validity was not established.' }
        default { return 'GitHub CLI could not complete the release authorization preflight. Check the installed gh CLI and its github.com configuration.' }
    }
}
