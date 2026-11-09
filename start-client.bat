@echo off
rem Boots the KickFlight_A15 emulator, installs the current patched APK, seeds the
rem asset cache if it is missing and launches Kick-Flight. Run start-server.bat first.
rem Optional: pass extra emulator arguments, e.g.  start-client.bat -gpu swiftshader_indirect
setlocal
set SDK=%LOCALAPPDATA%\Android\Sdk
set PATH=%SDK%\platform-tools;%PATH%
set ADB=adb
set EMU=%SDK%\emulator\emulator.exe
set APK=%~dp0.local\KickFlight-2.11.0-current-patches.apk
if defined APK_OVERRIDE set APK=%APK_OVERRIDE%
if not defined KF_PACKAGE_ID set KF_PACKAGE_ID=world.global.fenix
set PKG=%KF_PACKAGE_ID%

if not exist "%APK%" (
    echo APK not found: %APK%
    echo Build it first from Git Bash:  bash .local/build.sh
    pause
    goto :eof
)

%ADB% get-state >nul 2>&1
if errorlevel 1 (
    echo Starting emulator...
    start "KickFlight emulator" "%EMU%" -avd KickFlight_A15 -memory 6144 -no-snapshot-load -no-boot-anim -netdelay none -netspeed full %*
    %ADB% wait-for-device
    :bootloop
    for /f "delims=" %%b in ('%ADB% shell getprop sys.boot_completed 2^>nul') do set BOOTED=%%b
    if not "%BOOTED%"=="1" (
        timeout /t 5 /nobreak >nul
        goto :bootloop
    )
    timeout /t 15 /nobreak >nul
) else (
    echo Emulator already running.
)

echo Installing APK...
%ADB% install -r "%APK%" || goto :fail

adb shell "run-as %PKG% find files/octo -type f | wc -l" > "%TEMP%\kf_count.txt"
set NFILES=0
set /p NFILES=<"%TEMP%\kf_count.txt"
echo Asset cache files: %NFILES%
if %NFILES% LSS 2000 (
    echo Seeding asset cache - takes a few minutes...
    python "%~dp0scripts\seed-device-cache.py" -s emulator-5554 || goto :fail
)

echo Launching Kick-Flight...
%ADB% shell am force-stop %PKG%
%ADB% shell monkey -p %PKG% -c android.intent.category.LAUNCHER 1 >nul 2>&1
echo Done. TAP START, then Combate.
goto :eof

:fail
echo Something failed - see the output above.
pause
