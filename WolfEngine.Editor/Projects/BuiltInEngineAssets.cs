using WolfEngine.AssetPipeline;
using WolfEngine.Rendering;

namespace WolfEngine.Editor.Projects;

/// <summary>
/// Persistent references to assets in <c>BuiltInContent/Assets</c>. The node IDs are owned by the committed
/// <c>.meta</c> files next to those sources, so they must be updated together.
/// </summary>
public static class BuiltInEngineAssets
{
	public static readonly AssetRef<Material> DefaultMaterial = new() { NodeId = new Guid("b8d409d6-fbb9-43eb-9326-8559949535ca") };
	public static readonly AssetRef<Mesh> CubeMesh = new() { NodeId = new Guid("ee15038d-b4d5-44c2-a0c6-a62caa57570f") };
	public static readonly AssetRef<Mesh> SphereMesh = new() { NodeId = new Guid("c51b476b-26a7-4ea9-aaac-8be9c30cf5aa") };
}
