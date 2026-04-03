[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('InstallerDefaults', 'GoodBinary', 'UnsignedBinary', 'TamperedBinary', 'WrongSubject', 'WrongIssuer', 'ChecksumMismatch', 'MetadataMismatch')]
    [string]$Scenario,

    [string]$BinaryPath,

    [string]$ArchivePath,

    [string]$ChecksumsPath,

    [string]$ReleaseMetadataPath,

    [string]$InstallerScriptPath = (Join-Path $PSScriptRoot 'install\install-copilotd.ps1'),

    [switch]$KeepArtifacts
)

$ErrorActionPreference = 'Stop'

function Get-RequiredFullPath
{
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Description
    )

    $resolvedPath = [System.IO.Path]::GetFullPath($Path)
    if (-not (Test-Path $resolvedPath))
    {
        throw "$Description '$resolvedPath' was not found."
    }

    return $resolvedPath
}

function Invoke-ExpectedFailure
{
    param(
        [Parameter(Mandatory)][string]$ScenarioName,
        [Parameter(Mandatory)][scriptblock]$Action,
        [Parameter(Mandatory)][string]$ExpectedMessageFragment
    )

    $failureMessage = $null
    try
    {
        & $Action
    }
    catch
    {
        $failureMessage = $_.Exception.Message
    }

    if ($null -eq $failureMessage)
    {
        throw "Scenario '$ScenarioName' unexpectedly succeeded."
    }

    if ($failureMessage -notlike "*$ExpectedMessageFragment*")
    {
        throw "Scenario '$ScenarioName' failed, but with an unexpected message: $failureMessage"
    }

    Write-Host "Scenario '$ScenarioName' failed as expected: $failureMessage"
}

function New-TamperedBinaryCopy
{
    param(
        [Parameter(Mandatory)][string]$SourcePath,
        [Parameter(Mandatory)][string]$DestinationDirectory
    )

    $tamperedPath = Join-Path $DestinationDirectory (([System.IO.Path]::GetFileNameWithoutExtension($SourcePath)) + '-tampered' + [System.IO.Path]::GetExtension($SourcePath))
    Copy-Item -Path $SourcePath -Destination $tamperedPath -Force

    $bytes = [System.IO.File]::ReadAllBytes($tamperedPath)
    if ($bytes.Length -eq 0)
    {
        throw "Cannot tamper with empty file '$tamperedPath'."
    }

    $bytes[$bytes.Length - 1] = ($bytes[$bytes.Length - 1] + 1) % 256
    [System.IO.File]::WriteAllBytes($tamperedPath, $bytes)
    Write-Verbose "Created tampered binary '$tamperedPath'."

    return $tamperedPath
}

function Get-MutatedExpectedSubject
{
    param([Parameter(Mandatory)][string]$Subject)

    $segments = @($Subject -split ',')
    for ($index = 0; $index -lt $segments.Count; $index++)
    {
        $match = [regex]::Match($segments[$index], '^\s*([^=]+?)\s*=\s*(.+?)\s*$')
        if (-not $match.Success)
        {
            continue
        }

        $key = Normalize-DistinguishedNameKey -Key $match.Groups[1].Value
        if ($key -eq 'CN')
        {
            $segments[$index] = 'CN=Unexpected Signer'
            return ($segments | ForEach-Object { $_.Trim() }) -join ', '
        }
    }

    return "CN=Unexpected Signer, $Subject"
}

function Get-MutatedHexString
{
    param([Parameter(Mandatory)][string]$Value)

    $replacement = if ($Value[0] -eq '0') { '1' } else { '0' }
    return $replacement + $Value.Substring(1)
}

function New-ChecksumMismatchFile
{
    param(
        [Parameter(Mandatory)][string]$ChecksumsPath,
        [Parameter(Mandatory)][string]$AssetName,
        [Parameter(Mandatory)][string]$DestinationDirectory
    )

    $destinationPath = Join-Path $DestinationDirectory 'checksums-mismatch.txt'
    $lines = Get-Content -Path $ChecksumsPath
    $updatedLines = foreach ($line in $lines)
    {
        if ($line -match "\s\*?$([regex]::Escape($AssetName))$")
        {
            $match = [regex]::Match($line, '^\s*([0-9a-fA-F]{64})(?<suffix>\s+\*?.+)$')
            if (-not $match.Success)
            {
                throw "Invalid checksum line format for '$AssetName' in '$ChecksumsPath'."
            }

            $hash = $match.Groups[1].Value.ToLowerInvariant()
            $replacement = if ($hash[0] -eq '0') { '1' } else { '0' }
            $replacement + $hash.Substring(1) + $match.Groups['suffix'].Value
        }
        else
        {
            $line
        }
    }

    $updatedLines | Set-Content -Path $destinationPath
    Write-Verbose "Created checksum-mismatch fixture '$destinationPath'."
    return $destinationPath
}

