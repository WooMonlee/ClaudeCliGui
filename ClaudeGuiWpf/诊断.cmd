@echo off
cd /d "%~dp0"
echo [1] 注册表中的 ANTHROPIC 变量:
reg query HKCU\Environment /v ANTHROPIC_API_KEY 2>nul && echo 已设置 || echo 未设置
reg query HKCU\Environment /v ANTHROPIC_BASE_URL 2>nul && echo 已设置 || echo 未设置

echo.
echo [2] 当前进程的 ANTHROPIC 变量:
if "%ANTHROPIC_API_KEY%"=="" (echo API_KEY=(空^)) else (echo API_KEY=已设置)
if "%ANTHROPIC_BASE_URL%"=="" (echo BASE_URL=(空^)) else (echo BASE_URL=%ANTHROPIC_BASE_URL%)

echo.
echo [3] 直接测试 claude (等10秒):
echo hi | claude -p "hi" --permission-mode bypassPermissions 2>&1 | findstr /C:"authentication" /C:"api_retry" /C:"error" | head -2
echo.

pause
