@echo off
setlocal EnableExtensions
chcp 65001 >nul

REM ---------------------------------------------------------------------------
REM Install the locally built dsh-tray.exe over the per-user installation.
REM
REM Build first:   dotnet publish -c Release -r win-x64
REM Then run this script (double-click is fine).
REM
REM Why the tray must be stopped first:
REM   - Windows locks a running exe, so the copy would fail.
REM   - Starting a second tray does NOT replace the first one: the single
REM     instance check makes it send "reopen" to the running instance and exit,
REM     so a new build would never actually take over.
REM Stopping the tray also closes the DSH App windows and stops the dsh web
REM service; this script starts everything again at the end.
REM ---------------------------------------------------------------------------

set "SRC=%~dp0bin\Release\net10.0-windows\win-x64\publish\dsh-tray.exe"

set "DESTDIR=%LOCALAPPDATA%\Programs\DeepSeek Harness Tray"
if not exist "%DESTDIR%\dsh-tray.exe" set "DESTDIR=%LOCALAPPDATA%\Programs\DSH Tray"
set "DEST=%DESTDIR%\dsh-tray.exe"

if not exist "%SRC%" (
  echo [install] ERROR: build output not found:
  echo [install]   %SRC%
  echo [install] Run: dotnet publish -c Release -r win-x64
  exit /b 1
)

if not exist "%DEST%" (
  echo [install] ERROR: no installed dsh-tray.exe found under:
  echo [install]   %LOCALAPPDATA%\Programs\DeepSeek Harness Tray
  echo [install]   %LOCALAPPDATA%\Programs\DSH Tray
  echo [install] Install the tray first, or run the build output directly.
  exit /b 1
)

echo [install] source : %SRC%
echo [install] target : %DEST%
echo.
echo [install] Stopping the running tray (the DSH App window closes and the
echo           dsh web service stops; both come back at the end)...
"%DEST%" --stop >nul 2>&1
timeout /t 3 /nobreak >nul

tasklist /fi "imagename eq dsh-tray.exe" 2>nul | find /i "dsh-tray.exe" >nul
if not errorlevel 1 (
  echo [install] Tray is still running, stopping it forcefully...
  taskkill /im dsh-tray.exe /f >nul 2>&1
  timeout /t 2 /nobreak >nul
)

echo [install] Copying the new build...
copy /y "%SRC%" "%DEST%" >nul
if errorlevel 1 (
  echo [install] ERROR: copy failed; dsh-tray.exe is most likely still running.
  exit /b 1
)

echo [install] Installed. Starting the tray...
start "" "%DEST%"
echo [install] Done.
exit /b 0
