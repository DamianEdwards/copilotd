[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$fixtureDirectory = Join-Path ([System.IO.Path]::GetTempPath()) ("copilotd-installer-provenance-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixtureDirectory | Out-Null

try
{
    # v0.0.16 has a valid signature whose timestamp certificate expired in October 2026.
    gh release download v0.0.16 --repo DamianEdwards/copilotd --pattern copilotd-win-x64.zip --dir $fixtureDirectory
    if ($LASTEXITCODE -ne 0)
    {
        throw 'Failed to download the installer provenance fixture.'
    }

    Expand-Archive -LiteralPath (Join-Path $fixtureDirectory 'copilotd-win-x64.zip') -DestinationPath $fixtureDirectory
    $binaryPath = Join-Path $fixtureDirectory 'copilotd.exe'
    $scenarioScriptPath = Join-Path $PSScriptRoot 'Test-InstallerProvenance.ps1'
    foreach ($scenario in @('ExpiredTimestamp', 'GoodBinary', 'TamperedBinary', 'WrongSubject', 'WrongIssuer'))
    {
        & $scenarioScriptPath -Scenario $scenario -BinaryPath $binaryPath
    }
    & $scenarioScriptPath -Scenario UnsignedBinary -BinaryPath $scenarioScriptPath
}
finally
{
    Remove-Item -LiteralPath $fixtureDirectory -Recurse -Force
}
