@echo off
setlocal

rem Runs the editor with every D3D12 diagnostic enabled:
rem
rem   WOLF_GPU_MARKERS       per-pass GPU markers, so a DRED breadcrumb names the failing pass
rem                          (ctx='<pass>' / enclosing='<pass>') instead of dumping anonymous ops
rem   WOLF_VALIDATE_INDIRECT before every ExecuteIndirect, check the buffers whose GPU virtual
rem                          addresses were baked into the indirect records still live there
rem   WOLF_D3D_DEBUG_LAYER   the D3D12 debug layer, so a rejected call explains itself instead of
rem                          surfacing as "Value does not fall within the expected range"
rem
rem All three cost frame time. This is a debugging launcher, not a normal run.
rem
rem Usage, from anywhere:
rem   scripts\windows\run-editor-diagnostics.cmd
rem
rem Extra arguments pass straight through to the editor, so the headless capture flags work too:
rem   scripts\windows\run-editor-diagnostics.cmd --scene "Assets/Scenes/Bistro/Bistro.scene.json" ^
rem     --frames 10 --capture "Artifacts/visual/example.png" --quit
rem
rem Overridable before invoking: WOLF_GAME_PROJECT (defaults to the repository containing this
rem submodule), CONFIGURATION (defaults to Debug).

rem The quoted form matters: `set X=1 && ...` bakes a trailing space into the value, and the
rem exact-match check in GraphicsConfig then fails silently.
set "WOLF_GPU_MARKERS=1"
set "WOLF_VALIDATE_INDIRECT=1"
set "WOLF_D3D_DEBUG_LAYER=1"

set "ENGINE_ROOT=%~dp0..\.."
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

set "EDITOR_PROJECT=%ENGINE_ROOT%\WolfEngine.Editor\WolfEngine.Editor.csproj"
if not exist "%EDITOR_PROJECT%" (
	echo Could not find the editor project at "%EDITOR_PROJECT%".
	exit /b 1
)

rem With no arguments the editor resolves the game project from the working directory, so run from
rem there rather than passing --project: any argument at all puts it into headless capture mode,
rem which then demands --scene, --frames and --capture.
pushd "%WOLF_GAME_PROJECT%" || exit /b 1
echo Launching the editor with GPU markers, indirect validation and the D3D12 debug layer enabled.
dotnet run --project "%EDITOR_PROJECT%" -c "%CONFIGURATION%" %*
set "EDITOR_EXITCODE=%ERRORLEVEL%"
popd

exit /b %EDITOR_EXITCODE%
