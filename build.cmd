@echo off
echo [1/4] Checking if current project is ClaudeCliGui itself...
set IS_SELF=0
for %%I in ("%CD%") do set CUR_DIR=%%~fI
if /i "%CUR_DIR%"=="D:\Prog\_Project\ClaudeCodeCliGuiC#" set IS_SELF=1

echo [2/4] Killing running instances...
taskkill /F /IM claudeCliGui.exe >nul 2>&1
timeout /t 1 /nobreak >nul

echo [3/4] Building ClaudeGuiWpf (WPF)...
dotnet publish ClaudeGuiWpf -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true
if %errorlevel% neq 0 (
    echo Build failed!
    pause
    exit /b 1
)

echo [4/4] Copying executable...
set SRC=ClaudeGuiWpf\bin\Release\net8.0-windows\win-x64\publish\claudeCliGui.exe
xcopy /y "%SRC%" ".\" >nul
echo   -> claudeCliGui.exe (project root)
xcopy /y "%SRC%" "D:\Prog\ProgIDE\Claude\bin\" >nul
echo   -> D:\Prog\ProgIDE\Claude\bin\claudeCliGui.exe

echo.
echo ========================================
echo   Done! claudeCliGui.exe
if %IS_SELF%==1 (
    echo   ^>^> 本项目是 ClaudeCliGui 自身，编译完成！
    echo   ^>^> 请在终端输入您的下一个需求继续 ^(会话不会中断^)
)
echo ========================================
pause
