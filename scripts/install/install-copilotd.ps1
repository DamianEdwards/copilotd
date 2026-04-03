[CmdletBinding()]
param(
    [ValidateSet('Dev', 'PreRelease', 'Stable')]
    [string]$Quality = 'Stable',

    [switch]$Force,

    [string]$TargetPath = (Join-Path $env:USERPROFILE '.copilotd\bin'),

    [bool]$UpdatePath = $true,

    [string]$Repository = 'DamianEdwards/copilotd',

    [string]$ExpectedSignerSubject = 'CN=Damian Edwards, O=Damian Edwards, L=Issaquah, S=Washington, C=US',

    [Parameter(DontShow = $true)]
    [switch]$NoExecute
)

$ErrorActionPreference = 'Stop'

# Keep this single-sourced here; the release workflow reads these values from the installer script.
$ExpectedSignerIssuerSha512Thumbprints = @(
    '1c93dcf4e032b19949a67722d0c25e683309fbcd36110da84129f45d8175b709ebc6ef3439596ece9eb8f2dae1967b856adc49ba74535244a8a5db5fb48fa7b9'
    '1770433e5d2c028e0bf8640a0345bdb86307e7cc2a99cfbe93acf9d960a996d1c63b2d5cf30d52e7741df4fd057ea778442f75c1b62ee2106c66333078a04e6d'
)
$ExpectedSignerParentIssuerSha512Thumbprints = @(
    '46f16bb99340f8d728c83ff093af9d4cff87811d432f92a804741144f0f3fc0aa8011b1efe0c24e0480bd6c7cb7af699077f9b8fc7ec8a40f9f7a186725224c6'
)

$runningOnWindows = if (Get-Variable -Name IsWindows -ErrorAction SilentlyContinue)
{
    [bool]$IsWindows
}
else
{
    [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Windows)
}

if (-not $runningOnWindows)
{
    $scriptName = if ($MyInvocation.MyCommand.Name) { $MyInvocation.MyCommand.Name } else { 'install-copilotd.ps1' }
    Write-Error "$scriptName currently supports Windows only. Running on '$([System.Runtime.InteropServices.RuntimeInformation]::OSDescription)' is not yet supported."
    exit 1
}

