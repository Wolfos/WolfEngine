# Runtime scene loading

Gameplay systems can resolve `ISceneLoadService` from the service provider passed to
`IGameplayModule.CreateSystems`. Store a target scene in an `AssetRef<SceneAsset>` field;
the editor's asset picker accepts scene assets for this type.

```csharp
Task pendingLoad = scenes.LoadAsync(world, targetScene);
```

The host consumes requests between frames. Do not synchronously wait on the task from
`Update`, `PhysicsUpdate`, or a lifecycle callback. The task completes after the new
world and gameplay module are bound; rendering completes on later frames. A missing
or invalid scene faults the task and keeps the current scene running. Requests queued
by a world that is no longer active are canceled.

The standalone runtime loads cooked scenes from its mounted packs. Include scene IDs
in `WolfEngineBuild.json` or reference them through serialized asset fields so the
build includes their dependency closure. `IRuntimeSceneLoader.Load` only constructs a
world; `GameplayWorldSession` owns registration, view rebinding, and teardown.

Replacement calls `OnUnloading` for the outgoing world, stops audio playback, and
notifies world-removal listeners before detaching gameplay systems. Physics and
animation state are cleared, private skinning geometry and the old RT scene are
retired through the GPU submission fences, and the render view drops scene history.
Immutable asset caches remain shared across scene loads. Gameplay systems are
recreated through `CreateSystems` and `OnLoaded` receives the new world. Cleanup
continues if a removal listener throws; the standalone runtime treats lifecycle
failures as fatal rather than continuing with partially torn-down state.

Editor Play mode uses the same request service and loads an isolated runtime copy
of the requested scene. It preserves the authoring scene and current playing or
paused state. Stopping Play returns to the original authoring scene. The automation
host's `load_play_scene` tool exercises this path; `load_scene` loads an authoring
scene through the editor UI path.
