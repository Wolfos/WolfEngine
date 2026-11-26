using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering.Shaders;

namespace WolfEngine.Rendering;

/// <summary>
/// The ray-tracing acceleration structures of the scene being drawn. Loading another scene replaces them as a
/// whole, so a pass holds this and reads <see cref="Resources"/> when its graph nodes execute.
/// </summary>
internal sealed class RayTracingScene
{
	private readonly IShaderProvider _shaderProvider;

	public RayTracingScene(IShaderProvider shaderProvider)
	{
		_shaderProvider = shaderProvider ?? throw new ArgumentNullException(nameof(shaderProvider));
		Resources = new RayTracingSceneResources(shaderProvider);
	}

	public RayTracingSceneResources Resources { get; private set; }

	/// <summary>
	/// Starts over with empty acceleration structures and retires the old ones, which frames in flight may still
	/// trace against.
	/// </summary>
	public void Reset(IGfxDevice device)
	{
		ArgumentNullException.ThrowIfNull(device);

		var previous = Resources;
		Resources = new RayTracingSceneResources(_shaderProvider);
		device.Retire(previous, "Replaced scene ray tracing resources");
	}
}
