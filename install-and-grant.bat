@echo off
REM Installeert de build, zet alle benodigde runtime-permissies, en start de app.
REM Vervangt de MQDH Custom Command - vereist alleen adb (platform-tools) in PATH
REM en de Quest aangesloten via USB met "USB-foutopsporing" toegestaan.
REM
REM Pad naar de apk wordt berekend t.o.v. dit script (%~dp0), dus dit werkt
REM ongeacht op welke PC/in welke map het project staat - zolang dit .bat-bestand
REM in de projectroot staat, naast de "Builds"-map.

set PACKAGE=com.DefaultCompany.VRMultiplayer
set APK=%~dp0Builds\VR_MP_03_LAN_XR.apk

echo Controleer verbonden apparaten...
adb devices

echo.
echo Apk verwijderen indien aanwezig (fout hier is normaal als 'ie nog niet stond)...
adb uninstall %PACKAGE%

echo.
echo Apk installeren vanaf: %APK%
adb install "%APK%"

echo.
echo Permissies zetten...
adb shell pm grant %PACKAGE% android.permission.CAMERA
adb shell pm grant %PACKAGE% horizonos.permission.HEADSET_CAMERA
adb shell pm grant %PACKAGE% com.oculus.permission.USE_SCENE
adb shell pm grant %PACKAGE% horizonos.permission.USE_SCENE

echo.
echo App starten...
adb shell monkey -p %PACKAGE% -c android.intent.category.LAUNCHER 1

echo.
echo Klaar. Verifieren...
adb shell dumpsys package %PACKAGE% | findstr /C:"CAMERA" /C:"USE_SCENE"

pause
