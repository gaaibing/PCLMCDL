@echo off
chcp 65001 >nul
cd /d "%~dp0src"

echo === 发布 PCLMCDL（单文件，约 320 KB）===
dotnet publish -c Release -o "%~dp0dist"

if errorlevel 1 (
  echo.
  echo 发布失败
  pause
  exit /b 1
)

echo.
echo 输出目录: %~dp0dist
dir /b "%~dp0dist"
echo.
echo 运行前请确认已安装 .NET 10 桌面运行时:
echo https://dotnet.microsoft.com/zh-cn/download/dotnet/10.0
echo.
pause
