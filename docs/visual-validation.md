# Visual validation

The editor can run a scene unattended and write a PNG of the Play-mode camera output. This is intended for automated visual review; it does not create, compare, or update visual baselines.

From a development checkout, run the editor project with the target project supplied explicitly:

```sh
dotnet run --project WolfEngine.Editor/WolfEngine.Editor.csproj -- \
  --project /path/to/WolfEngineGame \
  --scene Assets/Scenes/Example/Example.scene.json \
  --frames 10 \
  --capture Artifacts/visual/example-after.png \
  --quit
```

`--project` defaults to the current directory. `--scene`, `--frames`, and `--capture` are required. `--width` and `--height` default to 1280 and 720. The command exits with a non-zero status when arguments, project loading, Play-mode startup, rendering, or PNG writing fail.

For visual work, capture a `before.png` before making a change and an `after.png` after it. Keep both under an ignored artifact directory such as `Artifacts/visual/<task>/`; inspect them before reporting the task complete.

The editor opens a native window and needs the normal operating-system display permissions for the environment. Frame capture is supported by the Metal and Direct3D12 renderers.

## Validating the editor UI

`capture_frame` reads back the scene color target, so the returned PNG contains the rendered scene and nothing else: no panels, dock tabs, or menus. Editor-UI work needs the other two automation tools.

`capture_editor_window(output_path)` reads back the final presented target instead, after the ImGui pass has composited the editor over the scene. Use it whenever the thing under test is the interface rather than the image the renderer produced.

`get_editor_ui_state()` reports the same frame as structured data: every registered panel with its open state, dock node, whether it is that node's selected tab, whether it is focused or hovered, and its position and size in ImGui points. Dock nodes are also grouped, so an assertion like "Components is still the selected tab of the right-hand node" needs no screenshot at all. The values are recorded while the panels draw, so wait for render frames after a change before reading them.

A workspace or docking regression is checked by reading the state, changing something, and reading it again:

```text
set_workspace_window_open("components", true) -> wait_for_render_frames(5) -> get_editor_ui_state()
activate_workspace(<other>) -> wait_for_render_frames(10)
activate_workspace(<first>) -> wait_for_render_frames(15) -> get_editor_ui_state()
```

The selected window id per dock node should be identical in both snapshots. Capture the window as well when the report is for a person to look at.
