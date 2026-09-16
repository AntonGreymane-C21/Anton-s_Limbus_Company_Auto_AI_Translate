@echo off
chcp 65001 >nul
title 边狱巴士自动增量汉化工具 - 命令行工具
cd /d "%~dp0"

echo ============================================
echo   命令行模式：执行 Diff 分析
echo   默认使用 data\testdata\ 下的测试数据
echo ============================================
echo.

rem 第7轮：只允许使用当前真实的输出目录（TargetFramework = net10.0-windows）。
rem 历史遗留的 bin\Debug\net10.0\ 目录曾导致双击启动到过期程序，这里不再引用它。
set CLI=src\LimbusTranslator.Cli\bin\Debug\net10.0-windows\LimbusTranslator.Cli.exe
set CLI_PROJ=src\LimbusTranslator.Cli\LimbusTranslator.Cli.csproj

echo [调试] 正在构建命令行工具（确保运行的是最新代码）...
dotnet build "%CLI_PROJ%" -c Debug --nologo -v:q
if errorlevel 1 (
    echo.
    echo [错误] 构建失败，没有启动任何程序。请检查 .NET SDK 与源码状态。
    pause
    exit /b 1
)

if not exist "%CLI%" (
    echo.
    echo [错误] 未找到命令行程序: %CLI%
    echo [错误] 当前项目 TargetFramework 应为 net10.0-windows，请检查 csproj 是否被改动。
    pause
    exit /b 1
)

"%CLI%" %*
set EXITCODE=%ERRORLEVEL%

echo.
echo [调试] 命令行工具退出码: %EXITCODE%
pause
exit /b %EXITCODE%
