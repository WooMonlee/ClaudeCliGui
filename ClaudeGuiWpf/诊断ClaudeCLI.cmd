@echo off
chcp 65001 >nul
setlocal

echo ============================================
echo   Claude CLI 环境诊断
echo ============================================
echo.

echo [1] 环境变量（当前进程 + 注册表）：
echo.

for %%v in (ANTHROPIC_API_KEY ANTHROPIC_BASE_URL) do (
    call set "cur=%%%%v%%"
    if "!cur!"=="" (
        REM 从注册表读（setx 写入的）
        for /f "tokens=2* delims= " %%a in ('reg query "HKCU\Environment" /v %%v 2^>nul ^| findstr %%v') do set "cur=%%b"
    )
    if "!cur!"=="" (
        echo   %%v = (未设置^)
    ) else (
        if "%%v"=="ANTHROPIC_API_KEY" (
            echo   %%v = !cur:~0,8!...（已设置，长度 !cur! 字节^)
        ) else (
            echo   %%v = !cur!
        )
    )
)
echo.

echo [2] claude 版本：
claude --version 2>&1
echo.

echo [3] 测试 Anthropic 格式（用 %ANTHROPIC_BASE_URL%）：
powershell -Command "$u=$env:ANTHROPIC_BASE_URL;if(!$u){$u='(未设置)'};try{$r=Invoke-WebRequest -Uri '$u/v1/messages' -Method POST -Headers @{'x-api-key'=$env:ANTHROPIC_API_KEY;'anthropic-version'='2023-06-01'} -ContentType 'application/json' -Body '{\"model\":\"claude-sonnet-4-20250514\",\"max_tokens\":10,\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}';Write-Host \"HTTP $($r.StatusCode)\"}catch{Write-Host \"错误: $_\"}" 2>&1 | findstr /C:"HTTP" /C:"错误"

echo.

echo [4] 测试 DeepSeek 格式（api.deepseek.com）：
powershell -Command "try{$r=Invoke-WebRequest -Uri 'https://api.deepseek.com/v1/chat/completions' -Method POST -Headers @{'Authorization'=('Bearer '+$env:ANTHROPIC_API_KEY)} -ContentType 'application/json' -Body '{\"model\":\"deepseek-chat\",\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}],\"max_tokens\":10}';Write-Host \"HTTP $($r.StatusCode)\"}catch{Write-Host \"错误: $_\"}" 2>&1 | findstr /C:"HTTP" /C:"错误"

echo.
echo ============================================
echo   诊断完成
echo ============================================
pause
