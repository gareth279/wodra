function Assert-WodraReleaseVersion {
    param([Parameter(Mandatory)][ValidateSet('alpha', 'stable')][string]$Channel,
          [Parameter(Mandatory)][string]$Version)
    $pattern = if ($Channel -eq 'alpha') { '^\d+\.\d+\.\d+-alpha\.\d+$' } else { '^\d+\.\d+\.\d+$' }
    if ($Version -notmatch $pattern) {
        throw "Version '$Version' is invalid for channel '$Channel'. Alpha requires MAJOR.MINOR.PATCH-alpha.N; Stable requires MAJOR.MINOR.PATCH."
    }
}

function Get-WodraNextAlphaVersion {
    param([Parameter(Mandatory)][string]$BaseVersion, [Parameter(Mandatory)][ValidateRange(0, 2147483647)][int]$CommitDistance)
    if ($BaseVersion -match '^(\d+\.\d+\.\d+)-alpha\.(\d+)$') {
        return "$($Matches[1])-alpha.$([int]$Matches[2] + $CommitDistance)"
    }
    if ($BaseVersion -match '^(\d+)\.(\d+)\.(\d+)$') {
        $alphaNumber = [Math]::Max(1, $CommitDistance)
        return "$($Matches[1]).$($Matches[2]).$([int]$Matches[3] + 1)-alpha.$alphaNumber"
    }
    throw "Cannot derive the next Alpha version from '$BaseVersion'."
}

function Assert-WodraStablePromotionVersion {
    param([Parameter(Mandatory)][string]$StableVersion, [Parameter(Mandatory)][string]$SourceAlphaTag)
    $version = $SourceAlphaTag -replace '^v', ''
    if ($version -notmatch '^(\d+\.\d+\.\d+)-alpha\.\d+$' -or $Matches[1] -ne $StableVersion) {
        throw "Stable version '$StableVersion' must be the SemVer release corresponding to source candidate '$SourceAlphaTag'."
    }
}

function Get-WodraStableRouteIds {
    @(
        'dunedin-one-way-oval-gardens',
        'dunedin-south-coastal-loop',
        'dunedin-rolling-loop',
        'dunedin-signal-hill-climbing-loop',
        'otago-central-rail-trail-clyde-alexandra',
        'signal-hill-big-easy-mtb',
        'wodra-xc-skills-loop'
    )
}
