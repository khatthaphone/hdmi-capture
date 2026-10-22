# Creates (once) the self-signed certificate that signs sideloaded dev MSIX builds (make win-package)
# and prints its thumbprint. MSIX signing reads it from Cert:\CurrentUser\My; $Path keeps a PFX copy.
# The subject must match Publisher in src/HDMICaptain.App/Package.appxmanifest.
param([Parameter(Mandatory)][string]$Path)

$ErrorActionPreference = 'Stop'
$cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object Subject -eq 'CN=HDMI Captain Dev' | Select-Object -First 1
if (-not $cert) {
    $cert = New-SelfSignedCertificate -Type Custom -Subject 'CN=HDMI Captain Dev' -KeyUsage DigitalSignature `
        -FriendlyName 'HDMI Captain Dev' -CertStoreLocation Cert:\CurrentUser\My `
        -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')
}
if (-not (Test-Path $Path)) {
    New-Item -ItemType Directory -Force (Split-Path $Path) | Out-Null
    $password = ConvertTo-SecureString 'dev' -AsPlainText -Force
    Export-PfxCertificate -Cert $cert -FilePath $Path -Password $password | Out-Null
    [Console]::Error.WriteLine("Created $Path. Trust it once from an elevated prompt:")
    [Console]::Error.WriteLine("  Import-PfxCertificate -FilePath '$Path' -CertStoreLocation Cert:\LocalMachine\TrustedPeople -Password (ConvertTo-SecureString dev -AsPlainText -Force)")
}
$cert.Thumbprint
