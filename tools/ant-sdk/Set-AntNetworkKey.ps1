$ErrorActionPreference = 'Stop'

$secureHex = Read-Host 'Enter the 8-byte ANT+ network key as 16 hexadecimal characters' -AsSecureString
$bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secureHex)
$bytes = $null
try {
    $hex = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr)
    if ($hex -notmatch '^[0-9A-Fa-f]{16}$') {
        throw 'Expected exactly 16 hexadecimal characters.'
    }
    $bytes = [byte[]]::new(8)
    for ($i = 0; $i -lt 8; $i++) {
        $bytes[$i] = [Convert]::ToByte($hex.Substring($i * 2, 2), 16)
    }

    $directory = Join-Path $env:LOCALAPPDATA 'Wodra'
    $path = Join-Path $directory 'antplus-network-key.bin'
    [IO.Directory]::CreateDirectory($directory) | Out-Null

    $identity = [Security.Principal.WindowsIdentity]::GetCurrent().User
    $directoryAcl = [Security.AccessControl.DirectorySecurity]::new()
    $directoryAcl.SetAccessRuleProtection($true, $false)
    $directoryAcl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
        $identity, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow'))
    Set-Acl -LiteralPath $directory -AclObject $directoryAcl

    [IO.File]::WriteAllBytes($path, $bytes)
    $fileAcl = [Security.AccessControl.FileSecurity]::new()
    $fileAcl.SetAccessRuleProtection($true, $false)
    $fileAcl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($identity, 'FullControl', 'Allow'))
    Set-Acl -LiteralPath $path -AclObject $fileAcl

    Write-Output "Private ANT+ key file created at $path. The key was not printed."
}
finally {
    if ($bytes) { [Array]::Clear($bytes, 0, $bytes.Length) }
    if ($bstr -ne [IntPtr]::Zero) { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
    if ($secureHex) { $secureHex.Dispose() }
}
