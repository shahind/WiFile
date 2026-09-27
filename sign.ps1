# Authenticode-signs one file with the WiFile code-signing certificate, if one is configured.
#   WIFILE_CERT_THUMBPRINT  thumbprint of a certificate in Cert:\CurrentUser\My (e.g. a Certum/SimplySign
#                           or hardware-token certificate), or
#   WIFILE_CERT_PFX + WIFILE_CERT_PASSWORD  path to a .pfx file and its password.
# Signed files show your name as publisher and stop the "unknown publisher" SmartScreen warning
# once the certificate has reputation.
param([Parameter(Mandatory)] [string]$File)
$ErrorActionPreference = 'Stop'

$cert = $null
if ($env:WIFILE_CERT_THUMBPRINT) {
    $cert = Get-Item "Cert:\CurrentUser\My\$($env:WIFILE_CERT_THUMBPRINT)"
} elseif ($env:WIFILE_CERT_PFX) {
    # Works on Windows PowerShell 5.1 (Get-PfxCertificate has no -Password there).
    $cert = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2(
        $env:WIFILE_CERT_PFX, $env:WIFILE_CERT_PASSWORD,
        [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet)
}
if (-not $cert) { Write-Warning "No signing certificate configured; $File left unsigned."; exit 0 }

$servers = 'http://timestamp.digicert.com', 'http://timestamp.sectigo.com', 'http://time.certum.pl'
foreach ($ts in $servers) {
    $r = Set-AuthenticodeSignature -FilePath $File -Certificate $cert -TimestampServer $ts -HashAlgorithm SHA256
    $signed = $r.SignerCertificate -and $r.SignerCertificate.Thumbprint -eq $cert.Thumbprint -and $r.TimeStamperCertificate
    if ($signed) {
        if ($r.Status -ne 'Valid') { Write-Warning "Signed $File, but this PC does not trust the certificate: $($r.StatusMessage)" }
        else { Write-Host "Signed $File" }
        exit 0
    }
    Write-Warning "Signing/timestamp via $ts failed: $($r.StatusMessage)"
}
throw "Signing failed for $File"
