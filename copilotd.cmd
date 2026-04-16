@echo off
setlocal
set "SCRIPT_DIR=%~dp0"
set "PROJECT_DIR=%SCRIPT_DIR%src\copilotd"

if /I "%~1"=="run" goto run_direct

dotnet run --project "%PROJECT_DIR%" -- %*
set "EXITCODE=%ERRORLEVEL%"
endlocal & exit /b %EXITCODE%

:run_direct
dotnet build "%PROJECT_DIR%\copilotd.csproj" -nologo
if errorlevel 1 (
    set "EXITCODE=%ERRORLEVEL%"
    endlocal & exit /b %EXITCODE%
)

REM Run through START /B /WAIT so Ctrl+C targets the child process rather than
REM cmd.exe interrupting the batch job first. Keep this as the final batch
REM command to avoid the "Terminate batch job (Y/N)?" prompt path.
start "" /B /WAIT "%SCRIPT_DIR%artifacts\bin\copilotd\debug\copilotd.exe" %*
