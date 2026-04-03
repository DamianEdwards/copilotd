[CmdletBinding()]
param(
    [string]$RootPath = (Join-Path $PSScriptRoot '..')
)

$ErrorActionPreference = 'Stop'

$resolvedRoot = [System.IO.Path]::GetFullPath($RootPath)
$scriptFiles = @(Get-ChildItem -Path $resolvedRoot -Recurse -Filter '*.ps1' -File)
if ($scriptFiles.Count -eq 0)
{
    throw "No PowerShell scripts were found under '$resolvedRoot'."
}

$syntaxErrors = @()
foreach ($scriptFile in $scriptFiles)
{
    $tokens = $null
    $errors = $null
    [System.Management.Automation.Language.Parser]::ParseFile($scriptFile.FullName, [ref]$tokens, [ref]$errors) | Out-Null

    foreach ($error in @($errors))
    {
        $syntaxErrors += [ordered]@{
            Path = $scriptFile.FullName
            Line = $error.Extent.StartLineNumber
            Column = $error.Extent.StartColumnNumber
            Message = $error.Message
        }
    }
}

if ($syntaxErrors.Count -gt 0)
{
    $details = $syntaxErrors | ForEach-Object { "$($_.Path):$($_.Line):$($_.Column) $($_.Message)" }
    throw "PowerShell syntax validation failed:`n$($details -join [Environment]::NewLine)"
}

Write-Host "PowerShell syntax validation passed for $($scriptFiles.Count) script(s)."
