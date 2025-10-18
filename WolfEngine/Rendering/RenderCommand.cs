using System.Numerics;
using System.Runtime.InteropServices;

namespace WolfEngine;

public enum RenderCommandType
{
    CreateMesh,
    CreateMaterial,
    DrawMesh,
    SetCamera
}

public readonly struct RenderCommand
{
    private readonly IArenaAllocator _allocator;

    internal RenderCommand(RenderCommandType type, nint payload, IArenaAllocator allocator)
    {
        Type = type;
        Payload = payload;
        _allocator = allocator ?? throw new ArgumentNullException(nameof(allocator));
    }

    public RenderCommandType Type { get; }

    public nint Payload { get; }

    public T ReadPayload<T>() where T : struct
    {
        if (_allocator is null)
        {
            throw new InvalidOperationException("Allocator is not available for this render command.");
        }

        return _allocator.Read<T>(Payload);
    }

    public readonly struct CreateMeshPayload
    {
        public CreateMeshPayload(GCHandle meshHandle)
        {
            MeshHandle = meshHandle;
        }

        public GCHandle MeshHandle { get; }
    }

    public readonly struct CreateMaterialPayload
    {
        public CreateMaterialPayload(GCHandle materialHandle)
        {
            MaterialHandle = materialHandle;
        }

        public GCHandle MaterialHandle { get; }
    }

    public readonly struct DrawMeshPayload
    {
        public DrawMeshPayload(GCHandle meshHandle, GCHandle materialHandle, Matrix4x4 transform)
        {
            MeshHandle = meshHandle;
            MaterialHandle = materialHandle;
            Transform = transform;
        }

        public GCHandle MeshHandle { get; }

        public GCHandle MaterialHandle { get; }

        public Matrix4x4 Transform { get; }
    }

    public readonly struct SetCameraPayload
    {
        public SetCameraPayload(GCHandle cameraHandle)
        {
            CameraHandle = cameraHandle;
        }

        public GCHandle CameraHandle { get; }
    }
}
