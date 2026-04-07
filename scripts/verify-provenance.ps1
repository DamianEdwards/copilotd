<#
.SYNOPSIS
    Verifies Authenticode provenance of a copilotd binary.

.DESCRIPTION
    This script is extracted from install-copilotd.ps1 and contains only the
    trust/certificate verification functions. It is embedded as a resource in
    the copilotd binary and extracted at runtime for self-update provenance checks.

    Trust configuration (signer subject, issuer thumbprints) is kept in sync
    with install-copilotd.ps1. CI should validate that both scripts share
    identical values.

.PARAMETER BinaryPath
    Full path to the binary to verify.

.OUTPUTS
    JSON object with verification result on stdout.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$BinaryPath
)

$ErrorActionPreference = 'Stop'

# Trust configuration — must stay in sync with install-copilotd.ps1
$ExpectedSignerSubject = 'CN=Damian Edwards, O=Damian Edwards, L=Issaquah, S=Washington, C=US'
$ExpectedSignerIssuerSha512Thumbprints = @(
    '1c93dcf4e032b19949a67722d0c25e683309fbcd36110da84129f45d8175b709ebc6ef3439596ece9eb8f2dae1967b856adc49ba74535244a8a5db5fb48fa7b9'
    '1770433e5d2c028e0bf8640a0345bdb86307e7cc2a99cfbe93acf9d960a996d1c63b2d5cf30d52e7741df4fd057ea778442f75c1b62ee2106c66333078a04e6d'
)
$ExpectedSignerParentIssuerSha512Thumbprints = @(
    '46f16bb99340f8d728c83ff093af9d4cff87811d432f92a804741144f0f3fc0aa8011b1efe0c24e0480bd6c7cb7af699077f9b8fc7ec8a40f9f7a186725224c6'
)

# --- Trust/cert functions (extracted from install-copilotd.ps1) ---

function Get-CertificateSha512Thumbprint
{
    param([Parameter(Mandatory)][System.Security.Cryptography.X509Certificates.X509Certificate2]$Certificate)

    $sha512 = [System.Security.Cryptography.SHA512]::Create()
    try
    {
        $hashBytes = $sha512.ComputeHash($Certificate.RawData)
        return ([System.BitConverter]::ToString($hashBytes)).Replace('-', '').ToLowerInvariant()
    }
    finally
    {
        $sha512.Dispose()
    }
}

function Get-ChainStatusMessages
{
    param([Parameter(Mandatory)][System.Security.Cryptography.X509Certificates.X509Chain]$Chain)

    return @($Chain.ChainStatus |
            Where-Object { $_.Status -ne [System.Security.Cryptography.X509Certificates.X509ChainStatusFlags]::NoError } |
            ForEach-Object {
                $statusText = $_.Status.ToString()
                $infoText = $_.StatusInformation.Trim()
                if ([string]::IsNullOrWhiteSpace($infoText))
                {
                    $statusText
                }
                else
                {
                    "$statusText ($infoText)"
                }
            })
}

function Write-CertificateDetailsVerbose
{
    param(
        [Parameter(Mandatory)][string]$Description,
        [System.Security.Cryptography.X509Certificates.X509Certificate2]$Certificate
    )

    if ($null -eq $Certificate)
    {
        Write-Verbose "$Description certificate: <null>"
        return
    }

    Write-Verbose ("{0} certificate: Subject='{1}', Issuer='{2}', Thumbprint='{3}', NotBefore='{4:O}', NotAfter='{5:O}'" -f `
            $Description, `
            $Certificate.Subject, `
            $Certificate.Issuer, `
            $Certificate.Thumbprint, `
            $Certificate.NotBefore, `
            $Certificate.NotAfter)
}

