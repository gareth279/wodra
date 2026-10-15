[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $AntSdkRoot,

    [string] $OutputDirectory = (Join-Path $PSScriptRoot '..\..\src\IndoorCycling.Trainer.Windows\AntSdk')
)

$ErrorActionPreference = 'Stop'
$AntSdkRoot = [IO.Path]::GetFullPath($AntSdkRoot)
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$required = @(
    'ANT_LIB\ANT_Lib.vcxproj',
    'ANT_Unmanaged_Wrapper\ANT_Unmanaged_Wrapper.vcxproj',
    'ANT_Managed_Library'
)
foreach ($relativePath in $required) {
    if (-not (Test-Path -LiteralPath (Join-Path $AntSdkRoot $relativePath))) {
        throw "ANT PC SDK 3.5 is incomplete: missing $relativePath"
    }
}

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path -LiteralPath $vswhere)) { throw 'Visual Studio Build Tools with the C++ workload are required.' }
$installationPath = (& $vswhere -latest -products '*' -property installationPath | Select-Object -First 1)
if (-not $installationPath) { throw 'Visual Studio Build Tools were not found.' }
$msbuild = Join-Path $installationPath 'MSBuild\Current\Bin\MSBuild.exe'
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $msbuild) -or -not (Test-Path -LiteralPath $csc)) {
    throw 'MSBuild and the x64 .NET Framework compiler are required.'
}

$tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('WodraAntSdkBuild-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tempRoot | Out-Null
Get-ChildItem -LiteralPath $AntSdkRoot -Force | Copy-Item -Destination $tempRoot -Recurse -Force

$nativeProjects = @(
    (Join-Path $tempRoot 'ANT_LIB\ANT_Lib.vcxproj'),
    (Join-Path $tempRoot 'ANT_Unmanaged_Wrapper\ANT_Unmanaged_Wrapper.vcxproj')
)
foreach ($project in $nativeProjects) {
    $xml = [IO.File]::ReadAllText($project)
    $xml = $xml.Replace('Debug|Win32', 'Debug|x64').Replace('Release|Win32', 'Release|x64')
    $xml = $xml.Replace('<Platform>Win32</Platform>', '<Platform>x64</Platform>')
    $xml = $xml.Replace('<TargetMachine>MachineX86</TargetMachine>', '<TargetMachine>MachineX64</TargetMachine>')
    $xml = $xml.Replace('<PlatformToolset>v120</PlatformToolset>', '<PlatformToolset>v143</PlatformToolset>')
    $xml = $xml.Replace('<RuntimeLibrary>MultiThreadedDebugDLL</RuntimeLibrary>', '<RuntimeLibrary>MultiThreadedDebug</RuntimeLibrary>')
    $xml = $xml.Replace('<RuntimeLibrary>MultiThreadedDLL</RuntimeLibrary>', '<RuntimeLibrary>MultiThreaded</RuntimeLibrary>')
    [IO.File]::WriteAllText($project, $xml)
}

$solutionDirectory = $tempRoot + '\'
foreach ($project in $nativeProjects) {
    & $msbuild $project /m /t:Rebuild /p:Configuration=Release /p:Platform=x64 "/p:SolutionDir=$solutionDirectory" /v:minimal
    if ($LASTEXITCODE -ne 0) { throw "x64 ANT native build failed for $project (exit $LASTEXITCODE)." }
}

$managedSource = Join-Path $tempRoot 'ANT_Managed_Library'
$managedFiles = @(Get-ChildItem -LiteralPath $managedSource -Recurse -File -Filter '*.cs' | ForEach-Object FullName)
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$managedOutput = Join-Path $OutputDirectory 'ANT_NET.dll'
$compilerArguments = @('/nologo', '/target:library', '/platform:anycpu', "/out:$managedOutput") + $managedFiles
& $csc @compilerArguments
if ($LASTEXITCODE -ne 0) { throw "ANT managed library build failed (exit $LASTEXITCODE)." }

Copy-Item -LiteralPath (Join-Path $tempRoot 'Release\ANT_WrappedLib.dll') -Destination $OutputDirectory -Force
Copy-Item -LiteralPath (Join-Path $tempRoot 'License.txt') -Destination (Join-Path $OutputDirectory 'ANT-SDK-License.txt') -Force

$native = Join-Path $OutputDirectory 'ANT_WrappedLib.dll'
$bytes = [IO.File]::ReadAllBytes($native)
$peOffset = [BitConverter]::ToInt32($bytes, 0x3c)
$machine = [BitConverter]::ToUInt16($bytes, $peOffset + 4)
if ($machine -ne 0x8664) { throw 'The generated ANT_WrappedLib.dll is not x64.' }
Write-Host "Built x64 ANT SDK libraries in $OutputDirectory"
Write-Host "Temporary source copy retained at $tempRoot"
