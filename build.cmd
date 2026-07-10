@echo off
echo [1/4] Killing running instances...
taskkill /F /IM claudeg.exe >nul 2>&1
timeout /t 1 /nobreak >nul

echo [2/4] Cleaning previous build...
if exist "ClaudeWebGui\bin\Release" rmdir /s /q "ClaudeWebGui\bin\Release"

echo [3/4] Building ClaudeWebGui...
dotnet publish ClaudeWebGui -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true
if %errorlevel% neq 0 (
    echo Build failed!
    pause
    exit /b 1
)

echo [4/4] Cleaning extra files...
if exist "ClaudeWebGui\bin\Release\net8.0\win-x64\publish\web.config" del "ClaudeWebGui\bin\Release\net8.0\win-x64\publish\web.config"
if exist "ClaudeWebGui\bin\Release\net8.0\win-x64\publish\*.pdb" del "ClaudeWebGui\bin\Release\net8.0\win-x64\publish\*.pdb"

echo.
echo ========================================
echo   Build successful!
echo   Output: claudeg.exe (~45MB)
echo ========================================
echo.
echo Copying to D:\Prog\ProgIDE\Claude\bin...
xcopy /y "ClaudeWebGui\bin\Release\net8.0\win-x64\publish\claudeg.exe" "D:\Prog\ProgIDE\Claude\bin\" >nul
if exist "D:\Prog\ProgIDE\Claude\bin\claudeg.exe" (
    echo Done! claudeg.exe is now in D:\Prog\ProgIDE\Claude\bin\
) else (
    echo Copy failed! Check directory: D:\Prog\ProgIDE\Claude\bin\
)
echo.
pause
