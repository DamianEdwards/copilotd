$ErrorActionPreference = 'Stop'

$scriptDir = Split-Path -Parent $PSCommandPath
$projectDir = Join-Path $scriptDir 'src\copilotd'
$appHost = Join-Path $scriptDir 'artifacts\bin\copilotd\debug\copilotd.exe'

if (-not $env:COPILOTD_HOME) {
    $env:COPILOTD_HOME = Join-Path $scriptDir '.copilotd-home'
}

if ($args.Count -gt 0 -and $args[0] -eq 'run') {
    if (-not (Test-Path $appHost -PathType Leaf)) {
        [Console]::Error.WriteLine("Built app host not found at '$appHost'. Run .\build.ps1 first.")
        exit 1
    }

    & $appHost @args
    exit $LASTEXITCODE
}

& dotnet run --project $projectDir --no-build -- @args
exit $LASTEXITCODE
