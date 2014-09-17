cd /d %~dp0
color A
call xcopy "Assets" "Android/Assets" /D /E /C /R /I /K /Y 
call cd Android
call make.bat