using System.Numerics;
using System.Runtime.InteropServices;
using WolfEngine.ECS;

namespace WolfEngine;

public interface IRenderCommandFactory
{
    RenderCommand CreateMesh(Mesh mesh);
    RenderCommand DrawMesh(ref MeshRenderer meshRenderer, ref Transform transform);

    RenderCommand SetCamera(ref Camera camera, ref Transform transform);
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

    public RenderCommand DrawMesh(ref MeshRenderer meshRenderer, ref Transform transform)
    {
        var payload = new RenderCommand.DrawMeshPayload(GCHandle.Alloc(meshRenderer.Mesh), GCHandle.Alloc(meshRenderer.Material), transform.GetTransform());
        var pointer = _arenaAllocator.Store(payload);
        return new RenderCommand(RenderCommandType.DrawMesh, pointer, _arenaAllocator);
    }

    public RenderCommand SetCamera(ref Camera camera, ref Transform transform)
    {
        var payload = new RenderCommand.SetCameraPayload(camera, transform);
        var pointer = _arenaAllocator.Store(payload);
        return new RenderCommand(RenderCommandType.SetCamera, pointer, _arenaAllocator);
    }
}
