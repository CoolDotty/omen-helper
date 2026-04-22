@echo off
setlocal EnableExtensions EnableDelayedExpansion

echo Scanning for processes with "omen" in the image name...
set "FOUND=0"

for /f "tokens=1 delims=," %%A in ('tasklist /fo csv ^| findstr /i "omen"') do (
    set "IMAGE=%%~A"
    if not "!IMAGE!"=="" (
        set "FOUND=1"
        echo Killing !IMAGE!...
        taskkill /f /im "!IMAGE!" >nul 2>&1
    )
)

if "%FOUND%"=="0" (
    echo No matching processes found.
)

endlocal
