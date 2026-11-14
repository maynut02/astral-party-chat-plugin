@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1" %*
set "INSTALL_EXIT_CODE=%ERRORLEVEL%"
echo.
pause
exit /b %INSTALL_EXIT_CODE%
