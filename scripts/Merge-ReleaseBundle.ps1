[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$InputDirectory,

    [Parameter(Mandatory)]
    [string]$OutputDirectory,

    [Parameter(Mandatory)]
    [string]$Version
)

$ErrorActionPreference = 'Stop'

$inputDirectory = [System.IO.Path]::GetFullPath($InputDirectory)
$outputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)

if (-not (Test-Path $inputDirectory))
{
    throw "Input directory '$inputDirectory' was not found."
}

New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null

$metadataFiles = Get-ChildItem -Path $inputDirectory -Recurse -Filter '*.json' |
    Where-Object { $_.Name -ne 'release-metadata.json' }

if ($metadataFiles.Count -eq 0)
{
    throw "No per-asset metadata files were found under '$inputDirectory'."
}

$assets = foreach ($metadataFile in $metadataFiles)
{
    $metadata = Get-Content -Path $metadataFile.FullName -Raw | ConvertFrom-Json
    if ($metadata.version -ne $Version)
    {
        throw "Metadata file '$($metadataFile.FullName)' reported version '$($metadata.version)' instead of '$Version'."
    }

    $assetPath = Get-ChildItem -Path $inputDirectory -Recurse -File | Where-Object { $_.Name -eq $metadata.assetName } | Select-Object -First 1
    if (-not $assetPath)
    {
        throw "Asset '$($metadata.assetName)' referenced by '$($metadataFile.FullName)' was not found."
    }

    $destinationPath = Join-Path $outputDirectory $assetPath.Name
    Copy-Item $assetPath.FullName $destinationPath -Force

    [ordered]@{
        name = $metadata.assetName
        runtimeIdentifier = $metadata.runtimeIdentifier
        platform = $metadata.platform
        architecture = $metadata.architecture
        fileType = $metadata.fileType
        commandName = $metadata.commandName
        sha256 = $metadata.sha256
    }
}

$sortedAssets = @($assets | Sort-Object name)
$checksumsPath = Join-Path $outputDirectory 'checksums.txt'
$checksums = $sortedAssets | ForEach-Object { "$($_.sha256)  $($_.name)" }
$checksums | Set-Content -Path $checksumsPath

$releaseMetadata = [ordered]@{
    version = $Version
    assets = $sortedAssets
    windowsAssets = @($sortedAssets | Where-Object { $_.platform -eq 'win' })
}

$releaseMetadataPath = Join-Path $outputDirectory 'release-metadata.json'
$releaseMetadata | ConvertTo-Json -Depth 5 | Set-Content -Path $releaseMetadataPath
