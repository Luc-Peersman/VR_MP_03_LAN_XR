@echo off
REM Zet alleen de benodigde runtime-permissies voor de al-geinstalleerde app.
REM Installeert GEEN apk - ga ervan uit dat de app al op de headset staat.
REM Vereist: platform-tools (adb) in PATH, en de Quest aangesloten via USB
REM met "USB-foutopsporing" toegestaan.

set PACKAGE=com.DefaultCompany.VRMultiplayer

echo Controleer verbonden apparaten...
adb devices

echo.
echo Permissies zetten voor %PACKAGE%...
adb shell pm grant %PACKAGE% android.permission.CAMERA
adb shell pm grant %PACKAGE% horizonos.permission.HEADSET_CAMERA
adb shell pm grant %PACKAGE% com.oculus.permission.USE_SCENE
adb shell pm grant %PACKAGE% horizonos.permission.USE_SCENE

echo.
echo Klaar. Verifieren...
adb shell dumpsys package %PACKAGE% | findstr /C:"CAMERA" /C:"USE_SCENE"

pause
