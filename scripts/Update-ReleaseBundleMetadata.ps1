[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$BundleDirectory
)

$ErrorActionPreference = 'Stop'

$bundleDirectory = [System.IO.Path]::GetFullPath($BundleDirectory)
$metadataPath = Join-Path $bundleDirectory 'release-metadata.json'

if (-not (Test-Path $bundleDirectory))
{
    throw "Bundle directory '$bundleDirectory' was not found."
}

if (-not (Test-Path $metadataPath))
{
    throw "Release metadata file '$metadataPath' was not found."
}

$metadata = Get-Content -Path $metadataPath -Raw | ConvertFrom-Json
if ([string]::IsNullOrWhiteSpace($metadata.version))
{
    throw "Release metadata in '$metadataPath' did not contain a version."
}

$updatedAssets =
    @($metadata.assets |
        ForEach-Object {
            $assetPath = Join-Path $bundleDirectory $_.name
            if (-not (Test-Path $assetPath))
            {
                throw "Release asset '$assetPath' was not found."
            }

            [ordered]@{
                name = $_.name
                runtimeIdentifier = $_.runtimeIdentifier
                platform = $_.platform
                architecture = $_.architecture
                fileType = $_.fileType
                commandName = $_.commandName
                sha256 = (Get-FileHash -Path $assetPath -Algorithm SHA256).Hash.ToLowerInvariant()
            }
        } |
        Sort-Object name)

$checksumsPath = Join-Path $bundleDirectory 'checksums.txt'
$checksums = $updatedAssets | ForEach-Object { "$($_.sha256)  $($_.name)" }
$checksums | Set-Content -Path $checksumsPath

$updatedMetadata = [ordered]@{
    version = $metadata.version
    assets = $updatedAssets
    windowsAssets = @($updatedAssets | Where-Object { $_.platform -eq 'win' })
}

$updatedMetadata | ConvertTo-Json -Depth 5 | Set-Content -Path $metadataPath
