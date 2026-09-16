@echo off
chcp 65001 >nul
title 发布到 GitHub（公开分支 main；不含 docs 内部报告）
cd /d "%~dp0"

rem ============================================================
rem  作者本地发布用脚本；普通使用者无需运行。
rem
rem  作用：
rem    1. 把本地开发分支 master（含 docs 内部报告）的内容同步到公开分支 main；
rem    2. 同步时剔除 docs 目录下的内部报告，只保留 docs\README.md；
rem    3. 提交并推送到 GitHub（origin/main）。
rem  注意：master 只留在本地，不要推送，以免内部报告上传。
rem ============================================================

echo ============================================
echo   发布到 GitHub：main 分支（不含内部报告）
echo ============================================
echo.

rem 0) 工作区必须干净：发布内容与本地 master 完全一致，避免混入未提交改动
git diff-index --quiet HEAD -- || (
    echo [错误] 当前工作区存在未提交改动，请先提交到 master（或放弃改动）后再发布。
    pause
    exit /b 1
)

rem 1) 切回 master 并确认存在
git switch master || (
    echo [错误] 未找到本地分支 master。
    pause
    exit /b 1
)

rem 2) 切到公开分支（若不存在则创建）
git switch main 2>nul || (
    echo [调试] 公开分支 main 不存在，正在创建...
    rem 注意：这里必须用 checkout --orphan（保留工作区），
    rem 不能用 switch --orphan（会清空工作区文件）。
    git checkout --orphan main || (
        echo [错误] 创建 main 失败。
        pause
        exit /b 1
    )
)

rem 3) 用 master 的内容重排索引（不含历史），再剔除 docs 下的内部报告
git rm -r --cached --quiet . >nul 2>&1
git checkout master -- .
git rm -r --cached --quiet docs >nul 2>&1
git checkout master -- docs/README.md

rem 4) 提交并推送
git commit -m "发布: 同步公开版本（不含内部报告）" -q
if errorlevel 1 (
    echo [调试] 内容与上次发布一致，无需提交。
)

echo [调试] 校验：公开分支不应包含 docs 下的报告...
git ls-files docs

echo.
echo [调试] 正在推送到 origin/main ...
git push origin main
set PUSHCODE=%ERRORLEVEL%

git switch master
echo.
if %PUSHCODE%==0 (
    echo [调试] 发布完成：GitHub 上 main 分支已更新（master 与内部报告仅保留在本地）。
) else (
    echo [错误] 推送失败（退出码 %PUSHCODE%），请检查远程仓库与网络。未上传任何内容。
)
pause
exit /b %PUSHCODE%
