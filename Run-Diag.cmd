@echo off
REM Prints every HID joystick it can see, then logs each control as you move it.
REM Use this to work out which button number and axis belongs to which physical control.
setlocal
pushd "%~dp0"

if exist "dist\JoystickInputOverlay.exe" (
  dist\JoystickInputOverlay.exe --diag
) else (
  dotnet run --project src\JoystickInputOverlay\JoystickInputOverlay.csproj -c Release -- --diag
)

popd
endlocal
pause