function New-MetadataMismatchFile
{
    param(
        [Parameter(Mandatory)][string]$ReleaseMetadataPath,
        [Parameter(Mandatory)][string]$AssetName,
        [Parameter(Mandatory)][string]$DestinationDirectory
    )

    $destinationPath = Join-Path $DestinationDirectory 'release-metadata-mismatch.json'
    $metadata = Get-Content -Path $ReleaseMetadataPath -Raw | ConvertFrom-Json
    $metadataAsset = $metadata.assets | Where-Object { $_.name -eq $AssetName } | Select-Object -First 1
    if ($null -eq $metadataAsset)
    {
        throw "release-metadata.json did not contain asset '$AssetName'."
    }

    $metadataAsset.sha256 = Get-MutatedHexString -Value $metadataAsset.sha256
    $metadata | ConvertTo-Json -Depth 10 | Set-Content -Path $destinationPath
    Write-Verbose "Created metadata-mismatch fixture '$destinationPath'."
    return $destinationPath
}

$installerScriptPath = Get-RequiredFullPath -Path $InstallerScriptPath -Description 'Installer script'

Write-Verbose "Loading installer helpers from '$installerScriptPath'."
. $installerScriptPath -NoExecute

$config = Get-CopilotdInstallerTrustConfiguration
$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("copilotd-installer-verification-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null

try
{
    switch ($Scenario)
    {
        'InstallerDefaults'
        {
            $binaryPath = Get-RequiredFullPath -Path $BinaryPath -Description 'Signed binary'
            $evidence = Assert-WindowsBinaryTrust -BinaryPath $binaryPath -ExpectedSubject $config.ExpectedSignerSubject -ExpectedIssuerSha512Thumbprints $config.ExpectedSignerIssuerSha512Thumbprints -ExpectedParentIssuerSha512Thumbprints $config.ExpectedSignerParentIssuerSha512Thumbprints
            $matchDescription = if ($evidence.SignerIssuerTrustMatch.UsedFallback)
            {
                "using parent issuer fallback '$($evidence.SignerIssuerTrustMatch.Certificate.Subject)' ($($evidence.SignerIssuerTrustMatch.Sha512Thumbprint))"
            }
            else
            {
                "using immediate issuer '$($evidence.SignerIssuerTrustMatch.Certificate.Subject)' ($($evidence.SignerIssuerTrustMatch.Sha512Thumbprint))"
            }

            Write-Host "Scenario 'InstallerDefaults' succeeded for '$binaryPath' $matchDescription."
        }

        'GoodBinary'
        {
            $binaryPath = Get-RequiredFullPath -Path $BinaryPath -Description 'Signed binary'
            $evidence = Get-WindowsBinaryTrustEvidence -BinaryPath $binaryPath
            $null = Assert-WindowsBinaryTrust -BinaryPath $binaryPath -ExpectedSubject $evidence.SignerSubject -ExpectedIssuerSha512Thumbprints @($evidence.SignerIssuerSha512Thumbprint)
            Write-Host "Scenario 'GoodBinary' succeeded for '$binaryPath': '$($evidence.SignerSubject)' issued by '$($evidence.SignerIssuerCertificate.Subject)'."
        }

        'UnsignedBinary'
        {
            $binaryPath = Get-RequiredFullPath -Path $BinaryPath -Description 'Unsigned binary'
            Invoke-ExpectedFailure -ScenarioName $Scenario -ExpectedMessageFragment 'Authenticode signature validation failed' -Action {
                $null = Get-WindowsBinaryTrustEvidence -BinaryPath $binaryPath
            }
        }

        'TamperedBinary'
        {
            $binaryPath = Get-RequiredFullPath -Path $BinaryPath -Description 'Signed binary'
            $tamperedPath = New-TamperedBinaryCopy -SourcePath $binaryPath -DestinationDirectory $tempRoot
            Invoke-ExpectedFailure -ScenarioName $Scenario -ExpectedMessageFragment 'Authenticode signature validation failed' -Action {
                $null = Get-WindowsBinaryTrustEvidence -BinaryPath $tamperedPath
            }
        }

        'WrongSubject'
        {
            $binaryPath = Get-RequiredFullPath -Path $BinaryPath -Description 'Signed binary'
            $evidence = Get-WindowsBinaryTrustEvidence -BinaryPath $binaryPath
            $mutatedSubject = Get-MutatedExpectedSubject -Subject $evidence.SignerSubject
            Invoke-ExpectedFailure -ScenarioName $Scenario -ExpectedMessageFragment 'expected' -Action {
                $null = Assert-WindowsBinaryTrust -BinaryPath $binaryPath -ExpectedSubject $mutatedSubject -ExpectedIssuerSha512Thumbprints @($evidence.SignerIssuerSha512Thumbprint)
            }
        }

        'WrongIssuer'
        {
            $binaryPath = Get-RequiredFullPath -Path $BinaryPath -Description 'Signed binary'
            $evidence = Get-WindowsBinaryTrustEvidence -BinaryPath $binaryPath
            $mutatedThumbprint = Get-MutatedHexString -Value $evidence.SignerIssuerSha512Thumbprint
            $mutatedParentThumbprints = if ([string]::IsNullOrWhiteSpace($evidence.SignerParentIssuerSha512Thumbprint))
            {
                @()
            }
            else
            {
                @((Get-MutatedHexString -Value $evidence.SignerParentIssuerSha512Thumbprint))
            }
            Invoke-ExpectedFailure -ScenarioName $Scenario -ExpectedMessageFragment 'expected' -Action {
                $null = Assert-WindowsBinaryTrust -BinaryPath $binaryPath -ExpectedSubject $evidence.SignerSubject -ExpectedIssuerSha512Thumbprints @($mutatedThumbprint) -ExpectedParentIssuerSha512Thumbprints $mutatedParentThumbprints
            }
        }

        'ChecksumMismatch'
        {
            $archivePath = Get-RequiredFullPath -Path $ArchivePath -Description 'Archive'
            $checksumsPath = Get-RequiredFullPath -Path $ChecksumsPath -Description 'checksums.txt'
            $assetName = [System.IO.Path]::GetFileName($archivePath)
            $mismatchChecksumsPath = New-ChecksumMismatchFile -ChecksumsPath $checksumsPath -AssetName $assetName -DestinationDirectory $tempRoot
            Invoke-ExpectedFailure -ScenarioName $Scenario -ExpectedMessageFragment 'SHA256 mismatch' -Action {
                $null = Assert-WindowsArchiveIntegrity -ArchivePath $archivePath -AssetName $assetName -ChecksumsPath $mismatchChecksumsPath
            }
        }

        'MetadataMismatch'
        {
            $archivePath = Get-RequiredFullPath -Path $ArchivePath -Description 'Archive'
            $checksumsPath = Get-RequiredFullPath -Path $ChecksumsPath -Description 'checksums.txt'
            $releaseMetadataPath = Get-RequiredFullPath -Path $ReleaseMetadataPath -Description 'release-metadata.json'
            $assetName = [System.IO.Path]::GetFileName($archivePath)
            $mismatchMetadataPath = New-MetadataMismatchFile -ReleaseMetadataPath $releaseMetadataPath -AssetName $assetName -DestinationDirectory $tempRoot
            Invoke-ExpectedFailure -ScenarioName $Scenario -ExpectedMessageFragment 'did not match checksums.txt' -Action {
                $null = Assert-WindowsArchiveIntegrity -ArchivePath $archivePath -AssetName $assetName -ChecksumsPath $checksumsPath -ReleaseMetadataPath $mismatchMetadataPath
            }
        }
    }
}
finally
{
    if ($KeepArtifacts)
    {
        Write-Host "Kept verification fixtures in '$tempRoot'."
    }
    elseif (Test-Path $tempRoot)
    {
        Remove-Item -Path $tempRoot -Recurse -Force
    }
}
