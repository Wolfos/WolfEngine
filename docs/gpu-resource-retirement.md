# GPU resource retirement

GPU-visible resources must not be disposed or have their bindless descriptor slots recycled while a
recorded or in-flight command can still reference them. Runtime replacement and recycling therefore
belong to `IGfxDevice`, the component that owns submission and completion.

## API contract

Queue runtime destruction with:

```csharp
device.Retire(resource, resource.Name);
```

When a texture and its descriptors have custom ownership, queue one callback so they are released as
one lifetime unit:

```csharp
device.Retire(
    () =>
    {
        texture.Dispose();
        resourceWrapper.Dispose();
    },
    texture.Name);
```

`Retire` does not guess a fence from `LastSubmittedId`. It stores the release in an unsealed queue.
Immediately before a successful primary-frame submission, the device detaches the current unsealed
batch. After submission succeeds, that batch is sealed with the exact returned submission ID. A
completion pump executes the release only once the GPU has completed that ID.

Resources reclaimed from a frame that has already been submitted use the opaque token issued by the
device instead of being retained through another frame:

```csharp
var submittedFrame = device.LastPrimarySubmission;
device.RetireAfter(submittedFrame, resource, resource.Name);
```

Tokens carry device identity and cannot be constructed by callers. A device rejects invalid or
foreign tokens, preventing systems from guessing fence values or mixing timelines.

Auxiliary submissions do not seal retirements:

```csharp
device.Submit(renderGraphCommands); // Auxiliary by default.
device.Submit(presentCommands, GpuSubmissionKind.PrimaryFrame);
```

This distinction is required because WolfEngine can submit procedural-sky, upload, precomputation,
and render-graph work before the final presentation copy. The presentation submission is the primary
frame boundary because it is the last submission that can consume render-graph output. Sealing against
an earlier auxiliary submission would recreate the same use-after-free with a more misleading fence.

`WaitForIdle` waits for submitted work and then releases both pending and unsealed retirements. Its
caller must not retain an unsubmitted command list across that idle boundary.

## Ownership rules

- Runtime replacement or recycling of a published GPU resource must use `IGfxDevice.Retire`.
- The retirement callback owns native-resource destruction and descriptor release/recycling together.
- Direct disposal is reserved for unpublished allocations, transactional creation failure, or code
  that has already established a device-idle shutdown boundary.
- Systems must not maintain their own `LastSubmittedId`/`CompletedId` retirement queues.
- `RetirementStats` is the authoritative device-wide unsealed, pending, and released diagnostic.

The render graph, transient/history resources, editor scene render targets, GPU-draw buffer growth,
ray-tracing replacement, Metal argument buffers, and Metal ImGui recycling all use this service.

## Failure behavior

If submission fails, its detached retirement batch is restored ahead of releases queued concurrently,
so nothing is destroyed against a submission that never existed. If one release callback throws,
the queue still executes every other ready release and reports the failures together.
