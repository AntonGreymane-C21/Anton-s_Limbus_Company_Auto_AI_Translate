@echo off
title 推送到 GitHub（公开分支 main；docs 内部报告不参与）
cd /d "%~dp0"

echo ============================================
echo   推送到 GitHub：main 分支（不含 docs 内部报告）
echo ============================================
echo.

rem 公开分支是 main；docs 下的内部报告已被 .gitignore 排除，不会上传到 GitHub。
rem 本地另有历史归档分支 master（含报告），不要推送它。

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

rem 3) 推送到 origin/main
echo [调试] 正在推送到 origin/main ...
git push origin main
set PUSHCODE=%ERRORLEVEL%

echo.
if %PUSHCODE%==0 (
    echo [调试] 推送完成。内部报告仍只保留在本地。
) else (
    echo [错误] 推送失败（退出码 %PUSHCODE%）。若尚未配置远程仓库，请先执行：
    echo         git remote add origin https://github.com/你的账号/仓库名.git
)
pause
exit /b %PUSHCODE%