function Write-CertificateChainVerbose
{
    param(
        [Parameter(Mandatory)][System.Security.Cryptography.X509Certificates.X509Chain]$Chain,
        [Parameter(Mandatory)][string]$Description
    )

    Write-Verbose "$Description certificate chain contains $($Chain.ChainElements.Count) element(s)."

    for ($index = 0; $index -lt $Chain.ChainElements.Count; $index++)
    {
        Write-CertificateDetailsVerbose -Description "$Description chain[$index]" -Certificate $Chain.ChainElements[$index].Certificate
    }

    $statuses = Get-ChainStatusMessages -Chain $Chain
    if ($statuses.Count -eq 0)
    {
        Write-Verbose "$Description certificate chain status: NoError."
        return
    }

    foreach ($status in $statuses)
    {
        Write-Verbose "$Description certificate chain status: $status"
    }
}

function Get-ValidatedCertificateChain
{
    param(
        [Parameter(Mandatory)][System.Security.Cryptography.X509Certificates.X509Certificate2]$Certificate,
        [Parameter(Mandatory)][string]$Description,
        [switch]$IgnoreTimeValidity
    )

    Write-CertificateDetailsVerbose -Description $Description -Certificate $Certificate
    Write-Verbose "Building $Description certificate chain. IgnoreTimeValidity=$IgnoreTimeValidity."

    $chain = [System.Security.Cryptography.X509Certificates.X509Chain]::new()
    $chain.ChainPolicy.RevocationMode = [System.Security.Cryptography.X509Certificates.X509RevocationMode]::Online
    $chain.ChainPolicy.RevocationFlag = [System.Security.Cryptography.X509Certificates.X509RevocationFlag]::EntireChain
    $chain.ChainPolicy.UrlRetrievalTimeout = [TimeSpan]::FromSeconds(15)
    $chain.ChainPolicy.VerificationFlags = if ($IgnoreTimeValidity)
    {
        [System.Security.Cryptography.X509Certificates.X509VerificationFlags]::IgnoreNotTimeValid
    }
    else
    {
        [System.Security.Cryptography.X509Certificates.X509VerificationFlags]::NoFlag
    }

    $ok = $chain.Build($Certificate)
    if ($ok)
    {
        Write-CertificateChainVerbose -Chain $chain -Description $Description
        return $chain
    }

    $statuses = Get-ChainStatusMessages -Chain $chain
    Write-CertificateChainVerbose -Chain $chain -Description $Description
    $statusMessage = if ($statuses.Count -gt 0)
    {
        $statuses -join '; '
    }
    else
    {
        'unknown chain validation failure'
    }

    throw "$Description certificate chain validation failed: $statusMessage"
}

function Get-ImmediateIssuerCertificate
{
    param(
        [Parameter(Mandatory)][System.Security.Cryptography.X509Certificates.X509Chain]$Chain,
        [Parameter(Mandatory)][string]$Description
    )

    if ($Chain.ChainElements.Count -lt 2)
    {
        throw "$Description certificate chain did not include an issuing certificate."
    }

    $issuerCertificate = $Chain.ChainElements[1].Certificate
    if ($null -eq $issuerCertificate)
    {
        throw "$Description certificate chain did not provide an issuing certificate."
    }

    Write-CertificateDetailsVerbose -Description "$Description issuer" -Certificate $issuerCertificate
    return $issuerCertificate
}

function Get-ParentIntermediateIssuerCertificate
{
    param(
        [Parameter(Mandatory)][System.Security.Cryptography.X509Certificates.X509Chain]$Chain,
        [Parameter(Mandatory)][string]$Description
    )

    if ($Chain.ChainElements.Count -lt 3)
    {
        Write-Verbose "$Description certificate chain did not include a parent issuer fallback candidate."
        return $null
    }

    $parentIndex = 2
    $parentCertificate = $Chain.ChainElements[$parentIndex].Certificate
    if ($null -eq $parentCertificate)
    {
        Write-Verbose "$Description certificate chain did not provide a parent issuer fallback candidate."
        return $null
    }

    $isRootCandidate = ($parentIndex -eq ($Chain.ChainElements.Count - 1)) -or
        [string]::Equals($parentCertificate.Subject, $parentCertificate.Issuer, [System.StringComparison]::OrdinalIgnoreCase)
    if ($isRootCandidate)
    {
        Write-Verbose "$Description parent issuer fallback candidate resolved to a root certificate. Root certificates are never used for issuer fallback."
        return $null
    }

    Write-CertificateDetailsVerbose -Description "$Description parent issuer" -Certificate $parentCertificate
    return $parentCertificate
}

