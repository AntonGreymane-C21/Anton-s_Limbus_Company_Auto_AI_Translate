@echo off
chcp 65001 >nul
title 边狱巴士自动增量汉化工具 - 启动器
cd /d "%~dp0"

echo ============================================
echo   《边狱巴士》自动增量汉化工具 启动器
echo ============================================
echo.

rem 第9.0C.3.1：改为「每次都先构建再启动」。
rem 旧版本只在 exe 不存在时才构建，导致改完代码后双击本脚本仍然运行旧程序。
set PROJ=src\LimbusTranslator.Wpf\LimbusTranslator.Wpf.csproj
set EXE=src\LimbusTranslator.Wpf\bin\Debug\net10.0-windows\LimbusTranslator.Wpf.exe

echo [调试] 正在构建图形界面（确保运行的是最新代码）...
dotnet build "%PROJ%" -c Debug --nologo -v:q
if errorlevel 1 (
    echo.
    echo [错误] 构建失败，没有启动任何程序。
    echo [错误] 常见原因：旧窗口仍在运行导致文件被占用，请先关闭本工具的所有窗口后重试。
    pause
    exit /b 1
)

if not exist "%EXE%" (
    echo [错误] 未找到图形界面程序: %EXE%
    echo [错误] 当前项目 TargetFramework 应为 net10.0-windows，请检查 csproj 是否被改动。
    pause
    exit /b 1
)

echo [调试] 构建成功，启动程序...
start "" "%EXE%"
exit /b 0
