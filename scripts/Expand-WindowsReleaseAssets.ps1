[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$BundleDirectory,

    [Parameter(Mandatory)]
    [string]$WorkingDirectory
)

$ErrorActionPreference = 'Stop'

$bundleDirectory = [System.IO.Path]::GetFullPath($BundleDirectory)
$workingDirectory = [System.IO.Path]::GetFullPath($WorkingDirectory)
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
$windowsAssets = @($metadata.windowsAssets)
if ($windowsAssets.Count -eq 0)
{
    $windowsAssets = @($metadata.assets | Where-Object { $_.platform -eq 'win' })
}

if ($windowsAssets.Count -eq 0)
{
    throw "No Windows assets were found in '$metadataPath'."
}

New-Item -ItemType Directory -Path $workingDirectory -Force | Out-Null
$manifestEntries = @()

foreach ($asset in $windowsAssets)
{
    if ([string]::IsNullOrWhiteSpace($asset.runtimeIdentifier))
    {
        throw "Windows asset '$($asset.name)' is missing a runtimeIdentifier."
    }

    $archivePath = Join-Path $bundleDirectory $asset.name
    if (-not (Test-Path $archivePath))
    {
        throw "Archive '$archivePath' referenced in release metadata was not found."
    }

    $assetDirectory = Join-Path $workingDirectory $asset.runtimeIdentifier
    if (Test-Path $assetDirectory)
    {
        Remove-Item $assetDirectory -Recurse -Force
    }

    New-Item -ItemType Directory -Path $assetDirectory -Force | Out-Null
    Expand-Archive -Path $archivePath -DestinationPath $assetDirectory -Force

    $binaryPath = Join-Path $assetDirectory 'copilotd.exe'
    if (-not (Test-Path $binaryPath))
    {
        throw "Expanded Windows asset '$($asset.name)' did not contain 'copilotd.exe'."
    }

    $manifestEntries += [ordered]@{
        assetName = $asset.name
        runtimeIdentifier = $asset.runtimeIdentifier
        stagingDirectory = $assetDirectory
    }
}

$manifestPath = Join-Path $workingDirectory 'windows-assets-manifest.json'
$manifestEntries | ConvertTo-Json -Depth 5 | Set-Content -Path $manifestPath
