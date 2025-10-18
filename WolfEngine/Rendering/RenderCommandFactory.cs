using System.Numerics;
using System.Runtime.InteropServices;

namespace WolfEngine;

public interface IRenderCommandFactory
{
    RenderCommand CreateMesh(Mesh mesh);

    RenderCommand CreateMaterial(Material material);

    RenderCommand DrawMesh(Mesh mesh, Material material, Matrix4x4 transform);

    RenderCommand SetCamera(Camera camera);
}


public class RenderCommandFactory : IRenderCommandFactory
{
    private readonly IArenaAllocator _arenaAllocator;

    public RenderCommandFactory(IArenaAllocator arenaAllocator)
    {
        _arenaAllocator = arenaAllocator ?? throw new ArgumentNullException(nameof(arenaAllocator));
    }

    public RenderCommand CreateMesh(Mesh mesh)
    {
        ArgumentNullException.ThrowIfNull(mesh);

        var payload = new RenderCommand.CreateMeshPayload(GCHandle.Alloc(mesh));
        var pointer = _arenaAllocator.Store(payload);
        return new RenderCommand(RenderCommandType.CreateMesh, pointer, _arenaAllocator);
    }

    public RenderCommand CreateMaterial(Material material)
    {
        ArgumentNullException.ThrowIfNull(material);

        var payload = new RenderCommand.CreateMaterialPayload(GCHandle.Alloc(material));
        var pointer = _arenaAllocator.Store(payload);
        return new RenderCommand(RenderCommandType.CreateMaterial, pointer, _arenaAllocator);
    }

    public RenderCommand DrawMesh(Mesh mesh, Material material, Matrix4x4 transform)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(material);

        var payload = new RenderCommand.DrawMeshPayload(GCHandle.Alloc(mesh), GCHandle.Alloc(material), transform);
        var pointer = _arenaAllocator.Store(payload);
        return new RenderCommand(RenderCommandType.DrawMesh, pointer, _arenaAllocator);
    }

    public RenderCommand SetCamera(Camera camera)
    {
        ArgumentNullException.ThrowIfNull(camera);

        var payload = new RenderCommand.SetCameraPayload(GCHandle.Alloc(camera));
        var pointer = _arenaAllocator.Store(payload);
        return new RenderCommand(RenderCommandType.SetCamera, pointer, _arenaAllocator);
    }
}
