$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\tools\ReleaseChannelPolicy.ps1')

Assert-WodraReleaseVersion -Channel alpha -Version '0.1.0-alpha.94'
Assert-WodraReleaseVersion -Channel stable -Version '0.1.0'
foreach ($case in @(
    @{ Channel = 'alpha'; Version = '0.1.0' },
    @{ Channel = 'stable'; Version = '0.1.0-alpha.1' },
    @{ Channel = 'stable'; Version = '0.1.0-beta.1' }
)) {
    $rejected = $false
    try { Assert-WodraReleaseVersion -Channel $case.Channel -Version $case.Version } catch { $rejected = $true }
    if (-not $rejected) { throw "Invalid version was accepted: $($case.Channel) $($case.Version)" }
}
if ((Get-WodraNextAlphaVersion '0.1.0-alpha.94' 1) -ne '0.1.0-alpha.95') { throw 'Alpha continuation did not increment the prerelease ordinal.' }
if ((Get-WodraNextAlphaVersion '0.1.0' 1) -ne '0.1.1-alpha.1') { throw 'Stable promotion did not advance normal Alpha work to the next patch series.' }
if ((Get-WodraNextAlphaVersion '0.1.0' 3) -ne '0.1.1-alpha.3') { throw 'Alpha version derivation did not preserve commit distance.' }
Assert-WodraStablePromotionVersion -StableVersion '0.1.0' -SourceAlphaTag 'v0.1.0-alpha.94'
Assert-WodraPromotionCandidateVersion -StableVersion '0.1.0' -SourceAlphaTag 'v0.1.0-alpha.95'
$obsoleteFirstStableCandidateRejected = $false
try { Assert-WodraPromotionCandidateVersion -StableVersion '0.1.0' -SourceAlphaTag 'v0.1.0-alpha.94' } catch { $obsoleteFirstStableCandidateRejected = $true }
if (-not $obsoleteFirstStableCandidateRejected) { throw 'The first Stable promotion accepted the obsolete Alpha.94 source.' }
Assert-WodraPromotionCandidateVersion -StableVersion '0.1.1' -SourceAlphaTag 'v0.1.1-alpha.1'
$badPromotionRejected = $false
try { Assert-WodraStablePromotionVersion -StableVersion '0.1.1' -SourceAlphaTag 'v0.1.0-alpha.94' } catch { $badPromotionRejected = $true }
if (-not $badPromotionRejected) { throw 'Stable promotion accepted a version outside the candidate prerelease series.' }
$promotionWorkflow = Get-Content (Join-Path $PSScriptRoot '..\.github\workflows\windows-stable-promotion.yml') -Raw
foreach ($requiredGate in @(
    'Assert-WodraPromotionCandidateVersion',
    'tools/ReleaseChannelPolicy.ps1',
    'UpdateChannelSelector',
    'environment: stable-release',
    'refs/heads/main'
)) {
    if ($promotionWorkflow -notmatch [regex]::Escape($requiredGate)) {
        throw "Stable promotion workflow is missing required trusted-source/owner gate '$requiredGate'."
    }
}
Write-Host 'PASS release channel version policy'
