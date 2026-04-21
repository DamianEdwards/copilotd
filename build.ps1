$ErrorActionPreference = 'Stop'

$scriptDir = Split-Path -Parent $PSCommandPath
$projectFile = Join-Path $scriptDir 'src\copilotd\copilotd.csproj'

& dotnet build $projectFile --no-logo @args
exit $LASTEXITCODE
