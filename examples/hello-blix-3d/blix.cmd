@echo off
setlocal

set "BLIX_CHECKOUT=%BLIX_ROOT%"
if not defined BLIX_CHECKOUT set "BLIX_CHECKOUT=%~dp0engine\blix"

call "%BLIX_CHECKOUT%\blix.cmd" %*
exit /b %ERRORLEVEL%
