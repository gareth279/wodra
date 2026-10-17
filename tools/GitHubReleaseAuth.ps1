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

function Invoke-GitHubUploadProcess {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(Mandatory = $true)][string[]]$ArgumentList,
        [Parameter(Mandatory = $true)][string]$WorkingDirectory,
        [Parameter(Mandatory = $true)][string]$LogPath,
        [Parameter(Mandatory = $true)][int]$TimeoutSeconds,
        [Parameter(Mandatory = $true)][string]$Credential
    )

    $quoteArgument = {
        param([string]$Value)
        if ($Value.Length -gt 0 -and $Value -notmatch '[\s"]') { return $Value }
        $builder = [System.Text.StringBuilder]::new()
        [void]$builder.Append('"')
        $slashes = 0
        foreach ($character in $Value.ToCharArray()) {
            if ($character -eq '\') { $slashes++; continue }
            if ($character -eq '"') {
                [void]$builder.Append(('\' * (($slashes * 2) + 1))).Append('"')
                $slashes = 0
                continue
            }
            if ($slashes -gt 0) { [void]$builder.Append(('\' * $slashes)); $slashes = 0 }
            [void]$builder.Append($character)
        }
        if ($slashes -gt 0) { [void]$builder.Append(('\' * ($slashes * 2))) }
        [void]$builder.Append('"')
        return $builder.ToString()
    }
    $encodedArguments = (@($ArgumentList | ForEach-Object { & $quoteArgument $_ }) -join ' ')
    $startInfo = New-Object System.Diagnostics.ProcessStartInfo
    $startInfo.FileName = $FilePath
    $startInfo.Arguments = $encodedArguments
    $startInfo.WorkingDirectory = $WorkingDirectory
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    # Give the selected credential only to the upload child; remove conflicting inherited GitHub token variables.
    [void]$startInfo.EnvironmentVariables.Remove('GH_TOKEN')
    [void]$startInfo.EnvironmentVariables.Remove('GITHUB_TOKEN')
    [void]$startInfo.EnvironmentVariables.Remove('GH_ENTERPRISE_TOKEN')
    $startInfo.EnvironmentVariables['VPK_TOKEN'] = $Credential

    $process = New-Object System.Diagnostics.Process
    $process.StartInfo = $startInfo
    $timedOut = $false
    try {
        if (-not $process.Start()) { throw 'Could not start the Velopack GitHub upload process.' }
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            $timedOut = $true
            try { $process.Kill(); [void]$process.WaitForExit(15000) } catch { }
        }
        $stdout = $stdoutTask.GetAwaiter().GetResult()
        $stderr = $stderrTask.GetAwaiter().GetResult()
        if ($timedOut) {
            Set-Content -LiteralPath $LogPath -Value "Timed out after $TimeoutSeconds seconds; raw process output was not written."
            throw "Velopack GitHub upload timed out after $TimeoutSeconds seconds. No process output was written to logs."
        }
        if ($process.ExitCode -ne 0) {
            $kind = Get-GitHubReleaseAuthFailureKind ("$stdout`n$stderr")
            Set-Content -LiteralPath $LogPath -Value "Failed with exit code $($process.ExitCode); category=$kind; raw process output was not written."
            if ($kind -ne 'cli') { throw "Velopack GitHub upload failed. $(Get-GitHubReleaseAuthFailureMessage $kind)" }
            throw "Velopack GitHub upload failed with exit code $($process.ExitCode); see the sanitized status log at $LogPath."
        }
        Set-Content -LiteralPath $LogPath -Value 'Completed successfully; raw process output was not written.'
        Write-Host 'Velopack GitHub upload completed.'
    }
    finally {
        $stdout = $null
        $stderr = $null
        $stdoutTask = $null
        $stderrTask = $null
        if ($null -ne $process) { $process.Dispose() }
        [void]$startInfo.EnvironmentVariables.Remove('VPK_TOKEN')
    }
}
