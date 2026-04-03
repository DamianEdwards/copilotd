[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$WorkingDirectory,

    [Parameter(Mandatory)]
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'

$workingDirectory = [System.IO.Path]::GetFullPath($WorkingDirectory)
$outputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
$manifestPath = Join-Path $workingDirectory 'windows-assets-manifest.json'

if (-not (Test-Path $manifestPath))
{
    throw "Windows asset manifest '$manifestPath' was not found."
}

$manifestEntries = @(Get-Content -Path $manifestPath -Raw | ConvertFrom-Json)
if ($manifestEntries.Count -eq 0)
{
    throw "Windows asset manifest '$manifestPath' did not contain any entries."
}

New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem

foreach ($entry in $manifestEntries)
{
    $stagingDirectory = [System.IO.Path]::GetFullPath($entry.stagingDirectory)
    if (-not (Test-Path $stagingDirectory))
    {
        throw "Staging directory '$stagingDirectory' was not found."
    }

    $binaryPath = Join-Path $stagingDirectory 'copilotd.exe'
    if (-not (Test-Path $binaryPath))
    {
        throw "Staging directory '$stagingDirectory' does not contain 'copilotd.exe'."
    }

    $licensePath = Join-Path $stagingDirectory 'LICENSE'
    if (-not (Test-Path $licensePath))
    {
        throw "Staging directory '$stagingDirectory' does not contain 'LICENSE'."
    }

    $archivePath = Join-Path $outputDirectory $entry.assetName
    if (Test-Path $archivePath)
    {
        Remove-Item $archivePath -Force
    }

    [System.IO.Compression.ZipFile]::CreateFromDirectory($stagingDirectory, $archivePath)
}
