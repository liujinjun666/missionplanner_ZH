@echo off
setlocal

REM ============================================================
REM  Swiwin ECU bidirectional control - one-click installer
REM  (Pure ASCII: avoid cmd codepage/encoding issues on any PC)
REM ============================================================

echo =================================================
echo   Swiwin ECU Control - Mission Planner Installer
echo =================================================
echo.

REM ---- Locate Mission Planner install dir ----
set "MP_DIR="
if exist "C:\Program Files (x86)\Mission Planner\plugins" set "MP_DIR=C:\Program Files (x86)\Mission Planner"
if exist "C:\Program Files\Mission Planner\plugins" set "MP_DIR=C:\Program Files\Mission Planner"

if "%MP_DIR%"=="" (
    echo [ERROR] Mission Planner not found.
    echo         Please copy "MP-plugin\zh-ecu.cs" manually into the
    echo         Mission Planner "plugins" folder.
    goto :manual
)

echo [1/2] Mission Planner dir: %MP_DIR%

REM ---- Copy plugin ----
echo [2/2] Copying zh-ecu.cs to plugins folder ...
copy /Y "%~dp0MP-plugin\zh-ecu.cs" "%MP_DIR%\plugins\zh-ecu.cs" >nul
if errorlevel 1 (
    echo [ERROR] Copy failed. Try right-click this .bat and
    echo         "Run as administrator".
    goto :manual
)
echo       Plugin copied OK.

echo.
echo =================================================
echo   INSTALL DONE - next manual steps:
echo =================================================
echo.
echo  Ground station:
echo   1. Close Mission Planner completely, then start it again
echo      (plugins load only at startup).
echo   2. Flight Data page: tick the "XuanYun ECU" checkbox,
echo      left of the "Auto pan" checkbox.
echo.
echo  Flight controller (do manually, see README):
echo   3. Copy "FC-script\swiwin_ecu_bridge.lua" to the SD card
echo      folder APM\scripts\  (must be 23588 bytes, do not truncate)
echo   4. Set params: SCR_ENABLE=1, SCR_VM_I_COUNT=200000,
echo      SERIALx_PROTOCOL=28, SERIALx_BAUD=9600
echo      (x = the serial port wired to the ECU)
echo   5. Reboot the flight controller.
echo.
echo  Full details: see the deployment doc (.md files).
echo.
pause
exit /b 0

:manual
echo.
echo  Manual steps (see deployment doc for details):
echo   1. Copy "MP-plugin\zh-ecu.cs" into Mission Planner "plugins" folder
echo   2. Restart MP, tick "XuanYun ECU" on Flight Data page
echo   3. Upload "FC-script\swiwin_ecu_bridge.lua" to SD card APM\scripts\
echo   4. Set params: SCR_ENABLE=1, SCR_VM_I_COUNT=200000,
echo      SERIALx_PROTOCOL=28, SERIALx_BAUD=9600
echo.
pause
exit /b 0
