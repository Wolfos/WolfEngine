@echo off
setlocal enabledelayedexpansion

rem Takes a real PIX GPU capture of the editor and dumps the event list to CSV, without the PIX GUI.
rem
rem The editor triggers the capture itself through IDXGraphicsAnalysis, so pixtool runs in
rem programmatic-capture mode and waits. Three ways to trigger it:
rem
rem   --frame <n>          capture the nth rendered frame (WOLF_GPU_CAPTURE_FRAME)
rem   --terrain-stamp <n>  capture the frame after the nth terrain brush stamp is applied
rem                        (WOLF_GPU_CAPTURE_TERRAIN_STAMP)
rem   RenderGraph.RequestGpuCapture()   capture the next frame, from code
rem
rem Usage:
rem   scripts\windows\capture-gpu-frame.cmd --frame 30 --scene "Assets/Scenes/Terrain Prototype/Terrain Prototype.scene.json"
rem   scripts\windows\capture-gpu-frame.cmd --terrain-stamp 1     (interactive; paint, and the frame
rem                                                                after the first stamp is captured)
rem
rem Output goes to Artifacts\gpu\<name>.wpix and <name>.csv. Override the name with --name.
rem
rem The event list CSV columns are: Queue ID, Parent, Name, Global ID. GPU markers appear as rows whose
rem Queue ID shows up in another row's Parent; Parent = -1 on every row means no markers were recorded.

rem Resolve this before parsing: `shift` moves %0 as well, so %~dp0 stops being the script path.
set "SCRIPT_DIR=%~dp0"

set "PIX_ROOT=C:\Program Files\Microsoft PIX"
set "PIXTOOL="
for /f "delims=" %%d in ('dir /b /o-n "%PIX_ROOT%" 2^>nul') do (
	if not defined PIXTOOL if exist "%PIX_ROOT%\%%d\pixtool.exe" set "PIXTOOL=%PIX_ROOT%\%%d\pixtool.exe"
)
if not defined PIXTOOL (
	echo Could not find pixtool.exe under "%PIX_ROOT%". Install PIX for Windows.
	exit /b 1
)

set "CAPTURE_FRAME="
set "CAPTURE_NAME=terrain"
set "SCENE="
set "TERRAIN_STAMP="
rem Each branch needs a parenthesised block: in `if cond a & b`, b runs whether or not cond held.
:parse
if "%~1"=="" goto parsed
if /i "%~1"=="--frame" (
	set "CAPTURE_FRAME=%~2"
	shift
	shift
	goto parse
)
if /i "%~1"=="--name" (
	set "CAPTURE_NAME=%~2"
	shift
	shift
	goto parse
)
if /i "%~1"=="--scene" (
	set "SCENE=%~2"
	shift
	shift
	goto parse
)
if /i "%~1"=="--terrain-stamp" (
	set "TERRAIN_STAMP=%~2"
	shift
	shift
	goto parse
)
echo Unknown option "%~1".
exit /b 1
:parsed

set "ENGINE_ROOT=%SCRIPT_DIR%..\.."
rem The game project is the directory holding Assets/, which is a sibling of this submodule, not the
rem repository root. Pointing at the root fails with "Project folder must contain an Assets subfolder".
if not defined WOLF_GAME_PROJECT (
	if exist "%ENGINE_ROOT%\..\WolfEngineGame\Assets" (
		set "WOLF_GAME_PROJECT=%ENGINE_ROOT%\..\WolfEngineGame"
	) else (
		set "WOLF_GAME_PROJECT=%ENGINE_ROOT%\.."
	)
)
if not defined CONFIGURATION set "CONFIGURATION=Debug"

set "EDITOR_EXE=%ENGINE_ROOT%\WolfEngine.Editor\bin\%CONFIGURATION%\net10.0\WolfEngine.Editor.exe"
if not exist "%EDITOR_EXE%" (
	echo Building the editor first, because "%EDITOR_EXE%" does not exist.
	dotnet build "%ENGINE_ROOT%\WolfEngine.Editor\WolfEngine.Editor.csproj" -c "%CONFIGURATION%" -m:1 || exit /b 1
)

set "OUTPUT_DIR=%WOLF_GAME_PROJECT%\Artifacts\gpu"
if not exist "%OUTPUT_DIR%" mkdir "%OUTPUT_DIR%"

rem Markers are what make the capture readable per pass; the debug layer is left off because it
rem perturbs timings and PIX does its own validation on playback.
set "WOLF_GPU_MARKERS=1"
if defined CAPTURE_FRAME set "WOLF_GPU_CAPTURE_FRAME=%CAPTURE_FRAME%"
if defined TERRAIN_STAMP set "WOLF_GPU_CAPTURE_TERRAIN_STAMP=%TERRAIN_STAMP%"
if not defined CAPTURE_FRAME if not defined TERRAIN_STAMP (
	echo Nothing would trigger a capture. Pass --frame ^<n^> or --terrain-stamp ^<n^>.
	exit /b 1
)

rem Headless scene mode needs all three of --scene/--frames/--capture, and any argument at all puts the
rem editor into that mode; with no --scene it launches interactively instead.
set "EDITOR_ARGS="
if defined SCENE (
	set /a "CAPTURE_FRAMES=%CAPTURE_FRAME%+5"
	set "EDITOR_ARGS=--project ""%WOLF_GAME_PROJECT%"" --scene ""%SCENE%"" --frames !CAPTURE_FRAMES! --capture ""%OUTPUT_DIR%\%CAPTURE_NAME%.png"" --quit"
)

pushd "%WOLF_GAME_PROJECT%" || exit /b 1

rem --command-line= must precede the exe path or pixtool reports "Unknown option". Quoting only
rem survives from a .cmd wrapper; driving this from PowerShell mangles it.
"%PIXTOOL%" launch --command-line="!EDITOR_ARGS!" "%EDITOR_EXE%" ^
	programmatic-capture --until-exit ^
	save-capture "%OUTPUT_DIR%\%CAPTURE_NAME%.wpix" ^
	save-event-list "%OUTPUT_DIR%\%CAPTURE_NAME%.csv"
set "PIX_EXITCODE=%ERRORLEVEL%"

popd

if not "%PIX_EXITCODE%"=="0" (
	echo pixtool failed with exit code %PIX_EXITCODE%.
	exit /b %PIX_EXITCODE%
)

echo Wrote "%OUTPUT_DIR%\%CAPTURE_NAME%.wpix" and "%OUTPUT_DIR%\%CAPTURE_NAME%.csv".
exit /b 0
