# Screen points and world rays

`IInputSystem.GetMouseViewportPosition()` returns the mouse position normalized to the active gameplay viewport.
Pass it to the selected camera with that entity's world transform:

```csharp
using WolfEngine;
using WolfEngine.ECS;
using WolfEngine.Input;
using WolfEngine.Mathematics;

public sealed class CameraPickSystem : IUpdate
{
    private readonly IInputSystem _inputSystem;

    public CameraPickSystem(IInputSystem inputSystem)
    {
        _inputSystem = inputSystem;
    }

    public void Update(float deltaTime, World world)
    {
        foreach (var entry in world.View<WorldTransform, Camera>())
        {
            if (world.IsEnabled(entry.Entity) == false)
                continue;

            var transform = entry.First;
            var camera = entry.Second;
            var ray = camera.ViewportPointToRay(_inputSystem.GetMouseViewportPosition(), transform);
            if (ray.IsValid == false)
                return;

            // Use the ray for picking or a physics query.
            return;
        }
    }

    public WorldTag GetTag() => WorldTag.Game;
}
```

This chooses the first enabled camera as the canonical gameplay camera. If the world has multiple gameplay
cameras, select the intended camera explicitly.

No mouse callback registration is needed. The standalone runtime and editor update loops already call
`ProcessPointerInput`, which supplies the pointer viewport's origin and size. The standalone runtime uses the
window rectangle; the editor passes the displayed game/scene image rectangle. The input system maps raw mouse
coordinates to that rectangle each update, even if the pointer did not move. `Available` controls UI/input
ownership, not coordinate conversion, so the normalized point can fall outside 0..1 during a captured drag.

`GetMouseViewportPosition` returns `(NaN, NaN)` if no mouse position or valid pointer viewport is known, gameplay
input is disabled, or the pointer/window is unfocused. `TryGetMouseViewportPosition` provides the same result as a
boolean plus an `out` value. A pointer outside the image is still a valid position; keep click and hover ownership
checks separate from ray math.

`Camera.ViewportPointToRay` builds its projection from the camera's current `ScreenResolution`, field of view and
clip planes. `CameraResolutionUpdater` follows the primary rendered resolution. If a secondary view uses a
different aspect ratio or needs the exact projection submitted for a particular rendered frame, retain that
view's matrices and use the advanced `ViewProjection` API described below.

## Viewport and screen coordinates

`TryViewportPointToRay` takes normalized viewport coordinates. `(0, 0)` is the top-left corner and `(1, 1)` is the
bottom-right corner. `TryScreenPointToRay` takes a screen point plus the minimum and maximum of the displayed
image rectangle. The point and rectangle must use the same coordinate system: runtime `MousePosition` uses
window coordinates, while editor tools use ImGui coordinates. Use the displayed image bounds after letterboxing,
and keep pointer coordinates in those same units; do not independently scale them for DPI or render-resolution
scaling.

For an editor tool, read the current mouse position from ImGui and use the displayed image bounds and
rendered projection for the same view. This example assumes `viewportStateBus` is the editor's existing
`EditorViewportStateBus` and `viewId` identifies the viewport being queried. Run it during the editor UI frame:

```csharp
using ImGuiNET;
using WolfEngine.Rendering;
using WolfEngine.Rendering.UI;

var viewport = viewportStateBus.GetUiState(viewId);
var projection = viewportStateBus.GetRenderState(viewId).ViewProjection;
var mousePosition = ImGui.GetIO().MousePos;

if (viewport.Visible && viewport.PointerAvailable &&
    projection.TryScreenPointToRay(
        mousePosition, viewport.ImageMin, viewport.ImageMax, out var ray))
{
    var origin = ray.Origin;
    var direction = ray.Direction;
    var pointTenMetersAway = ray.GetPoint(10.0f);
    // Use the ray for picking or a physics query.
}
```

`MousePos` and the image bounds are all in ImGui coordinates, so no DPI conversion is needed. For a tool
continuing a drag it already owns, use `PointerCaptured` for eligibility instead of requiring `PointerAvailable`.

Both methods extrapolate points outside their bounds. They do not decide whether the pointer is eligible to
start an interaction. Gate clicks and hover with the owning UI or viewport state, and continue a captured drag
with extrapolated coordinates after it leaves the image.

## Rays and physics queries

`Ray` stores a world-space `Origin` and normalized `Direction`. `GetPoint(distance)` evaluates a point along the
ray. Constructing a ray with a non-finite origin/direction or zero direction throws `ArgumentOutOfRangeException`;
the default struct value is invalid, so check `IsValid` before using a default-initialized ray. Ray length belongs
to the query, not the ray. Pass the normalized ray and an explicit maximum distance to physics; the hit fraction
is relative to that distance:

```csharp
var maxDistance = 100.0f;
if (physics.TryRaycast(world, ray, maxDistance, out var hit))
{
    var pointOnRay = ray.GetPoint(hit.Fraction * maxDistance);
}
```

The origin-and-direction overload remains available for casts represented as a displacement vector.

`ViewProjection.TryCreate` returns `false` for non-finite or non-invertible matrices. Ray conversion returns
`false` for an invalid snapshot or non-finite coordinates; screen conversion also returns `false` for a
non-finite or degenerate screen rectangle. Both return `false` when unprojection cannot produce finite near and
far points.

## Rendered view snapshots

The editor renderer publishes a `ViewProjection` with each `SceneViewportRenderState`. Editor tools retrieve the
matching per-view snapshot with `EditorViewportStateBus.GetRenderState(viewId).ViewProjection`, so picking,
terrain tools and gizmos use the camera transform and unjittered projection associated with that rendered view.
The snapshot is invalid when that view has no valid camera projection or scene output. The bus is editor viewport
state; it is not a general gameplay view service. Runtime systems should retain or obtain the projection snapshot
from the view that rendered their image rather than assume a global main camera.
