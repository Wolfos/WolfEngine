# Release checklist: `development` → `master`

`master` is public, so a merge is a release. Work top to bottom; a failed item blocks the merge.

## 1. Branch hygiene

- [ ] `development` is up to date with `master` (`git merge master` into development first, resolve conflicts there, not on master)
- [ ] Working tree clean, nothing unpushed
- [ ] Skim `git log --oneline master..development` and `git diff --stat master...development` for anything that shouldn't go public:
  - [ ] No game-specific code or content (KingsAndCastles / SwordAndSparrow names, assets, scenes)
  - [ ] No absolute personal paths (`/Users/...`, `C:\Users\...`), keys, signing identities or certificates
  - [ ] No large binaries or generated output (`bin/`, `obj/`, `Artifacts/`, `dist/`, `.wpix`, captures)
  - [ ] No leftover debug code: forced env flags, `RequestGpuCapture()` calls, temporary logging, disabled tests
- [ ] New `TODO`/`HACK`/`FIXME` lines added on the branch are intentional

## 2. Build and tests (both platforms)

- [ ] Clean build of `WolfEngine.sln` in **Debug and Release** on Windows: `dotnet build -c Release` with no new warnings
- [ ] Same on macOS (arm64)
- [ ] `dotnet test` passes on both: ECS, Physics, Audio, UI, Editor, SourceGenerators, Tests
- [ ] CI (`.github/workflows/dotnet.yml`) passes on the PR. Note it runs on **ubuntu only**, so it does not prove D3D12 or Metal work; sections 3–5 are what cover that
- [ ] Source generators: a full rebuild (delete `obj/`) produces no generator errors in a game project

## 3. Renderer — D3D12 (Windows)

Run with strict validation so silence means something:
`WOLF_D3D_DEBUG_LAYER=1 WOLF_D3D_DEBUG_BREAK=1`, or `scripts\windows\run-editor-diagnostics.cmd` (adds GPU markers and indirect-buffer validation).

- [ ] Headless capture of each reference scene exits 0 and the PNG looks right (see [visual-validation.md](visual-validation.md)):
  `dotnet run --project WolfEngine.Editor/WolfEngine.Editor.csproj -- --project <game> --scene <scene> --frames 60 --capture Artifacts/visual/release/<name>.png --quit`
- [ ] Compare against captures from current `master` for the same scenes: no unexplained regressions
- [ ] Features changed this cycle, checked on purpose:
  - [ ] DDGI + final gather + AO, denoising on moving objects, TAA
  - [ ] FSR3 upscaling on and off
  - [ ] Draw record batching / indirect draws (no missing or flickering meshes)
  - [ ] Skinned meshes (skinning pass uploads only changes)
  - [ ] Particle system / VFX
  - [ ] Shadows at low resolutions
  - [ ] Game-provided compute passes
  - [ ] Texture format de-duplication: no wrong-format or black textures
- [ ] No D3D12 debug-layer errors, no DRED breadcrumbs, no device removal over a few minutes of play
- [ ] Resize the window, toggle fullscreen, change resolution and vsync — no crash or stale targets

## 4. Renderer — Metal (macOS)

- [ ] Same reference scenes captured headless on Metal, compared with D3D12 and master
- [ ] Metal API validation enabled for one run, no errors
- [ ] Trackpad gestures in the editor viewport
- [ ] Retina / mixed-DPI: editor UI and viewport scale correctly
- [ ] `scripts/macos/pack-app.sh` produces an app that launches

## 5. Editor smoke test (both platforms)

- [ ] Fresh start with no editor preferences, then restart: last-loaded scene restores
- [ ] Open, edit, save, reopen a scene; dirty-state and save workflow behave consistently
- [ ] Double-click opens each document type (scene, prefab, material, animation graph, clip)
- [ ] Asset browser: import, rename, move, delete; asset refs survive renames
- [ ] Inspector: edit components, add/remove, undo/redo
- [ ] Transform gizmos: move/rotate/scale, static colliders follow moved entities
- [ ] Animation editor / Animate mode: create clip, rename clip with editor open, switch to Animate (no magenta flash), enter PIE from Animate (no stray Components window)
- [ ] Animation graph: create, edit node names, play in PIE
- [ ] Viewport frame limit on: editor UI stays responsive, scene still updates
- [ ] Workspaces/docking: switch away and back, dock layout unchanged (`get_editor_ui_state` check in visual-validation.md)
- [ ] Play mode: enter, pause system works, exit, no leaked entities or state; Escape doesn't quit the engine
- [ ] Hot reload of gameplay code: no stale gameplay modules after a change
- [ ] Profiler opens and shows data

## 6. Runtime builds

Build each internal game with `WolfEngine.Build` (Release, self-contained) on both platforms:

- [ ] Build succeeds; build log is written and readable
- [ ] Package cooker includes everything: scenes, AnimationGraphs, prefabs, audio, UI images
- [ ] No asset-ID misdetection (e.g. animation node IDs treated as asset IDs)
- [ ] Built game launches outside the repo, on a machine/user without the dev setup if possible
- [ ] Splash screen shows (black background), and can be disabled via project settings
- [ ] Fullscreen, resolution and vsync from the graphics settings API apply and persist
- [ ] Music and SFX play
- [ ] UI renders, including images; input works (keyboard, mouse, gamepad)
- [ ] Physics: vehicles and colliders behave as in the editor
- [ ] Quitting from the game exits cleanly (exit code 0, no hang)
- [ ] Prefab spawning at runtime and runtime scene loading work

## 7. Downstream games

- [ ] KingsAndCastles and SwordAndSparrow build against the new engine with no source changes, or the needed changes are listed under breaking changes below
- [ ] Each opens in the editor and plays its main scene without errors
- [ ] Before running, note `git status` of each game repo and confirm afterwards the editor didn't change tracked files unexpectedly

## 8. Performance

- [ ] Frame time of a heavy reference scene is no worse than master (CPU and GPU), at fixed resolution and settings
- [ ] Memory/VRAM doesn't grow during a few minutes of play or repeated PIE enter/exit
- [ ] Editor startup and scene-load time not noticeably slower

## 9. Docs and public-facing

- [ ] README feature list and platform table still accurate
- [ ] New features have a Manual page or an entry (graphics settings, VFX, animation, audio, prefabs) and the README links them
- [ ] Breaking API changes written down (merge commit or release notes) with how to migrate
- [ ] `LICENSE` and third-party notices cover any new dependency (e.g. FSR3)

## 10. Merge

- [ ] Open a PR `development` → `master` so CI runs on it
- [ ] Merge with a merge commit (keeps history traceable)
- [ ] Tag the release on master, e.g. `v2026.11`
- [ ] Merge `master` back into `development`
