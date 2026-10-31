@echo off
rem Double-click to build Stardew Valley for the PS Vita from your own Steam copy (see README.md).
rem Needs the .NET 8 SDK and vita-toolchain-windows.zip in this folder. Options are passed on to the builder.
setlocal
cd /d "%~dp0"
set DOTNET_CLI_TELEMETRY_OPTOUT=1
set DOTNET_NOLOGO=1

set SDK=
for /f "delims=" %%s in ('dotnet --list-sdks 2^>nul') do set SDK=1
if not defined SDK (
	echo The .NET SDK is not installed. Install the .NET 8 SDK from https://dotnet.microsoft.com/download
	echo ^(the SDK, not only the Runtime^), then run Build.bat again.
	echo.
	pause
	exit /b 1
)

dotnet run --project builder -c Release -- %*
rem Not "if errorlevel 1": a crash exits with a negative code.
if %errorlevel% neq 0 (
	echo.
	echo The build stopped: see the ERROR line above.
	echo.
	pause
	exit /b 1
)
if exist "work\out\ux0" explorer "work\out\ux0"
echo.
pause
