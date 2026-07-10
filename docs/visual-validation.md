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

The first implementation supports Metal on macOS. It opens a native window and needs the normal macOS application/display permissions for that environment. Direct3D reports capture as unsupported until its GPU readback path is implemented.
