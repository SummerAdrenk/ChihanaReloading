@echo off
REM 部署用启动脚本 — 放在根目录
REM 目录结构：
REM   根目录/
REM     run.bat              ← 本文件（复制到根目录后改名为 run.bat）
REM     scr/CxdecReloading/  ← 主控程序
REM     scr/KrkrExtractForCxdecV2/
REM     scr/FreeMote/
REM     scr/LE/
chcp 65001 >nul
cd /d "%~dp0"
"%~dp0scr\CxdecReloading\CxdecReloading.exe" %*
pause
