@echo off
title zh-serial 部署并重启 Mission Planner
echo 正在复制插件到 Mission Planner plugins 目录...
copy /Y "%~dp0zh-serial.cs" "C:\Program Files (x86)\Mission Planner\plugins\zh-serial.cs" >nul
if not exist "C:\Program Files (x86)\Mission Planner\plugins\zh-serial.cs" (
    echo.
    echo [失败] 权限不足。请右键本文件，选择“以管理员身份运行”。
    echo.
    pause
    exit /b
)
echo [成功] 已复制：
for %%F in ("C:\Program Files (x86)\Mission Planner\plugins\zh-serial.cs") do echo        %%~zF 字节  %%~tF  zh-serial.cs
echo.
echo 提示：装好后在 MP「飞行数据」页工具栏勾选「遥测通信监听」（在「飞控网口」右侧）。
echo.
choice /c YN /n /m "现在自动重启 Mission Planner 吗？(Y=是  N=否，我手动重启) "
if errorlevel 2 goto :end
echo 正在关闭 Mission Planner...
taskkill /IM MissionPlanner.exe >nul 2>&1
timeout /t 3 /nobreak >nul
taskkill /F /IM MissionPlanner.exe >nul 2>&1
timeout /t 2 /nobreak >nul
echo 正在启动 Mission Planner...
start "" "C:\Program Files (x86)\Mission Planner\MissionPlanner.exe"
:end
echo.
pause
