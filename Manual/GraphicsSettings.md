# Graphics settings

Gameplay code reads and changes the window mode, resolution and vsync through
`IGraphicsSettings`, which dependency injection provides. A settings menu changes
everything through this one service, and the same calls work in the editor, in
desktop builds and on iOS.

## How a game starts

**Project Settings > Window** sets how a build starts:

- **Fullscreen** (the default) covers the display at its native resolution.
  Width and Height are greyed out because fullscreen doesn't use them.
- With Fullscreen off, the game starts in a window of **Width** × **Height**
  points.

Projects whose settings were saved before this option existed start fullscreen.

For testing, the desktop runtime accepts two command-line overrides:

- `--windowed` starts in a window of the project's Width and Height.
- `--window-size 1280x720` starts in a window of that size.

## Getting the service

Request `IGraphicsSettings` in your gameplay module's `CreateSystems`, like other
engine services, and pass it to the system that draws your settings menu.

```csharp
using Microsoft.Extensions.DependencyInjection;
using WolfEngine.Rendering;

public IEnumerable<ISystem> CreateSystems(IServiceProvider serviceProvider)
{
	var graphics = serviceProvider.GetRequiredService<IGraphicsSettings>();

	yield return new SettingsMenuSystem(graphics);
}
```

You can call the service from any thread.

## Settings and display information

`Current` is a `GraphicsSettings` record holding what was last applied:

| Property | Meaning |
| --- | --- |
| `WindowMode` | `Fullscreen` or `Windowed`. |
| `Resolution` | Fullscreen: the size in pixels to render at; `Int2.Zero` means native. Windowed: the window's size in points; `Int2.Zero` keeps the current size. |
| `VSync` | Whether frames wait for the display to refresh. |

`Display` is a `DisplayInfo` record describing what the window actually shows,
as of the last rendered frame:

| Property | Meaning |
| --- | --- |
| `WindowMode` | The mode the window is in now. |
| `WindowSize` | The window's size in points. |
| `OutputResolution` | The size in pixels the game renders and presents at. |
| `NativeResolution` | The resolution a fullscreen window gets on this display. |
| `SupportedResolutions` | Fullscreen resolutions to offer the player, smallest first, ending with native. |

`Current` is what you asked for and `Display` is what you got. Show `Current`
in a settings menu, and use `Display` when you need the real output size.

## Changing settings

Records are immutable, so create a changed copy with a `with` expression and pass
it to `Apply`. Fields you don't mention keep their current values.

```csharp
// Toggle fullscreen.
var settings = _graphics.Current;
var mode = settings.WindowMode == WindowMode.Fullscreen
	? WindowMode.Windowed
	: WindowMode.Fullscreen;

_graphics.Apply(settings with
{
	WindowMode = mode,
	Resolution = Int2.Zero
});
```

```csharp
// Turn vsync off.
_graphics.Apply(_graphics.Current with { VSync = false });
```

`Apply` returns immediately and the change takes effect before the next frame
renders. On macOS, entering or leaving fullscreen plays the system animation, so
`Display` takes several frames to catch up. Don't wait on it in the same frame.

### Fullscreen resolution

In fullscreen the display keeps its own mode. A resolution below native renders
smaller and is scaled up to fill the screen, which is cheaper than native and
never makes the display switch modes. Offer the player the entries of
`SupportedResolutions`:

```csharp
var display = _graphics.Display;
foreach (var resolution in display.SupportedResolutions)
{
	var label = $"{resolution.X} x {resolution.Y}";
	if (DrawOption(label, resolution == _graphics.Current.Resolution))
	{
		_graphics.Apply(_graphics.Current with
		{
			WindowMode = WindowMode.Fullscreen,
			Resolution = resolution
		});
	}
}
```

The supported resolutions all keep the display's aspect ratio, so the image fills
the screen without stretching. Any other resolution is accepted too, but a
resolution larger than native is clamped to native.

`SupportedResolutions` is empty until the window has opened. Build the menu from
it each time the menu opens, rather than once at startup.

The list can also change while the game runs, for example when the window moves
to a different display. On a MacBook with a camera notch, macOS keeps fullscreen
windows below the notch, so native resolution there is slightly shorter than the
panel.

### Window size

In windowed mode, `Resolution` is the window's size in points. The game renders
exactly that many pixels and scales them to the window. This applies even on a
Retina display, so a 1280 × 720 window renders 1280 × 720 pixels.

```csharp
_graphics.Apply(_graphics.Current with
{
	WindowMode = WindowMode.Windowed,
	Resolution = new Int2(1600, 900)
});
```

Players can still resize a window by dragging its edges. From then on the game
renders at the window's full pixel size.

### Changes made by the player

On macOS the player can also leave fullscreen with the window's green button.
`Display.WindowMode` shows the change, while `Current` keeps the last values you
applied. To keep a settings menu accurate, read `Display.WindowMode` when the
menu opens.

## Where the window can't be changed

`CanChangeWindow` is false when window mode and resolution can't be applied:

- **In the editor.** Play mode renders into the Scene panel, so the editor window
  is left alone. `Apply` still records the settings in `Current`, so a settings
  menu behaves the same as in a build. VSync is still applied, and it stays in
  effect for the editor after Play mode stops.
- **On iOS.** The game always fills the screen and always uses vsync.

You don't need to check `CanChangeWindow` before calling `Apply`. Use it to grey
out the display options in your menu.

## Saving the player's choice

The engine doesn't save graphics settings between runs yet. Store the player's
choice with your own save data, then apply it when your gameplay module starts.

## Coming later

Effect quality settings will be added to `GraphicsSettings`, as overrides on top
of the scene's render config for settings that cost performance, such as render
scale and shadow resolution. A settings menu will then change quality through the
same `Apply` call.
