cd /d %~dp0
color A
call ndk-build
pause
call ant debug
call adb uninstall nl.rvanee.wolfengine
call adb install Bin/wolfengine-debug.apk
adb shell am start -n nl.rvanee.wolfengine/nl.rvanee.wolfengine.WolfEngine
pause