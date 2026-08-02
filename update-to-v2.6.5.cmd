@echo off
chcp 65001 >nul 2>&1
echo ========================================
echo   DeskOrganizer v2.6.5 Replacement
echo ========================================
echo.

set "SRC=d:\code\Trae\桌面布局小工具net8.0-windows\publish\v2.6.5\DeskOrganizer_v2.exe"
set "DST=C:\Users\22145\OneDrive\桌面\电脑小工具\桌面管理器\DeskOrganizer_v2.exe"

echo [1/3] Checking new version file...
if not exist "%SRC%" (
    echo ERROR: Source file not found: %SRC%
    pause
    exit /b 1
)
echo OK

echo [2/3] Closing app if running...
tasklist /fi "imagename eq DeskOrganizer_v2.exe" 2>nul | find "DeskOrganizer_v2.exe" >nul 2>&1
if not errorlevel 1 (
    taskkill /f /im DeskOrganizer_v2.exe >nul 2>&1
    timeout /t 3 /nobreak >nul
)
echo OK

echo [3/3] Replacing exe...
copy /y "%SRC%" "%DST%" >nul 2>&1
if errorlevel 1 (
    echo ERROR: Copy failed!
    pause
    exit /b 1
)
echo OK

echo.
echo Starting v2.6.5...
start "" "%DST%"
timeout /t 3 >nul
del "%~f0" >nul 2>&1
