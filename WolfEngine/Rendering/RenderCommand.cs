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
    private RenderCommand(RenderCommandType type, nint payload)
    {
        Type = type;
        Payload = payload;
    }

    public RenderCommandType Type { get; }

    public nint Payload { get; }

    public static RenderCommand CreateMesh(Mesh mesh)
    {
        ArgumentNullException.ThrowIfNull(mesh);

        var payload = new CreateMeshPayload(GCHandle.Alloc(mesh));
        var pointer = ArenaAllocator.RenderCommands.Store(payload);
        return new RenderCommand(RenderCommandType.CreateMesh, pointer);
    }

    public static RenderCommand CreateMaterial(Material material)
    {
        ArgumentNullException.ThrowIfNull(material);

        var payload = new CreateMaterialPayload(GCHandle.Alloc(material));
        var pointer = ArenaAllocator.RenderCommands.Store(payload);
        return new RenderCommand(RenderCommandType.CreateMaterial, pointer);
    }

    public static RenderCommand DrawMesh(Mesh mesh, Material material, Matrix4x4 transform)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(material);

        var payload = new DrawMeshPayload(GCHandle.Alloc(mesh), GCHandle.Alloc(material), transform);
        var pointer = ArenaAllocator.RenderCommands.Store(payload);
        return new RenderCommand(RenderCommandType.DrawMesh, pointer);
    }

    public static RenderCommand SetCamera(Camera camera)
    {
        ArgumentNullException.ThrowIfNull(camera);

        var payload = new SetCameraPayload(GCHandle.Alloc(camera));
        var pointer = ArenaAllocator.RenderCommands.Store(payload);
        return new RenderCommand(RenderCommandType.SetCamera, pointer);
    }

    public T ReadPayload<T>() where T : struct
    {
        return ArenaAllocator.RenderCommands.Read<T>(Payload);
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
