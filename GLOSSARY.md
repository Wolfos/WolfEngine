# WolfEngine

A game engine for high-end 3D games. This glossary names the engine's own concepts; general graphics terms are not repeated here.

## Shaders

**Shader source**:
Where the renderer gets compiled engine shaders, together with the applied shader revision they belong to.
_Avoid_: shader compiler, shader provider (when meaning the renderer's view of it)

**Applied shader revision**:
The shader revision the renderer has switched to: it changes only between frames, after the GPU is idle and the old pipelines are gone. Anything built against an older applied revision is stale.
_Avoid_: shader revision (unqualified), reload generation

**Compute kernel**:
One compute entry point of an engine shader, with the defines it is compiled with. It is the only thing a pass keeps for a compute dispatch.
_Avoid_: compute pipeline, compute shader (when meaning one entry point)

**Graphics shader**:
One vertex and pixel entry-point pair of an engine shader, with its defines. A pass gets a pipeline from it for each set of render targets and render state it draws with.
_Avoid_: graphics pipeline (when meaning the shader rather than one state of it)

**Shader reload**:
Replacing engine shaders while the editor runs, taking effect from the next applied shader revision.
_Avoid_: hot reload (that is gameplay code), shader invalidation
