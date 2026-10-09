@echo off
setlocal
cd /d "%~dp0"

rem Publish to the canonical folders under ..\发布母本; no timestamped output folders.
node "..\work\run_publish.js"
set "PUBLISH_EXIT=%ERRORLEVEL%"
echo.
if "%PUBLISH_EXIT%"=="0" (
    echo Publish complete. Updated the canonical 发布母本 folders.
) else (
    echo Publish failed. Existing 发布母本 was preserved if compilation failed.
)
echo.
pause
exit /b %PUBLISH_EXIT%
