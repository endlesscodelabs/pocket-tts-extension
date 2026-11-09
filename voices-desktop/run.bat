@echo off
title Voices Desktop (.NET 8 / WinUI 3)
echo ========================================================
echo   Voices Desktop - Local Neural Text-to-Speech
echo ========================================================
echo.

cd /d "%~dp0"

REM Configure user-local or system .NET 8 SDK environment
if exist "%USERPROFILE%\dotnet\dotnet.exe" (
    set "DOTNET_ROOT=%USERPROFILE%\dotnet"
    set "PATH=%USERPROFILE%\dotnet;%PATH%"
)

where dotnet >nul 2>&1
if %errorlevel% neq 0 (
    echo [ERROR] .NET 8 SDK not found in PATH or %USERPROFILE%\dotnet.
    echo Please install the .NET 8 SDK from https://dotnet.microsoft.com/download/dotnet/8.0
    echo.
    pause
    exit /b 1
)

if exist "bin\Debug\net8.0-windows10.0.19041.0\win-x64\VoicesDesktop.exe" (
    echo Launching Voices Desktop...
    start "" "%~dp0bin\Debug\net8.0-windows10.0.19041.0\win-x64\VoicesDesktop.exe"
    exit /b 0
)

echo Building and launching Voices Desktop...
dotnet run --project "%~dp0VoicesDesktop.csproj"

if %errorlevel% neq 0 (
    echo.
    echo Run failed. Please verify that .NET 8 SDK and Windows App SDK are installed.
    pause
)
