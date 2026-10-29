@echo off
if "!"=="" (echo PathMan: disable delayed expansion before switching. 1>&2 & exit /b 1)
set "_PATHMAN_DIR=%~dp0"
set "_PATHMAN_TMP="
set "_PATHMAN_NAME="
set "_PATHMAN_VERSION="
set "_PATHMAN_SCOPE="
:parse
if "%~1"=="" goto run
if /i "%~1"=="/n" goto name
if /i "%~1"=="/v" goto version
if /i "%~1"=="/s" goto scope
if not defined _PATHMAN_NAME (set "_PATHMAN_NAME=%~1") else if not defined _PATHMAN_VERSION (set "_PATHMAN_VERSION=%~1") else goto usage
shift
goto parse
:name
if defined _PATHMAN_NAME goto usage
shift
set "_PATHMAN_NAME=%~1"
goto next
:version
if defined _PATHMAN_VERSION goto usage
shift
set "_PATHMAN_VERSION=%~1"
goto next
:scope
if defined _PATHMAN_SCOPE goto usage
shift
set "_PATHMAN_SCOPE=%~1"
:next
if "%~1"=="" goto usage
shift
goto parse
:run
if not defined _PATHMAN_NAME goto usage
if not defined _PATHMAN_VERSION goto usage
if not defined _PATHMAN_SCOPE goto usage
set "_PATHMAN_TMP=%TEMP%\PathMan-%RANDOM%-%RANDOM%.env"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%_PATHMAN_DIR%Switch-PathMan.ps1" -Name "%_PATHMAN_NAME%" -Version "%_PATHMAN_VERSION%" -Scope "%_PATHMAN_SCOPE%" -OutputFormat Cmd -OutputFile "%_PATHMAN_TMP%"
if not "%errorlevel%"=="0" goto failed
for /f "tokens=2 delims=:" %%C in ('chcp') do set "_PATHMAN_CP=%%C"
chcp 65001 >nul
for /f "usebackq delims=" %%E in ("%_PATHMAN_TMP%") do set "%%E"
chcp %_PATHMAN_CP% >nul
del /q "%_PATHMAN_TMP%" >nul 2>&1
call :cleanup
exit /b 0
:usage
echo Usage: switch-pathman [/n] name [/v] version /s reg^|shell^|all 1>&2
:failed
if defined _PATHMAN_TMP del /q "%_PATHMAN_TMP%" >nul 2>&1
call :cleanup
exit /b 1
:cleanup
set "_PATHMAN_NAME="
set "_PATHMAN_VERSION="
set "_PATHMAN_SCOPE="
set "_PATHMAN_TMP="
set "_PATHMAN_CP="
set "_PATHMAN_DIR="
exit /b
