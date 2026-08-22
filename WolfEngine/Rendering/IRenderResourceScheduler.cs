namespace WolfEngine.Rendering;

/// <summary>
/// Queues render-resource creation and release work for the renderer.
/// </summary>
public interface IRenderResourceScheduler
{
	void EnsureTextureResources(Texture texture);
	void EnsureMeshResources(Mesh mesh);
	void ReleaseMeshResources(Mesh mesh);
}
