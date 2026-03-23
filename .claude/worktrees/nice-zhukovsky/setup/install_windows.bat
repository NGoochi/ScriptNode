@echo off
REM ScriptNode Plugin Installer — Windows
REM Run from the repo root directory

set GH_LIBRARIES=%APPDATA%\Grasshopper\Libraries
set GHA_SOURCE=src\bin\Release\net7.0\ScriptNodePlugin.gha

echo ════════════════════════════════════════════
echo  ScriptNode Plugin Installer (Windows)
echo ════════════════════════════════════════════

REM Check if source exists
if not exist "%GHA_SOURCE%" (
    echo [ERROR] Could not find %GHA_SOURCE%
    echo         Build the plugin first: cd src ^& dotnet build -c Release
    echo         Or use a pre-built .gha from the releases page.
    pause
    exit /b 1
)

REM Check if GH Libraries folder exists
if not exist "%GH_LIBRARIES%" (
    echo [ERROR] Grasshopper Libraries folder not found: %GH_LIBRARIES%
    echo         Is Rhino 8 installed? Open Grasshopper once to create the folder.
    pause
    exit /b 1
)

REM Check if Rhino is running
tasklist /FI "IMAGENAME eq Rhino.exe" 2>NUL | find /I /N "Rhino.exe" >NUL
if "%ERRORLEVEL%"=="0" (
    echo [WARNING] Rhino is currently running. 
    echo           Close Rhino before installing, or the file may be locked.
    echo.
    set /p CONTINUE="Continue anyway? (y/n): "
    if /i not "%CONTINUE%"=="y" exit /b 0
)

REM Copy
echo Copying ScriptNodePlugin.gha to %GH_LIBRARIES%...
copy /Y "%GHA_SOURCE%" "%GH_LIBRARIES%\ScriptNodePlugin.gha"

REM Unblock (PowerShell)
echo Unblocking file...
powershell -Command "Unblock-File -Path '%GH_LIBRARIES%\ScriptNodePlugin.gha'" 2>NUL

echo.
echo [DONE] Plugin installed.
echo        Start Rhino + Grasshopper and look for ScriptNode in the Script tab.
echo.
pause