function Normalize-DistinguishedNameKey
{
    param([Parameter(Mandatory)][string]$Key)

    $normalized = $Key.Trim().ToUpperInvariant()
    if ($normalized -eq 'ST')
    {
        return 'S'
    }

    return $normalized
}

function Parse-DistinguishedName
{
    param([Parameter(Mandatory)][string]$DistinguishedName)

    $result = @{}
    foreach ($segment in $DistinguishedName -split ',')
    {
        $match = [regex]::Match($segment, '^\s*([^=]+?)\s*=\s*(.+?)\s*$')
        if (-not $match.Success)
        {
            throw "Could not parse distinguished name segment '$segment' from '$DistinguishedName'."
        }

        $key = Normalize-DistinguishedNameKey -Key $match.Groups[1].Value
        $value = $match.Groups[2].Value.Trim()
        if ([string]::IsNullOrWhiteSpace($value))
        {
            throw "Distinguished name key '$key' in '$DistinguishedName' has an empty value."
        }

        if ($result.ContainsKey($key))
        {
            throw "Distinguished name '$DistinguishedName' contains duplicate key '$key', which is not supported."
        }

        $result[$key] = $value
    }

    return $result
}

function Get-WindowsBinaryTrustEvidence
{
    param(
        [Parameter(Mandatory)][string]$BinaryPath
    )

    $resolvedBinaryPath = [System.IO.Path]::GetFullPath($BinaryPath)
    Write-Verbose "Inspecting Authenticode signature for '$resolvedBinaryPath'."

    $signature = Get-AuthenticodeSignature -FilePath $resolvedBinaryPath
    $statusMessage = if ([string]::IsNullOrWhiteSpace($signature.StatusMessage)) { 'No additional details were provided.' } else { $signature.StatusMessage }
    Write-Verbose "Authenticode signature status for '$resolvedBinaryPath': $($signature.Status) - $statusMessage"

    if ($signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid)
    {
        throw "Authenticode signature validation failed for '$resolvedBinaryPath': $($signature.Status) - $statusMessage"
    }

    if ($null -eq $signature.SignerCertificate)
    {
        throw "Authenticode signature on '$resolvedBinaryPath' did not include a signer certificate."
    }

    $signerChain = Get-ValidatedCertificateChain -Certificate $signature.SignerCertificate -Description 'Signer' -IgnoreTimeValidity
    $issuerCertificate = Get-ImmediateIssuerCertificate -Chain $signerChain -Description 'Signer'
    $issuerThumbprint = Get-CertificateSha512Thumbprint -Certificate $issuerCertificate
    Write-Verbose "Signer issuer SHA512 thumbprint for '$resolvedBinaryPath': '$issuerThumbprint'."
    $parentIssuerCertificate = Get-ParentIntermediateIssuerCertificate -Chain $signerChain -Description 'Signer'
    $parentIssuerThumbprint = $null
    if ($null -ne $parentIssuerCertificate)
    {
        $parentIssuerThumbprint = Get-CertificateSha512Thumbprint -Certificate $parentIssuerCertificate
        Write-Verbose "Signer parent issuer SHA512 thumbprint for '$resolvedBinaryPath': '$parentIssuerThumbprint'."
    }

    if ($null -eq $signature.TimeStamperCertificate)
    {
        throw "Expected a timestamped signature for '$resolvedBinaryPath', but no timestamp certificate was present."
    }

    $timestampChain = Get-ValidatedCertificateChain -Certificate $signature.TimeStamperCertificate -Description 'Timestamp'

    return [pscustomobject]@{
        BinaryPath = $resolvedBinaryPath
        Signature = $signature
        SignerCertificate = $signature.SignerCertificate
        SignerSubject = $signature.SignerCertificate.Subject
        SignerChain = $signerChain
        SignerIssuerCertificate = $issuerCertificate
        SignerIssuerSha512Thumbprint = $issuerThumbprint
        SignerParentIssuerCertificate = $parentIssuerCertificate
        SignerParentIssuerSha512Thumbprint = $parentIssuerThumbprint
        TimeStamperCertificate = $signature.TimeStamperCertificate
        TimeStamperChain = $timestampChain
    }
}

