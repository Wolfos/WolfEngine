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

## Editor scenes

**Authoring scene**:
The scene the editor edits and saves. Only edits to it are undoable and make it dirty.
_Avoid_: editor scene (when meaning which scene), edit scene

**Runtime scene**:
The isolated copy of the authoring scene that Play mode runs. It is discarded when Play stops, and nothing done to it reaches the authoring scene.
_Avoid_: play scene, PIE scene

**Scene authoring**:
Changing a scene's entities as a user would: creating, duplicating, deleting, instantiating models and prefabs, reparenting, moving, enabling and disabling them, changing their icons, and adding, editing and removing their components. On the authoring scene each operation is one undo step, whoever asks for it; on the runtime scene it is never recorded.
_Avoid_: entity operations, scene editing (when meaning only these structural changes)

**Component edit**:
One continuous change to one component on a set of entities, such as a gizmo drag or dragging an inspector field, that becomes a single undo step when it is committed. A transform edit is a component edit of the transform.
_Avoid_: component capture, gizmo edit, field edit (when meaning the whole interaction)
