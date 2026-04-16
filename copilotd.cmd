@echo off
setlocal
set "SCRIPT_DIR=%~dp0"
set "PROJECT_DIR=%SCRIPT_DIR%src\copilotd"

if /I "%~1"=="run" goto run_direct

dotnet run --project "%PROJECT_DIR%" -- %*
exit /b %ERRORLEVEL%

:run_direct
dotnet build "%PROJECT_DIR%\copilotd.csproj" -nologo
if errorlevel 1 exit /b %ERRORLEVEL%

"%SCRIPT_DIR%artifacts\bin\copilotd\debug\copilotd.exe" %*
exit /b %ERRORLEVEL%