function Normalize-PathEntry
{
    param([Parameter(Mandatory)][string]$PathEntry)

    return $PathEntry.Trim().TrimEnd('\').ToLowerInvariant()
}

function Ensure-PathContains
{
    param(
        [Parameter(Mandatory)][string]$Entry,
        [Parameter(Mandatory)][System.EnvironmentVariableTarget]$Target
    )

    $current = [System.Environment]::GetEnvironmentVariable('PATH', $Target)
    $existingEntries = @()
    if (-not [string]::IsNullOrWhiteSpace($current))
    {
        $existingEntries = @($current.Split(';', [System.StringSplitOptions]::RemoveEmptyEntries))
    }

    $normalizedTarget = Normalize-PathEntry -PathEntry $Entry
    $alreadyPresent = $existingEntries | Where-Object { (Normalize-PathEntry -PathEntry $_) -eq $normalizedTarget } | Select-Object -First 1
    if ($alreadyPresent)
    {
        return $false
    }

    $updated = if ([string]::IsNullOrWhiteSpace($current))
    {
        $Entry
    }
    else
    {
        $current.TrimEnd(';') + ';' + $Entry
    }

    [System.Environment]::SetEnvironmentVariable('PATH', $updated, $Target)
    return $true
}

function Get-HttpStatusCode
{
    param([Parameter(Mandatory)][System.Exception]$Exception)

    $responseProperty = $Exception.PSObject.Properties['Response']
    if ($null -eq $responseProperty -or $null -eq $Exception.Response)
    {
        return $null
    }

    try
    {
        return [int]$Exception.Response.StatusCode
    }
    catch
    {
        return $null
    }
}

function Invoke-GitHubApi
{
    param([Parameter(Mandatory)][string]$Uri)

    $token = if (-not [string]::IsNullOrWhiteSpace($env:GH_TOKEN))
    {
        $env:GH_TOKEN
    }
    elseif (-not [string]::IsNullOrWhiteSpace($env:GITHUB_TOKEN))
    {
        $env:GITHUB_TOKEN
    }
    else
    {
        $null
    }

    $headers = @{
        Accept       = 'application/vnd.github+json'
        'User-Agent' = 'copilotd-install-script'
    }
    if ($token)
    {
        $headers.Authorization = "Bearer $token"
    }

    Invoke-RestMethod -Method Get -Uri $Uri -Headers $headers
}

function Invoke-GitHubAssetDownload
{
    param(
        [Parameter(Mandatory)][string]$Uri,
        [Parameter(Mandatory)][string]$OutFile
    )

    $token = if (-not [string]::IsNullOrWhiteSpace($env:GH_TOKEN))
    {
        $env:GH_TOKEN
    }
    elseif (-not [string]::IsNullOrWhiteSpace($env:GITHUB_TOKEN))
    {
        $env:GITHUB_TOKEN
    }
    else
    {
        $null
    }

    $headers = @{
        'User-Agent' = 'copilotd-install-script'
    }
    if ($token)
    {
        $headers.Authorization = "Bearer $token"
    }

    Invoke-WebRequest -Uri $Uri -Headers $headers -OutFile $OutFile
}

function Get-ReleaseByTag
{
    param(
        [Parameter(Mandatory)][string]$Repo,
        [Parameter(Mandatory)][string]$Tag
    )

    $tagUri = "https://api.github.com/repos/$Repo/releases/tags/$Tag"
    try
    {
        return Invoke-GitHubApi -Uri $tagUri
    }
    catch
    {
        if ((Get-HttpStatusCode -Exception $_.Exception) -eq 404)
        {
            return $null
        }

        throw
    }
}

function Get-ReleaseAsset
{
    param(
        [Parameter(Mandatory)]$Release,
        [Parameter(Mandatory)][string]$AssetName
    )

    return $Release.assets | Where-Object { $_.name -eq $AssetName } | Select-Object -First 1
}

function Get-WindowsArchitecture
{
    $arch = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture
    switch ($arch)
    {
        ([System.Runtime.InteropServices.Architecture]::X64) { return 'x64' }
        ([System.Runtime.InteropServices.Architecture]::Arm64) { return 'arm64' }
        default { throw "Unsupported Windows architecture '$arch'. Only x64 and arm64 are supported." }
    }
}

function Get-ReleaseForQuality
{
    param(
        [Parameter(Mandatory)][string]$Repo,
        [Parameter(Mandatory)][string]$SelectedQuality,
        [Parameter(Mandatory)][string]$AssetName
    )

    if ($SelectedQuality -eq 'Dev')
    {
        $devRelease = Get-ReleaseByTag -Repo $Repo -Tag 'dev'
        if ($null -eq $devRelease)
        {
            $releases = @(Invoke-GitHubApi -Uri "https://api.github.com/repos/$Repo/releases?per_page=100" | ForEach-Object { $_ })
            $devRelease = $releases | Where-Object { $_.name -eq 'Development Build' } | Select-Object -First 1
        }

        if ($null -eq $devRelease)
        {
            throw "Could not locate the standing Development Build release (tag 'dev' or title 'Development Build')."
        }

        $devAsset = Get-ReleaseAsset -Release $devRelease -AssetName $AssetName
        if ($null -eq $devAsset)
        {
            throw "Development Build release '$($devRelease.name)' does not contain asset '$AssetName'."
        }

        return $devRelease
    }

    $allReleases = @(Invoke-GitHubApi -Uri "https://api.github.com/repos/$Repo/releases?per_page=100" | ForEach-Object { $_ })
    foreach ($release in $allReleases)
    {
        if ($release.draft)
        {
            continue
        }

        if ($release.tag_name -in @('dev', 'install-scripts'))
        {
            continue
        }

        if ($SelectedQuality -eq 'Stable' -and $release.prerelease)
        {
            continue
        }

        $asset = Get-ReleaseAsset -Release $release -AssetName $AssetName
        if ($null -eq $asset)
        {
            continue
        }

        return $release
    }

    throw "No '$SelectedQuality' release containing '$AssetName' was found in '$Repo'."
}

function Get-ExpectedSha256
{
    param(
        [Parameter(Mandatory)][string]$ChecksumsPath,
        [Parameter(Mandatory)][string]$AssetName
    )

    $line = @(Get-Content -Path $ChecksumsPath | Where-Object { $_ -match "\s\*?$([regex]::Escape($AssetName))$" } | Select-Object -First 1)
    if ($line.Count -eq 0)
    {
        throw "checksums.txt did not contain an entry for '$AssetName'."
    }

    $match = [regex]::Match($line[0], '^\s*([0-9a-fA-F]{64})\s+\*?.+$')
    if (-not $match.Success)
    {
        throw "Invalid checksum line format for '$AssetName' in checksums.txt."
    }

    return $match.Groups[1].Value.ToLowerInvariant()
}

function Get-CopilotdInstallerTrustConfiguration
{
    return [pscustomobject]@{
        ExpectedSignerSubject = $ExpectedSignerSubject
        ExpectedSignerIssuerSha512Thumbprints = @($ExpectedSignerIssuerSha512Thumbprints)
        ExpectedSignerParentIssuerSha512Thumbprints = @($ExpectedSignerParentIssuerSha512Thumbprints)
    }
}

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

function Assert-WindowsArchiveIntegrity
{
    param(
        [Parameter(Mandatory)][string]$ArchivePath,
        [Parameter(Mandatory)][string]$AssetName,
        [Parameter(Mandatory)][string]$ChecksumsPath,
        [string]$ReleaseMetadataPath
    )

    Write-Verbose "Validating archive SHA256 for '$AssetName' using '$ChecksumsPath'."
    $expectedSha = Get-ExpectedSha256 -ChecksumsPath $ChecksumsPath -AssetName $AssetName
    $actualSha = (Get-FileHash -Path $ArchivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    Write-Verbose "checksums.txt expected SHA256 for '$AssetName': '$expectedSha'."
    Write-Verbose "Actual SHA256 for '$ArchivePath': '$actualSha'."

    if ($expectedSha -ne $actualSha)
    {
        throw "SHA256 mismatch for '$AssetName'. Expected '$expectedSha' but got '$actualSha'."
    }

    if (-not [string]::IsNullOrWhiteSpace($ReleaseMetadataPath))
    {
        Write-Verbose "Validating release metadata for '$AssetName' using '$ReleaseMetadataPath'."
        $metadata = Get-Content -Path $ReleaseMetadataPath -Raw | ConvertFrom-Json
        $metadataAsset = $metadata.assets | Where-Object { $_.name -eq $AssetName } | Select-Object -First 1
        if ($null -eq $metadataAsset)
        {
            throw "release-metadata.json did not contain asset '$AssetName'."
        }

        $metadataSha = $metadataAsset.sha256.ToLowerInvariant()
        Write-Verbose "release-metadata.json SHA256 for '$AssetName': '$metadataSha'."
        if ($metadataSha -ne $expectedSha)
        {
            throw "release-metadata.json SHA256 for '$AssetName' did not match checksums.txt."
        }
    }

    return $actualSha
}

function Expand-WindowsReleaseArchive
{
    param(
        [Parameter(Mandatory)][string]$ArchivePath,
        [Parameter(Mandatory)][string]$DestinationPath
    )

    Write-Verbose "Expanding '$ArchivePath' to '$DestinationPath'."
    Expand-Archive -Path $ArchivePath -DestinationPath $DestinationPath -Force

    $binaryPath = Join-Path $DestinationPath 'copilotd.exe'
    if (-not (Test-Path $binaryPath))
    {
        throw "Downloaded archive '$([System.IO.Path]::GetFileName($ArchivePath))' did not contain 'copilotd.exe'."
    }

    Write-Verbose "Found extracted Windows binary '$binaryPath'."
    return $binaryPath
}

function Invoke-StatusStep
{
    param(
        [Parameter(Mandatory)][string]$Message,
        [Parameter(Mandatory)][scriptblock]$Action
    )

    Write-Host "$Message... " -NoNewline
    & $Action
    Write-Host 'done'
}

function Get-ReleaseStatusLabel
{
    param(
        [Parameter(Mandatory)][string]$SelectedQuality,
        [Parameter(Mandatory)]$Release
    )

    $releaseLabel =
        switch ($SelectedQuality)
        {
            'Stable' { 'latest stable release' }
            'PreRelease' { 'latest prerelease' }
            'Dev' { 'latest development build' }
            default { 'release' }
        }

    $releaseVersion = if (-not [string]::IsNullOrWhiteSpace($Release.tag_name))
    {
        $Release.tag_name
    }
    elseif (-not [string]::IsNullOrWhiteSpace($Release.name))
    {
        $Release.name
    }
    else
    {
        'unknown version'
    }

    return "$releaseLabel ($releaseVersion)"
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

function Get-CopilotdVersionString
{
    param([Parameter(Mandatory)][string]$BinaryPath)

    try
    {
        $output = & $BinaryPath --version 2>$null
        if ($LASTEXITCODE -eq 0 -and $output)
        {
            $firstLine = @($output | Select-Object -First 1)[0]
            $match = [regex]::Match($firstLine, '\d+\.\d+\.\d+(?:\.\d+)?(?:-[0-9A-Za-z\.-]+)?')
            if ($match.Success)
            {
                return $match.Value
            }
        }
    }
    catch
    {
    }

    $versionInfo = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($BinaryPath)
    $candidate = if (-not [string]::IsNullOrWhiteSpace($versionInfo.ProductVersion))
    {
        $versionInfo.ProductVersion
    }
    else
    {
        $versionInfo.FileVersion
    }

    if ([string]::IsNullOrWhiteSpace($candidate))
    {
        throw "Could not determine version for '$BinaryPath'."
    }

    $metadataMatch = [regex]::Match($candidate, '\d+\.\d+\.\d+(?:\.\d+)?(?:-[0-9A-Za-z\.-]+)?')
    if (-not $metadataMatch.Success)
    {
        throw "Version '$candidate' for '$BinaryPath' was not in an expected format."
    }

    return $metadataMatch.Value
}

function Parse-SemanticVersion
{
    param([Parameter(Mandatory)][string]$Value)

    $match = [regex]::Match($Value, '^(?<major>\d+)\.(?<minor>\d+)\.(?<patch>\d+)(?:\.\d+)?(?:-(?<prerelease>[0-9A-Za-z\.-]+))?$')
    if (-not $match.Success)
    {
        throw "Version '$Value' is not a supported semantic version format."
    }

    return [ordered]@{
        Major = [int]$match.Groups['major'].Value
        Minor = [int]$match.Groups['minor'].Value
        Patch = [int]$match.Groups['patch'].Value
        PreRelease = $match.Groups['prerelease'].Value
    }
}

function Compare-SemanticVersion
{
    param(
        [Parameter(Mandatory)][string]$Left,
        [Parameter(Mandatory)][string]$Right
    )

    $leftVersion = Parse-SemanticVersion -Value $Left
    $rightVersion = Parse-SemanticVersion -Value $Right

    foreach ($part in @('Major', 'Minor', 'Patch'))
    {
        if ($leftVersion[$part] -gt $rightVersion[$part])
        {
            return 1
        }

        if ($leftVersion[$part] -lt $rightVersion[$part])
        {
            return -1
        }
    }

    $leftPre = $leftVersion.PreRelease
    $rightPre = $rightVersion.PreRelease
    if ([string]::IsNullOrWhiteSpace($leftPre) -and [string]::IsNullOrWhiteSpace($rightPre))
    {
        return 0
    }

    if ([string]::IsNullOrWhiteSpace($leftPre))
    {
        return 1
    }

    if ([string]::IsNullOrWhiteSpace($rightPre))
    {
        return -1
    }

    $leftIdentifiers = $leftPre.Split('.', [System.StringSplitOptions]::RemoveEmptyEntries)
    $rightIdentifiers = $rightPre.Split('.', [System.StringSplitOptions]::RemoveEmptyEntries)
    $maxLength = [Math]::Max($leftIdentifiers.Length, $rightIdentifiers.Length)

    for ($index = 0; $index -lt $maxLength; $index++)
    {
        if ($index -ge $leftIdentifiers.Length)
        {
            return -1
        }

        if ($index -ge $rightIdentifiers.Length)
        {
            return 1
        }

        $leftIdentifier = $leftIdentifiers[$index]
        $rightIdentifier = $rightIdentifiers[$index]
        $leftIsNumeric = $leftIdentifier -match '^\d+$'
        $rightIsNumeric = $rightIdentifier -match '^\d+$'

        if ($leftIsNumeric -and $rightIsNumeric)
        {
            $leftNumeric = [System.Numerics.BigInteger]::Parse($leftIdentifier)
            $rightNumeric = [System.Numerics.BigInteger]::Parse($rightIdentifier)
            if ($leftNumeric -gt $rightNumeric)
            {
                return 1
            }

            if ($leftNumeric -lt $rightNumeric)
            {
                return -1
            }

            continue
        }

        if ($leftIsNumeric -and -not $rightIsNumeric)
        {
            return -1
        }

        if (-not $leftIsNumeric -and $rightIsNumeric)
        {
            return 1
        }

        $identifierComparison = [string]::CompareOrdinal($leftIdentifier, $rightIdentifier)
        if ($identifierComparison -gt 0)
        {
            return 1
        }

        if ($identifierComparison -lt 0)
        {
            return -1
        }
    }

    return 0
}

function Invoke-CopilotdInstall
{
    $architecture = Get-WindowsArchitecture
    $assetName = "copilotd-win-$architecture.zip"
    Write-Verbose "Selecting release asset '$assetName' for quality '$Quality' from '$Repository'."

    $release = Get-ReleaseForQuality -Repo $Repository -SelectedQuality $Quality -AssetName $assetName
    Write-Verbose "Selected release '$($release.name)' ($($release.tag_name))."
    $releaseStatusLabel = Get-ReleaseStatusLabel -SelectedQuality $Quality -Release $release

    $asset = Get-ReleaseAsset -Release $release -AssetName $assetName
    if ($null -eq $asset)
    {
        throw "Release '$($release.name)' does not contain expected asset '$assetName'."
    }

    if ($Quality -eq 'Dev' -and -not $Force)
    {
        $confirmation = Read-Host "Dev quality disables checksum/signature verification. Type YES to continue"
        if ($confirmation -cne 'YES')
        {
            throw 'Installation canceled by user.'
        }
    }

    $tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("copilotd-install-" + [guid]::NewGuid().ToString('N'))
    $downloadPath = Join-Path $tempRoot $assetName
    $extractPath = Join-Path $tempRoot 'extract'

    Write-Verbose "Using temporary workspace '$tempRoot'."

    try
    {
        New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null

        Invoke-StatusStep -Message "Downloading $releaseStatusLabel" -Action {
            Write-Verbose "Downloading release asset from '$($asset.browser_download_url)' to '$downloadPath'."
            Invoke-GitHubAssetDownload -Uri $asset.browser_download_url -OutFile $downloadPath
        }

        if ($Quality -ne 'Dev')
        {
            $checksumsAsset = Get-ReleaseAsset -Release $release -AssetName 'checksums.txt'
            if ($null -eq $checksumsAsset)
            {
                throw "Release '$($release.name)' did not include checksums.txt."
            }

            $checksumsPath = Join-Path $tempRoot 'checksums.txt'
            Write-Verbose "Downloading checksums from '$($checksumsAsset.browser_download_url)' to '$checksumsPath'."
            Invoke-GitHubAssetDownload -Uri $checksumsAsset.browser_download_url -OutFile $checksumsPath

            $releaseMetadataPath = $null
            $releaseMetadataAsset = Get-ReleaseAsset -Release $release -AssetName 'release-metadata.json'
            if ($null -ne $releaseMetadataAsset)
            {
                $releaseMetadataPath = Join-Path $tempRoot 'release-metadata.json'
                Write-Verbose "Downloading release metadata from '$($releaseMetadataAsset.browser_download_url)' to '$releaseMetadataPath'."
                Invoke-GitHubAssetDownload -Uri $releaseMetadataAsset.browser_download_url -OutFile $releaseMetadataPath
            }

            Invoke-StatusStep -Message 'Verifying asset checksums' -Action {
                $null = Assert-WindowsArchiveIntegrity -ArchivePath $downloadPath -AssetName $assetName -ChecksumsPath $checksumsPath -ReleaseMetadataPath $releaseMetadataPath
            }
        }

        $downloadedBinaryPath = Expand-WindowsReleaseArchive -ArchivePath $downloadPath -DestinationPath $extractPath

        if ($Quality -ne 'Dev')
        {
            Invoke-StatusStep -Message 'Verifying asset provenance' -Action {
                $null = Assert-WindowsBinaryTrust -BinaryPath $downloadedBinaryPath -ExpectedSubject $ExpectedSignerSubject -ExpectedIssuerSha512Thumbprints $ExpectedSignerIssuerSha512Thumbprints -ExpectedParentIssuerSha512Thumbprints $ExpectedSignerParentIssuerSha512Thumbprints
            }
        }
        else
        {
            Write-Host 'Skipping checksum and provenance verification for development build.'
        }

        $installDirectory = [System.IO.Path]::GetFullPath($TargetPath)
        Write-Verbose "Installing to '$installDirectory'."
        New-Item -ItemType Directory -Path $installDirectory -Force | Out-Null

        $destinationPath = Join-Path $installDirectory 'copilotd.exe'
        $downloadedVersion = Get-CopilotdVersionString -BinaryPath $downloadedBinaryPath
        $installedVersion = $downloadedVersion
        Write-Verbose "Downloaded copilotd.exe version: '$downloadedVersion'."

        $shouldInstall = $true
        if (Test-Path $destinationPath)
        {
            $existingVersion = Get-CopilotdVersionString -BinaryPath $destinationPath
            $comparison = Compare-SemanticVersion -Left $downloadedVersion -Right $existingVersion
            Write-Verbose "Existing copilotd.exe version at '$destinationPath': '$existingVersion'. Comparison result: $comparison."
            if ($comparison -le 0)
            {
                $shouldInstall = $false
                $installedVersion = $existingVersion
                Write-Host "Existing copilotd.exe version '$existingVersion' is newer than or equal to downloaded version '$downloadedVersion'; skipping overwrite."
            }
        }

        if ($shouldInstall)
        {
            Invoke-StatusStep -Message "Installing $downloadedVersion to '$installDirectory'" -Action {
                Copy-Item -Path $downloadedBinaryPath -Destination $destinationPath -Force
            }
        }

        if ($UpdatePath)
        {
            $sessionPathUpdated = Ensure-PathContains -Entry $installDirectory -Target Process
            $userPathUpdated = Ensure-PathContains -Entry $installDirectory -Target User

            if ($sessionPathUpdated)
            {
                Write-Host "Added '$installDirectory' to current session PATH."
            }
            else
            {
                Write-Host "Current session PATH already contains '$installDirectory'."
            }

            if ($userPathUpdated)
            {
                Write-Host "Added '$installDirectory' to user PATH (will take effect in new terminal sessions)."
            }
            else
            {
                Write-Host "User PATH already contains '$installDirectory'."
            }
        }
        else
        {
            Write-Host "Skipped PATH updates because -UpdatePath was set to false."
        }

        Write-Host "copilotd $installedVersion is ready to use from '$installDirectory'." -ForegroundColor Green
    }
    finally
    {
        if (Test-Path $tempRoot)
        {
            Write-Verbose "Cleaning up temporary workspace '$tempRoot'."
            Remove-Item -Path $tempRoot -Recurse -Force
        }
    }
}

if (-not $NoExecute)
{
    Invoke-CopilotdInstall
}
