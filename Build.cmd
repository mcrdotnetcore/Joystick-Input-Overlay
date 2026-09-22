@echo off
REM Builds a standalone JoystickInputOverlay.exe into .\dist
setlocal
pushd "%~dp0"

dotnet publish src\JoystickInputOverlay\JoystickInputOverlay.csproj ^
  -c Release ^
  -r win-x64 ^
  --self-contained false ^
  -p:PublishSingleFile=true ^
  -o dist

if errorlevel 1 (
  echo.
  echo Build FAILED.
  popd
  exit /b 1
)

echo.
echo Built: %~dp0dist\JoystickInputOverlay.exe
popd
endlocal
