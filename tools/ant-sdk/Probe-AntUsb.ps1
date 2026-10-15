param(
    [string]$SdkDirectory = (Join-Path $PSScriptRoot '..\..\src\IndoorCycling.Trainer.Windows\AntSdk')
)

$ErrorActionPreference = 'Stop'
$SdkDirectory = [System.IO.Path]::GetFullPath($SdkDirectory)
$managedPath = Join-Path $SdkDirectory 'ANT_NET.dll'
$wrappedPath = Join-Path $SdkDirectory 'ANT_WrappedLib.dll'

Write-Output 'ANTUSB read-only probe (does not configure a network or transmit trainer commands)'
Write-Output ("Host context: {0}; OS: {1}; process: {2}-bit" -f $env:COMPUTERNAME,
    [Environment]::OSVersion.Version, ([IntPtr]::Size * 8))
foreach ($path in @($managedPath, $wrappedPath)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "ANT SDK dependency is missing: $([System.IO.Path]::GetFileName($path))"
    }
    Write-Output ("SDK file present: {0}" -f [System.IO.Path]::GetFileName($path))
}

try {
    $pnpMatches = @(Get-PnpDevice -PresentOnly -ErrorAction Stop | Where-Object {
        $_.InstanceId -match 'VID_0FCF&PID_1009'
    })
    if ($pnpMatches.Count -eq 0) {
        Write-Output 'Windows PnP status: no matching instance was returned (this alone does not establish that the device is absent).'
    } else {
        foreach ($device in $pnpMatches) {
            Write-Output ("Windows PnP: status={0}; class={1}; problem={2}; device={3}" -f
                $device.Status, $device.Class, $device.Problem, $device.FriendlyName)
        }
    }
} catch {
    Write-Output ("Windows PnP status unavailable: {0}" -f $_.Exception.GetType().Name)
}

$originalLocation = Get-Location
$originalDirectory = [Environment]::CurrentDirectory
Set-Location -LiteralPath $SdkDirectory
[Environment]::CurrentDirectory = $SdkDirectory
$assembly = [Reflection.Assembly]::LoadFrom($managedPath)
$commonType = $assembly.GetType('ANT_Managed_Library.ANT_Common', $true)
$deviceType = $assembly.GetType('ANT_Managed_Library.ANT_Device', $true)
$stopwatch = [Diagnostics.Stopwatch]::StartNew()
$countMethod = $commonType.GetMethod('getNumDetectedUSBDevices', [Reflection.BindingFlags]'Public,Static')
try {
    $deviceCount = [uint32]$countMethod.Invoke($null, @())
    Write-Output ("ANT SDK USB enumeration API loaded and returned count={0} ({1} ms)" -f $deviceCount, $stopwatch.ElapsedMilliseconds)
} catch {
    $cause = $_.Exception.GetBaseException()
    Write-Output ("ANT SDK USB enumeration API failed: {0}; {1}" -f $cause.GetType().Name, $cause.Message)
    Set-Location -LiteralPath $originalLocation
    [Environment]::CurrentDirectory = $originalDirectory
    exit 4
}
if ($deviceCount -eq 0) {
    Write-Output 'Probe stopped at stage 1: no compatible USB device was enumerated by the ANT SDK.'
    Set-Location -LiteralPath $originalLocation
    [Environment]::CurrentDirectory = $originalDirectory
    exit 2
}

$openTimer = [Diagnostics.Stopwatch]::StartNew()
$device = $null
try {
    $constructor = $deviceType.GetConstructor([Type[]]@([byte], [uint32]))
    $device = $constructor.Invoke([object[]]@([byte]0, [uint32]57600))
    Write-Output ("ANT SDK open: succeeded ({0} ms)" -f $openTimer.ElapsedMilliseconds)
} catch {
    Write-Output ("ANT SDK open: failed ({0}; {1})" -f $_.Exception.GetBaseException().GetType().Name,
        $_.Exception.GetBaseException().Message)
    Set-Location -LiteralPath $originalLocation
    [Environment]::CurrentDirectory = $originalDirectory
    exit 3
} finally {
    if ($null -ne $device) {
        $release = $deviceType.GetMethod('shutdownDeviceInstance', [Reflection.BindingFlags]'Public,Static')
        $arguments = [object[]]@($device)
        $release.Invoke($null, $arguments)
        Write-Output 'ANT SDK handle released.'
    }
}
Set-Location -LiteralPath $originalLocation
[Environment]::CurrentDirectory = $originalDirectory

Write-Output 'Stage 1 passed: ANT SDK enumeration, device open, and clean release succeeded.'
Write-Output 'This probe does not test network assignment, FE-C discovery, trainer telemetry, or control.'
