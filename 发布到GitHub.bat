@echo off
chcp 65001 >nul
title 推送到 GitHub（公开分支 main；docs 内部报告不参与）
cd /d "%~dp0"

rem ============================================================
rem  作者本地发布用脚本；普通使用者无需运行。
rem
rem  说明：
rem    · 公开分支是 main，日常开发也直接在 main 上提交；
rem    · docs 下的内部报告已被 .gitignore 排除，不会进入本仓库，
rem      因此不会上传到 GitHub（它们由 docs\ 自己的本地 git 仓库保管）；
rem    · 本地还保留了一个历史归档分支 master（含报告），不要推送它。
rem ============================================================

echo ============================================
echo   推送到 GitHub：main 分支（不含 docs 内部报告）
echo ============================================
echo.

rem 1) 工作区必须干净
git diff-index --quiet HEAD -- || (
    echo [错误] 当前工作区存在未提交改动，请先提交后再推送。
    pause
    exit /b 1
)

rem 2) 校验：本仓库跟踪的 docs 文件只应有 README.md
echo [调试] 当前仓库跟踪的 docs 文件：
git ls-files docs
echo.

rem 3) 推送
echo [调试] 正在推送到 origin/main ...
git push origin main
set PUSHCODE=%ERRORLEVEL%

echo.
if %PUSHCODE%==0 (
    echo [调试] 推送完成。内部报告仍只保留在本地。
) else (
    echo [错误] 推送失败（退出码 %PUSHCODE%）。若尚未配置远程仓库，请先执行：
    echo         git remote add origin https://github.com/^<你的账号^>/^<仓库名^>.git
)
pause
exit /b %PUSHCODE%
