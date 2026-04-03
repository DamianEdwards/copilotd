[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$RuntimeIdentifier,

    [Parameter(Mandatory)]
    [string]$Version,

    [Parameter(Mandatory)]
    [string]$ArtifactsDirectory,

    [string]$ProjectPath = (Join-Path $PSScriptRoot '..\src\copilotd\copilotd.csproj'),

    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

$projectPath = [System.IO.Path]::GetFullPath($ProjectPath)
$artifactsDirectory = [System.IO.Path]::GetFullPath($ArtifactsDirectory)
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$publishRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("copilotd-publish-" + [System.Guid]::NewGuid().ToString('N'))
$publishDirectory = Join-Path $publishRoot $RuntimeIdentifier
$stagingDirectory = Join-Path $publishRoot ("stage-" + $RuntimeIdentifier)

function Quote-CmdArgument
{
    param([Parameter(Mandatory)][string]$Value)

    if ([string]::IsNullOrEmpty($Value))
    {
        return '""'
    }

    if ($Value -notmatch '[\s"&()^<>|]')
    {
        return $Value
    }

    return '"' + $Value.Replace('"', '""') + '"'
}

function Get-VsDevCmdPath
{
    if (-not $IsWindows)
    {
        return $null
    }

    $vsWherePath = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path $vsWherePath))
    {
        return $null
    }

    $installationPath = & $vsWherePath -latest -products * -property installationPath
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($installationPath))
    {
        return $null
    }

    $vsDevCmdPath = Join-Path $installationPath.Trim() 'Common7\Tools\VsDevCmd.bat'
    if (-not (Test-Path $vsDevCmdPath))
    {
        return $null
    }

    return $vsDevCmdPath
}

function Get-WindowsTargetArchitecture
{
    param([Parameter(Mandatory)][string]$Rid)

    $architecture = $Rid.Split('-', 2)[1]
    switch ($architecture)
    {
        'x64' { 'amd64' }
        'arm64' { 'arm64' }
        default { throw "Unsupported Windows architecture '$Rid'." }
    }
}

function Invoke-DotNetPublish
{
    param(
        [Parameter(Mandatory)][string]$Rid,
        [Parameter(Mandatory)][string[]]$Arguments
    )

    $platform = $Rid.Split('-', 2)[0]
    if ($IsWindows -and $platform -eq 'win')
    {
        $vsDevCmdPath = Get-VsDevCmdPath
        if (-not [string]::IsNullOrWhiteSpace($vsDevCmdPath))
        {
            $targetArchitecture = Get-WindowsTargetArchitecture -Rid $Rid
            $commandSegments = @(
                'call',
                (Quote-CmdArgument -Value $vsDevCmdPath),
                '-no_logo',
                '-host_arch=amd64',
                "-arch=$targetArchitecture",
                '&&',
                'dotnet'
            ) + ($Arguments | ForEach-Object { Quote-CmdArgument -Value $_ })

            & cmd.exe /d /c ($commandSegments -join ' ')
            return
        }
    }

    & dotnet @Arguments
}

New-Item -ItemType Directory -Path $artifactsDirectory -Force | Out-Null
New-Item -ItemType Directory -Path $publishDirectory -Force | Out-Null

try
{
    $publishArguments = @(
        'publish',
        $projectPath,
        '--configuration', $Configuration,
        '--runtime', $RuntimeIdentifier,
        '--self-contained', 'true',
        '--nologo',
        '--tl:off',
        "-p:Version=$Version",
        '-p:ContinuousIntegrationBuild=true',
        '-o', $publishDirectory
    )

    Invoke-DotNetPublish -Rid $RuntimeIdentifier -Arguments $publishArguments

    if ($LASTEXITCODE -ne 0)
    {
        throw "dotnet publish failed for runtime '$RuntimeIdentifier'. Ensure the required NativeAOT toolchain is available for this platform."
    }

    $parts = $RuntimeIdentifier.Split('-', 2)
    if ($parts.Length -ne 2)
    {
        throw "Unexpected runtime identifier format '$RuntimeIdentifier'."
    }

    $platform = $parts[0]
    $architecture = $parts[1]
    $binaryName = if ($platform -eq 'win') { 'copilotd.exe' } else { 'copilotd' }
    $binaryPath = Join-Path $publishDirectory $binaryName

    if (-not (Test-Path $binaryPath))
    {
        throw "Published binary '$binaryPath' was not found."
    }

    New-Item -ItemType Directory -Path $stagingDirectory -Force | Out-Null
    Copy-Item $binaryPath (Join-Path $stagingDirectory $binaryName) -Force
    Copy-Item (Join-Path $repoRoot 'LICENSE') (Join-Path $stagingDirectory 'LICENSE') -Force

    $assetPath =
        if ($platform -eq 'win')
        {
            $path = Join-Path $artifactsDirectory "copilotd-$RuntimeIdentifier.zip"
            if (Test-Path $path)
            {
                Remove-Item $path -Force
            }

            Add-Type -AssemblyName System.IO.Compression.FileSystem
            [System.IO.Compression.ZipFile]::CreateFromDirectory($stagingDirectory, $path)
            $path
        }
        else
        {
            $path = Join-Path $artifactsDirectory "copilotd-$RuntimeIdentifier.tar.gz"
            if (Test-Path $path)
            {
                Remove-Item $path -Force
            }

            tar -czf $path -C $stagingDirectory .
            if ($LASTEXITCODE -ne 0)
            {
                throw "Failed to create archive '$path'."
            }

            $path
        }

    $hash = (Get-FileHash $assetPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $assetName = [System.IO.Path]::GetFileName($assetPath)
    $hashPath = Join-Path $artifactsDirectory ($assetName + '.sha256')
    "$hash  $assetName" | Set-Content -Path $hashPath -NoNewline

    $metadata = [ordered]@{
        version = $Version
        runtimeIdentifier = $RuntimeIdentifier
        platform = $platform
        architecture = $architecture
        assetName = $assetName
        fileType = if ($platform -eq 'win') { 'zip' } else { 'tar.gz' }
        commandName = 'copilotd'
        sha256 = $hash
    }

    $metadataPath = Join-Path $artifactsDirectory ("copilotd-$RuntimeIdentifier.json")
    $metadata | ConvertTo-Json -Depth 5 | Set-Content -Path $metadataPath
}
finally
{
    if (Test-Path $publishRoot)
    {
        Remove-Item $publishRoot -Recurse -Force
    }
}
