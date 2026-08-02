@echo off
chcp 65001 >nul 2>&1
echo ========================================
echo   DeskOrganizer v2.6.2 Replacement
echo ========================================
echo.

set "SRC=d:\code\Trae\桌面布局小工具net8.0-windows\publish\v2.6.2\DeskOrganizer_v2.exe"
set "DST=C:\Users\22145\OneDrive\桌面\电脑小工具\桌面管理器\DeskOrganizer_v2.exe"
set "UPDATER=C:\Users\22145\OneDrive\桌面\电脑小工具\桌面管理器\DeskOrganizerUpdater.exe"

echo [1/4] Checking new version file...
if not exist "%SRC%" (
    echo ERROR: Source file not found: %SRC%
    pause
    exit /b 1
)
echo OK

echo [2/4] Checking if app is running...
tasklist /fi "imagename eq DeskOrganizer_v2.exe" 2>nul | find "DeskOrganizer_v2.exe" >nul 2>&1
if not errorlevel 1 (
    echo App is running, closing...
    taskkill /f /im DeskOrganizer_v2.exe >nul 2>&1
    timeout /t 2 /nobreak >nul
)
echo OK

echo [3/4] Replacing main exe...
copy /y "%SRC%" "%DST%" >nul 2>&1
if errorlevel 1 (
    echo ERROR: Copy failed! Please close the app and try again.
    pause
    exit /b 1
)
echo OK

echo [4/4] Removing old Updater.exe...
if exist "%UPDATER%" (
    del "%UPDATER%" >nul 2>&1
    if exist "%UPDATER%" (
        echo WARNING: Could not delete Updater.exe, please delete manually.
    ) else (
        echo Deleted.
    )
) else (
    echo Not found, skipping.
)

echo.
echo ========================================
echo  Done! Starting new version...
echo ========================================
start "" "%DST%"
timeout /t 3 >nul
del "%~f0" >nul 2>&1