function Get-NormalizedSha512ThumbprintSet
{
    param(
        [Parameter(Mandatory)][string[]]$Thumbprints,
        [Parameter(Mandatory)][string]$Description
    )

    $normalizedThumbprintSet = @($Thumbprints |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
        ForEach-Object { $_.Trim().ToLowerInvariant() })
    if ($normalizedThumbprintSet.Count -eq 0)
    {
        throw "At least one expected $Description SHA512 thumbprint is required."
    }

    return $normalizedThumbprintSet
}

function Format-Sha512ThumbprintSet
{
    param([Parameter(Mandatory)][string[]]$Thumbprints)

    return ($Thumbprints | ForEach-Object { "'$_'" }) -join ', '
}

function Test-Sha512ThumbprintMatch
{
    param(
        [Parameter(Mandatory)][string]$ActualThumbprint,
        [Parameter(Mandatory)][string[]]$ExpectedThumbprints
    )

    $match = $ExpectedThumbprints |
        Where-Object { [string]::Equals($_, $ActualThumbprint, [System.StringComparison]::OrdinalIgnoreCase) } |
        Select-Object -First 1
    return ($null -ne $match)
}

function Assert-SignerIssuerTrust
{
    param(
        [Parameter(Mandatory)]$Evidence,
        [Parameter(Mandatory)][string[]]$ExpectedIssuerThumbprints,
        [string[]]$ExpectedParentIssuerThumbprints = @()
    )

    $expectedIssuerThumbprintSet = Get-NormalizedSha512ThumbprintSet -Thumbprints $ExpectedIssuerThumbprints -Description 'signer issuer'
    $formattedExpectedIssuerThumbprints = Format-Sha512ThumbprintSet -Thumbprints $expectedIssuerThumbprintSet
    Write-Verbose "Validating signer immediate issuer SHA512 thumbprint for '$($Evidence.BinaryPath)'. Expected one of $formattedExpectedIssuerThumbprints, actual '$($Evidence.SignerIssuerSha512Thumbprint)'."

    if (Test-Sha512ThumbprintMatch -ActualThumbprint $Evidence.SignerIssuerSha512Thumbprint -ExpectedThumbprints $expectedIssuerThumbprintSet)
    {
        Write-Verbose "Signer immediate issuer matched configured issuer SHA512 thumbprints for '$($Evidence.BinaryPath)'."
        return [pscustomobject]@{
            MatchSource = 'ImmediateIssuer'
            Certificate = $Evidence.SignerIssuerCertificate
            Sha512Thumbprint = $Evidence.SignerIssuerSha512Thumbprint
            UsedFallback = $false
        }
    }

    Write-Verbose "Signer immediate issuer thumbprint for '$($Evidence.BinaryPath)' did not match configured issuer SHA512 thumbprints. Evaluating parent issuer fallback."
    $expectedParentIssuerThumbprintSet = @($ExpectedParentIssuerThumbprints |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
        ForEach-Object { $_.Trim().ToLowerInvariant() })
    if ($expectedParentIssuerThumbprintSet.Count -eq 0)
    {
        throw "Signer issuer certificate '$($Evidence.SignerIssuerCertificate.Subject)' has SHA512 thumbprint '$($Evidence.SignerIssuerSha512Thumbprint)', expected one of: $formattedExpectedIssuerThumbprints. Parent issuer fallback is not configured."
    }

    $formattedExpectedParentIssuerThumbprints = Format-Sha512ThumbprintSet -Thumbprints $expectedParentIssuerThumbprintSet

    if ($null -eq $Evidence.SignerParentIssuerCertificate -or [string]::IsNullOrWhiteSpace($Evidence.SignerParentIssuerSha512Thumbprint))
    {
        throw "Signer issuer certificate '$($Evidence.SignerIssuerCertificate.Subject)' has SHA512 thumbprint '$($Evidence.SignerIssuerSha512Thumbprint)', expected one of: $formattedExpectedIssuerThumbprints. Parent issuer fallback expected one of: $formattedExpectedParentIssuerThumbprints, but no parent intermediate issuer was available."
    }

    Write-Verbose "Falling back to signer parent issuer SHA512 thumbprint for '$($Evidence.BinaryPath)'. Expected one of $formattedExpectedParentIssuerThumbprints, actual '$($Evidence.SignerParentIssuerSha512Thumbprint)'."
    if (Test-Sha512ThumbprintMatch -ActualThumbprint $Evidence.SignerParentIssuerSha512Thumbprint -ExpectedThumbprints $expectedParentIssuerThumbprintSet)
    {
        Write-Verbose "Signer parent issuer fallback matched configured parent issuer SHA512 thumbprints for '$($Evidence.BinaryPath)'."
        return [pscustomobject]@{
            MatchSource = 'ParentIssuer'
            Certificate = $Evidence.SignerParentIssuerCertificate
            Sha512Thumbprint = $Evidence.SignerParentIssuerSha512Thumbprint
            UsedFallback = $true
        }
    }

    throw "Signer issuer certificate '$($Evidence.SignerIssuerCertificate.Subject)' has SHA512 thumbprint '$($Evidence.SignerIssuerSha512Thumbprint)', expected one of: $formattedExpectedIssuerThumbprints. Fallback parent issuer certificate '$($Evidence.SignerParentIssuerCertificate.Subject)' has SHA512 thumbprint '$($Evidence.SignerParentIssuerSha512Thumbprint)', expected one of: $formattedExpectedParentIssuerThumbprints."
}

