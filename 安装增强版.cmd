@echo off
chcp 65001 >nul
where pwsh.exe >nul 2>nul
if errorlevel 1 (
  echo 请先安装 PowerShell 7，再重新运行本文件。
  echo 下载说明：https://learn.microsoft.com/powershell/scripting/install/installing-powershell-on-windows
  pause
  exit /b 1
)
pwsh.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install.ps1" -Mode Install
pause
