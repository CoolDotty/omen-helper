@echo off
setlocal

set "ROOT=%~dp0"
set "DOTNET=C:\Program Files\dotnet\dotnet.exe"
set "PROJECT=%ROOT%OmenHelper.csproj"
set "OUTPUT=%ROOT%bin\Debug\net48\OmenHelper.exe"

if not exist "%DOTNET%" (
  echo dotnet not found at "%DOTNET%"
  exit /b 1
)

echo Building OmenHelper...
"%DOTNET%" build "%PROJECT%"
if errorlevel 1 exit /b 1

echo.
echo Build complete.
echo Output: "%OUTPUT%"
echo.
echo Launching latest build...
start "" "%OUTPUT%"

endlocal
