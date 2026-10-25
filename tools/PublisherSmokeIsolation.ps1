function New-WodraSmokeIsolation {
    param([Parameter(Mandatory = $true)][string] $Root)

    $fullRoot = [System.IO.Path]::GetFullPath($Root)
    [pscustomobject]@{
        Root = $fullRoot
        UserDataRoot = $fullRoot
        AppData = Join-Path $fullRoot 'appdata'
        LocalAppData = Join-Path $fullRoot 'localappdata'
        Temp = Join-Path $fullRoot 'temp'
    }
}

function Invoke-WithWodraSmokeIsolation {
    param(
        [Parameter(Mandatory = $true)] $Isolation,
        [Parameter(Mandatory = $true)][scriptblock] $Action
    )

    $overrides = @{
        APPDATA = $Isolation.AppData
        LOCALAPPDATA = $Isolation.LocalAppData
        TEMP = $Isolation.Temp
        TMP = $Isolation.Temp
    }
    $previous = @{}
    foreach ($name in $overrides.Keys) {
        $previous[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
    }
    try {
        foreach ($name in $overrides.Keys) {
            [Environment]::SetEnvironmentVariable($name, $overrides[$name], 'Process')
        }
        & $Action $Isolation
    }
    finally {
        foreach ($name in $overrides.Keys) {
            [Environment]::SetEnvironmentVariable($name, $previous[$name], 'Process')
        }
    }
}

function Test-WodraPathInside {
    param([Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)][string] $Root)

    $actual = [System.IO.Path]::GetFullPath($Path).TrimEnd([char[]]@('\', '/'))
    $fullRoot = [System.IO.Path]::GetFullPath($Root).TrimEnd([char[]]@('\', '/'))
    $prefix = $fullRoot + [System.IO.Path]::DirectorySeparatorChar
    return $actual.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)
}
