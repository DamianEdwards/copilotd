$ErrorActionPreference = 'Stop'

$scriptDir = Split-Path -Parent $PSCommandPath
$projectDir = Join-Path $scriptDir 'src\copilotd'
$projectFile = Join-Path $projectDir 'copilotd.csproj'
$appHost = Join-Path $scriptDir 'artifacts\bin\copilotd\debug\copilotd.exe'

if ($args.Count -gt 0 -and $args[0] -eq 'run') {
    & dotnet build $projectFile -nologo
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }

    & $appHost @args
    exit $LASTEXITCODE
}

& dotnet run --project $projectDir -- @args
exit $LASTEXITCODE