function Assert-WindowsBinaryTrust
{
    param(
        [Parameter(Mandatory)][string]$BinaryPath,
        [Parameter(Mandatory)][string]$ExpectedSubject,
        [Parameter(Mandatory)][string[]]$ExpectedIssuerSha512Thumbprints,
        [string[]]$ExpectedParentIssuerSha512Thumbprints = @()
    )

    $evidence = Get-WindowsBinaryTrustEvidence -BinaryPath $BinaryPath
    Write-Verbose "Validating signer subject for '$($evidence.BinaryPath)'."

    $actualSubject = $evidence.SignerSubject
    $expectedSubjectParts = Parse-DistinguishedName -DistinguishedName $ExpectedSubject
    $actualSubjectParts = Parse-DistinguishedName -DistinguishedName $actualSubject
    foreach ($key in $expectedSubjectParts.Keys)
    {
        if (-not $actualSubjectParts.ContainsKey($key))
        {
            throw "Signer subject '$actualSubject' is missing required field '$key' from expected subject '$ExpectedSubject'."
        }

        $actualValue = $actualSubjectParts[$key]
        $expectedValue = $expectedSubjectParts[$key]
        if (-not [string]::Equals($actualValue, $expectedValue, [System.StringComparison]::OrdinalIgnoreCase))
        {
            throw "Signer subject '$actualSubject' has '$key=$actualValue', expected '$key=$expectedValue'."
        }
    }

    $issuerTrustMatch = Assert-SignerIssuerTrust `
        -Evidence $evidence `
        -ExpectedIssuerThumbprints $ExpectedIssuerSha512Thumbprints `
        -ExpectedParentIssuerThumbprints $ExpectedParentIssuerSha512Thumbprints

    Add-Member -InputObject $evidence -NotePropertyName SignerIssuerTrustMatch -NotePropertyValue $issuerTrustMatch -Force
    Write-Verbose "Windows binary trust verification succeeded for '$($evidence.BinaryPath)'."
    return $evidence
}

# --- Entry point ---

try
{
    $null = Assert-WindowsBinaryTrust `
        -BinaryPath $BinaryPath `
        -ExpectedSubject $ExpectedSignerSubject `
        -ExpectedIssuerSha512Thumbprints $ExpectedSignerIssuerSha512Thumbprints `
        -ExpectedParentIssuerSha512Thumbprints $ExpectedSignerParentIssuerSha512Thumbprints

    @{ success = $true; error = $null } | ConvertTo-Json -Compress
}
catch
{
    @{ success = $false; error = $_.Exception.Message } | ConvertTo-Json -Compress
    exit 1
}
