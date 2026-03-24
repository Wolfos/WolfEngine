using WolfEngine.AssetPipeline;
using WolfEngine.ECS;
using WolfEngine.Rendering.Passes;

namespace WolfEngine.Rendering;

public struct WorldSettings: IEntityComponent
{
	public AssetRef<RenderConfig> RenderConfigAsset;
}
