@echo off
setlocal EnableExtensions
cd /d "%~dp0"

echo ============================================
echo   PromptPal - one-click build and launch
echo ============================================
echo.

echo [1/2] Building (win-x64, Debug, self-contained WinAppSDK)...
dotnet build "src\PromptPal.App\PromptPal.App.csproj" ^
  -c Debug -p:RuntimeIdentifier=win-x64 -p:WindowsAppSDKSelfContained=true
if errorlevel 1 (
    echo.
    echo [ERROR] Build failed. See log above.
    pause
    exit /b 1
)

echo.
echo [2/2] Launching app...
set "FOUND=%~dp0src\PromptPal.App\bin\Debug\net10.0-windows10.0.26100.0\win-x64\PromptPal.App.exe"
if not exist "%FOUND%" (
    echo.
    echo [ERROR] PromptPal.App.exe not found. Check build output.
    pause
    exit /b 1
)

start "" "%FOUND%"
echo.
echo Launched: %FOUND%
echo Tip: if the window does not show, press Ctrl+Shift+P (global hotkey).
echo.

endlocal