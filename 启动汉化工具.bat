@echo off
chcp 65001 >nul
title 边狱巴士自动增量汉化工具 - 启动器
cd /d "%~dp0"

echo ============================================
echo   《边狱巴士》自动增量汉化工具 启动器
echo ============================================
echo.

set EXE=src\LimbusTranslator.Wpf\bin\Debug\net10.0-windows\LimbusTranslator.Wpf.exe

if exist "%EXE%" (
    echo [调试] 直接启动已构建的程序...
    start "" "%EXE%"
    exit /b 0
)

echo [调试] 未找到已构建程序，开始构建...
dotnet build src\LimbusTranslator.Wpf\LimbusTranslator.Wpf.csproj -c Debug
if errorlevel 1 (
    echo.
    echo [错误] 构建失败，请检查代码或运行环境。
    pause
    exit /b 1
)

echo [调试] 构建成功，启动程序...
start "" "%EXE%"
exit /b 0
