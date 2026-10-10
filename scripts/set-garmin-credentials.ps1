$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Security

# Get-Credential collects the password without exposing it in command arguments or chat.
$credential = Get-Credential -Message 'Garmin Connect: correo y contraseña. Se guardarán cifrados para tu usuario de Windows.'
if ($null -eq $credential) { return }
$root = Split-Path -Parent $PSScriptRoot
$privateDirectory = Join-Path $root '.local'
$destination = Join-Path $privateDirectory 'garmin-credentials.bin'
$temporary = Join-Path $privateDirectory ('credentials-' + [guid]::NewGuid().ToString('N') + '.tmp')
$plainBytes = $null
$plainRecord = $null
try {
    $emailAddress = [System.Net.Mail.MailAddress]::new($credential.UserName)
    if ($emailAddress.Address -ne $credential.UserName -or $credential.UserName.Length -gt 320) {
        throw 'Introduce el correo de Garmin como nombre de usuario.'
    }
    $plainRecord = @{ version = 1; email = $credential.UserName; password = $credential.GetNetworkCredential().Password }
    if ([string]::IsNullOrEmpty($plainRecord.password) -or $plainRecord.password.Length -gt 4096) {
        throw 'La contraseña está vacía o supera el límite admitido.'
    }
    $plainBytes = [System.Text.Encoding]::UTF8.GetBytes(($plainRecord | ConvertTo-Json -Compress))
    $cipher = [System.Security.Cryptography.ProtectedData]::Protect($plainBytes, $null, [System.Security.Cryptography.DataProtectionScope]::CurrentUser)
    New-Item -ItemType Directory -Force -Path $privateDirectory | Out-Null
    [System.IO.File]::WriteAllBytes($temporary, $cipher)
    Move-Item -LiteralPath $temporary -Destination $destination -Force
    Write-Host 'Credenciales guardadas cifradas. Ejecuta: dotnet run -- login'
} catch {
    # Never print exceptions that may contain user input.
    Write-Error 'No se guardaron las credenciales. Revisa el correo, la contraseña y los permisos de .local.'
} finally {
    if ($null -ne $plainBytes) { [Array]::Clear($plainBytes, 0, $plainBytes.Length) }
    if ($null -ne $plainRecord) { $plainRecord.password = $null }
    $credential = $null
    if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force }
}
