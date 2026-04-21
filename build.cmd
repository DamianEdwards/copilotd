@echo off
setlocal

dotnet build "%~dp0src\copilotd\copilotd.csproj" --no-logo %*
set "EXITCODE=%ERRORLEVEL%"
endlocal & exit /b %EXITCODE%
