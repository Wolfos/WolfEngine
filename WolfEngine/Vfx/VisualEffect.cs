using WolfEngine.AssetPipeline;
using WolfEngine.ECS;

namespace WolfEngine.Vfx;

/// <summary>
/// Plays an effect on this entity and follows it, with no code. Put it on a child entity to offset the effect, or on
/// a bone's socket entity to follow the bone. Gameplay turns it on and off with <see cref="Playing"/>; turning it
/// back on starts a fresh effect while the previous one fades out.
/// </summary>
public struct VisualEffect : IEntityComponent
{
	public VisualEffect()
	{
	}

	public AssetRef<VfxAsset> Effect;

	/// <summary>
	/// Whether the effect should be playing. A one-shot effect plays once each time this turns on; a continuous one
	/// plays until it turns off.
	/// </summary>
	public bool Playing = true;
}
