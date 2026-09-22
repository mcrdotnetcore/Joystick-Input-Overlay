@echo off
REM Builds a single self-contained JoystickInputOverlay.exe into .\release
REM
REM Unlike Build.cmd this bundles the .NET runtime, so the file runs on a clean Windows
REM machine with nothing installed. It is the file to attach to a GitHub release.
setlocal
pushd "%~dp0"

dotnet publish src\JoystickInputOverlay\JoystickInputOverlay.csproj ^
  -c Release ^
  -r win-x64 ^
  --self-contained true ^
  -p:PublishSingleFile=true ^
  -p:EnableCompressionInSingleFile=true ^
  -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:DebugType=none ^
  -o release

if errorlevel 1 (
  echo.
  echo Publish FAILED.
  popd
  exit /b 1
)

echo.
for %%F in ("release\JoystickInputOverlay.exe") do echo Built: %%~fF  (%%~zF bytes^)
echo Attach that single file to a GitHub release.
popd
endlocal
