@echo off
if /I "%~1"=="run" goto run_direct

setlocal
set "SCRIPT_DIR=%~dp0"
set "PROJECT_DIR=%SCRIPT_DIR%src\copilotd"
if not defined COPILOTD_HOME set "COPILOTD_HOME=%SCRIPT_DIR%.copilotd-home"

dotnet run --project "%PROJECT_DIR%" --no-build -- %*
set "EXITCODE=%ERRORLEVEL%"
endlocal & exit /b %EXITCODE%

:run_direct
setlocal
set "SCRIPT_DIR=%~dp0"
set "APP_HOST=%SCRIPT_DIR%artifacts\bin\copilotd\debug\copilotd.exe"
if not defined COPILOTD_HOME set "COPILOTD_HOME=%SCRIPT_DIR%.copilotd-home"
for /f %%I in ('powershell -NoLogo -NoProfile -ExecutionPolicy Bypass -Command "$ErrorActionPreference = 'Stop'; $me = Get-CimInstance Win32_Process -Filter ('ProcessId=' + $PID); $ancestorId = $me.ParentProcessId; $isPowerShell = $false; while ($ancestorId -gt 0) { $ancestor = Get-CimInstance Win32_Process -Filter ('ProcessId=' + $ancestorId); if ($null -eq $ancestor) { break }; if ($ancestor.Name -in @('powershell.exe', 'pwsh.exe')) { $isPowerShell = $true; break }; $ancestorId = $ancestor.ParentProcessId }; if ($isPowerShell) { '1' } else { '0' }" 2^>nul') do set "COPILOTD_PARENT_POWERSHELL=%%I"
if "%COPILOTD_PARENT_POWERSHELL%"=="1" (
    echo copilotd.cmd run does not handle Ctrl+C reliably when launched from PowerShell.
    echo Use .\copilotd.ps1 run instead.
    endlocal & exit /b 1
)

if not exist "%APP_HOST%" (
    echo Built app host not found at "%APP_HOST%". Run build.cmd first. 1>&2
    endlocal & exit /b 1
)

"%APP_HOST%" %*
set "EXITCODE=%ERRORLEVEL%"
endlocal & exit /b %EXITCODE%
