@echo off
setlocal
chcp 65001 >nul
set PYTHONUTF8=1
set PYTHONIOENCODING=utf-8

title Voices - Neural TTS Server

cd /d "%~dp0"

echo ========================================================
echo   Voices - Local Neural Text-to-Speech Server
echo ========================================================
echo.

set "PYTHON_EXE="

:: 1. Check for 'python'
where python >nul 2>&1
if %ERRORLEVEL% EQU 0 (
    set "PYTHON_EXE=python"
    goto :found
)

:: 2. Check for 'py'
where py >nul 2>&1
if %ERRORLEVEL% EQU 0 (
    set "PYTHON_EXE=py"
    goto :found
)

:: 3. Check for 'python3'
where python3 >nul 2>&1
if %ERRORLEVEL% EQU 0 (
    set "PYTHON_EXE=python3"
    goto :found
)

:not_found
echo [ERROR] Python was not found in your system PATH.
echo Please install Python 3.10+ from https://www.python.org/
echo and ensure "Add Python to PATH" is checked during installation.
echo.
pause
exit /b 1

:found
echo Using interpreter: %PYTHON_EXE%
echo Starting server on http://127.0.0.1:8000 ...
echo Press Ctrl+C to stop.
echo.

%PYTHON_EXE% server.py

if %ERRORLEVEL% NEQ 0 (
    echo.
    echo [NOTICE] Server terminated with exit code %ERRORLEVEL%.
    pause
)
