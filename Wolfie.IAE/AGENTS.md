# Wolfie Agent Guide

## What Wolfie Is

Wolfie is an Integrated Asset Environment for game development.

Its purpose is to give game artists one project-oriented workspace that coordinates their source assets, external authoring tools, and game engine outputs.

Wolfie initially integrates existing applications such as Blender and Substance Painter rather than attempting to replace them.

The long-term direction is that Wolfie may gradually gain native modeling, texture painting, image editing, and other authoring capabilities. External applications should therefore be treated as replaceable workflow backends rather than permanent foundations of the project model.

## Product Vision

A user should be able to:

1. Open or connect a game-engine project.
2. Browse the engine project's asset layout from Wolfie.
3. Create and manage source assets without manually juggling export files.
4. Open the appropriate external authoring application from Wolfie.
5. Save the source asset.
6. Have Wolfie publish the correct outputs into the engine project with deterministic settings.
7. Preserve engine-side asset identity and references.

Users should not need to think about FBX export dialogs, texture output folders, coordinate-system settings, or manually creating engine materials.

The game project is the destination.

The Wolfie project is the authoritative authoring workspace.

## Current MVP

The first useful MVP targets Unity and supports this workflow:

### Managed 3D model

```text
Right-click a folder
→ Create
→ 3D Model
→ Wolfie creates a .wolfasset and .blend file
→ Blender opens
→ Saving or publishing exports an FBX into Unity
```

### Managed texture project

```text
Right-click a managed model
→ Create Texture
→ Wolfie creates a Substance Painter project
→ Painter opens with the model
→ Saving or publishing exports textures into Unity
→ Wolfie creates or updates a Unity material
```

The user must not manually choose export destinations or recreate import settings for each asset.

## Current Technical Context

Wolfie is initially implemented as a separate application inside the WolfEngine repository.

Wolfie may reuse WolfEngine infrastructure such as:

* Application lifecycle
* ImGui integration
* File import infrastructure
* Asset pipeline infrastructure
* Job system
* File watching
* PBR rendering
* GPU resource management

Wolfie should remain conceptually separate from the WolfEngine game editor.

The desired dependency direction is:

```text
Wolfie
    → WolfEngine
```

Avoid introducing dependencies from WolfEngine back into Wolfie.

## Architectural Boundaries

Wolfie-specific domain concepts should live inside Wolfie code.

Examples include:

* Wolfie project
* Connected engine project
* Managed source asset
* Engine projection
* Tool binding
* Asset ownership
* Publish state
* Mirrored project browser
* Unity adapter
* Blender integration
* Substance Painter integration

Do not add these concepts to generic WolfEngine systems unless they are genuinely reusable outside Wolfie.

Wolfie may copy and adapt editor code during early development, but it should not directly depend on the WolfEngine editor's asset-browser implementation.

## Project Model

The Wolfie project is stored outside the connected Unity project.

Example:

```text
Unity project:
D:/Projects/MyGame/
    Assets/
    ProjectSettings/

Wolfie project:
D:/WolfieProjects/MyGameArt/
    MyGameArt.wolfieproject
    Assets/
    Cache/
```

The exact folder structure may evolve.

The Wolfie project stores:

* A stable project ID
* Project name
* Project format version
* Connected Unity project path
* Managed source assets
* Explicit engine-output mappings
* Tool configuration references where appropriate

Do not serialize ECS entities, renderer handles, ImGui state, or editor window state as authoritative project data.

Use plain domain data structures for persistent Wolfie data.

## Source and Output Ownership

Wolfie source assets are authoritative.

Unity files generated or copied by Wolfie are projections of those source assets.

The synchronization relationship is asymmetric:

```text
Wolfie source
    → publishes managed outputs into Unity

Unity project
    → provides layout, GUIDs, movement information, and unmanaged assets
```

Unity-side deletion must never delete authoritative Wolfie source assets.

Wolfie may modify or delete a Unity file only when that exact file is registered as a managed output.

Core safety rule:

> No managed-output record means no deletion.

Do not infer ownership from:

* File extension
* Folder name
* Naming convention
* File location alone

Ownership must be explicit.

## Folder Semantics

Folders are not independently owned assets.

The Wolfie asset browser represents the union of:

* The Wolfie source tree
* The Unity `Assets/` tree

A folder may therefore contain both managed and unmanaged files.

Example:

```text
Unity:
Boat/
    boat.fbx
    boat.cs

Wolfie:
Boat/
    boat.wolfasset
    boat.blend
```

If the managed Boat asset is deleted from Wolfie:

* Delete or trash the Wolfie-managed source files.
* Delete only the registered Unity output, such as `boat.fbx`.
* Do not delete `boat.cs`.
* Do not delete the Unity folder while it still contains unmanaged files.
* The remaining Unity-only folder should appear unmanaged in Wolfie.

Avoid assigning deletion intent to folders when file-level ownership provides the answer.

## Browser Presentation

The asset browser is a unified logical view rather than a direct filesystem browser.

Initial visual ownership states:

