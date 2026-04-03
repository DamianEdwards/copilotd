[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$BinaryPath,

    [string]$InstallerScriptPath = (Join-Path $PSScriptRoot 'install\install-copilotd.ps1')
)

$ErrorActionPreference = 'Stop'

$binaryPath = [System.IO.Path]::GetFullPath($BinaryPath)
$installerScriptPath = [System.IO.Path]::GetFullPath($InstallerScriptPath)

if (-not (Test-Path $binaryPath))
{
    throw "Signed binary '$binaryPath' was not found."
}

if (-not (Test-Path $installerScriptPath))
{
    throw "Installer script '$installerScriptPath' was not found."
}

Write-Verbose "Loading installer trust helpers from '$installerScriptPath'."
. $installerScriptPath -NoExecute

$config = Get-CopilotdInstallerTrustConfiguration
$expectedThumbprints = @($config.ExpectedSignerIssuerSha512Thumbprints)
$expectedParentThumbprints = @($config.ExpectedSignerParentIssuerSha512Thumbprints)
$evidence = Get-WindowsBinaryTrustEvidence -BinaryPath $binaryPath

$issuerTrustMatch = Assert-SignerIssuerTrust `
    -Evidence $evidence `
    -ExpectedIssuerThumbprints $expectedThumbprints `
    -ExpectedParentIssuerThumbprints $expectedParentThumbprints

$formattedExpectedThumbprints = ($expectedThumbprints | ForEach-Object { "'$_'" }) -join ', '
$formattedExpectedParentThumbprints = ($expectedParentThumbprints | ForEach-Object { "'$_'" }) -join ', '
$matchDescription = if ($issuerTrustMatch.UsedFallback)
{
    "using parent issuer fallback '$($issuerTrustMatch.Certificate.Subject)' ($($issuerTrustMatch.Sha512Thumbprint))"
}
else
{
    "using immediate issuer '$($issuerTrustMatch.Certificate.Subject)' ($($issuerTrustMatch.Sha512Thumbprint))"
}

Write-Host "Verified signer issuer chain for '$binaryPath' $matchDescription. Allowed immediate SHA512 thumbprints: $formattedExpectedThumbprints. Allowed parent SHA512 thumbprints: $formattedExpectedParentThumbprints."