* Unity-only content: grey or otherwise marked unmanaged
* Wolfie-managed source: normal or white
* Generated output: marked with an output/generated indicator
* Missing output: warning state
* Conflict or failure: error state

Do not rely on color alone. Use icons, labels, badges, or tooltips where appropriate.

The browser should hide Unity `.meta` files while still allowing backend code to read them.

## Unity Integration

Unity is the first supported game engine.

A Unity project is valid when it contains at least:

```text
Assets/
ProjectSettings/
```

Wolfie mirrors the visible folder and file layout under Unity's `Assets/` directory.

Unity `.meta` files are hidden from the user but used to track asset GUIDs.

When Wolfie republishes an existing Unity output:

* Preserve the existing `.meta` file.
* Preserve the Unity GUID.
* Avoid replacing valid output with a partial or failed export.
* Prefer writing to a temporary file and replacing atomically.

The first MVP should support only one Unity rendering pipeline and material workflow.

Do not generalize for every Unity pipeline prematurely.

## Blender Integration

For a managed 3D model, Wolfie owns:

* The `.wolfasset` record
* The `.blend` source
* The registered Unity FBX output
* Export settings and status

Blender is initially an external editing backend.

Wolfie should control FBX export settings deterministically rather than relying on the user's last-used Blender settings.

Implement manual publishing before automatic save-triggered publishing.

The export process should:

1. Receive a specific managed asset.
2. Use known export settings.
3. Write to a temporary output.
4. Replace the Unity FBX only after successful export.
5. Preserve the Unity `.meta` file and GUID.
6. Report errors back to Wolfie.

## Substance Painter Integration

For a managed texture project, Wolfie owns:

* The texture-project relationship
* The `.spp` source file
* The registered Unity texture outputs
* The export preset
* The generated or updated Unity material

Substance Painter is initially an external editing backend.

Wolfie should control:

* Texture naming
* File formats
* Channel packing
* Normal-map orientation
* Color-space intent
* Unity output paths

Support manual texture publishing before attempting save-triggered automation.

If ordinary `.spp` save detection cannot reliably trigger export, a dedicated Save and Publish action is acceptable.

## Tool Integrations

External tools should be represented by capabilities rather than being deeply embedded into the project model.

Example capabilities:

```text
Blender:
- Mesh editing
- UV editing
- Rigging
- Animation

Substance Painter:
- Texture painting
- Baking

Future Wolfie modeler:
- Mesh editing
- UV editing

Future Wolfie painter:
- Texture painting
- Baking
```

The project should describe what an asset needs, not permanently assume which application performs the work.

## Publishing Model

Publishing should behave like a build process.

Preferred flow:

```text
Authoritative source
→ Validate
→ Generate temporary output
→ Confirm success
→ Replace engine output
→ Preserve engine identity
→ Update status
```

Publishing should be deterministic and repeatable.

Avoid user-facing export dialogs when the project already contains enough information to determine the correct settings and destination.

## Asset Status

Managed assets may have states such as:

* Clean
* Source modified
* Publishing
* Published
* Output missing
* Publish failed
* Conflict

Status should be derived from explicit records, hashes, timestamps, job state, and observed output state.

Do not silently resolve conflicts by overwriting potentially valid user work.

## Reliability Principles

Prefer safe, understandable behavior over aggressive automation.

Important principles:

* Never delete unregistered files.
* Never treat an engine-side deletion as permission to delete Wolfie source.
* Preserve the previous valid output when publishing fails.
* Make destructive operations recoverable where practical.
* Keep manual publish commands even after automatic publishing exists.
* Treat filesystem events as evidence, not necessarily user intent.
* Make background jobs visible and diagnosable.
* Avoid blocking the main UI during scans or exports.

## Scope Discipline

The MVP does not include:

* Native 3D modeling
* Native texture painting
* Native image editing
* Unreal support
* Godot support
* Rigging and animation workflows
* Shared skeletons
* Shared material libraries
* Cloud collaboration
* Version-control UI
* General plugin marketplace
* Support for every Unity render pipeline
* Full migration of arbitrary existing projects

Do not add speculative frameworks for these features unless required by the current implementation.

Prefer a small implementation that supports the current workflow over a generalized system designed around hypothetical future requirements.

## Implementation Guidance

When making a decision, prefer the option that:

1. Preserves explicit asset ownership.
2. Keeps Wolfie domain data independent from WolfEngine runtime state.
3. Avoids coupling Wolfie permanently to Blender, Painter, or Unity.
4. Produces deterministic engine outputs.
5. Protects unmanaged user files.
6. Supports manual recovery when automation fails.
7. Advances the current Unity–Blender–Painter MVP.
8. Avoids unnecessary abstraction.

When uncertain, leave a clear extension seam rather than implementing a broad framework.

## Definition of MVP Success

The MVP succeeds when a new user can:

```text
Launch Wolfie
→ Connect a Unity project
→ Create a 3D model
→ Edit it in Blender
→ Publish it into Unity
→ Create a Painter project
→ Paint and publish textures
→ Receive a configured Unity material
```

The user should not manually select FBX or texture export destinations, configure export axes for every asset, or create the Unity material by hand.

All project relationships must survive restarting Wolfie.
