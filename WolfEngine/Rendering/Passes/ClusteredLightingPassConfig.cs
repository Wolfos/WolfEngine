using WolfEngine.Mathematics;
using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering.Passes;

public readonly struct ClusteredLightingPassConfig
{
	public required IGfxPipeline BuildClustersPipeline { get; init; }
	public required IGfxPipeline CountLightsPipeline { get; init; }
	public required IGfxPipeline PrefixOffsetsPipeline { get; init; }
	public required IGfxPipeline WriteLightIndicesPipeline { get; init; }

	public required IGfxBuffer PointLightBuffer { get; init; }
	public required IGfxBuffer ClusterAabbBuffer { get; init; }
	public required IGfxBuffer ClusterHeaderBuffer { get; init; }
	public required IGfxBuffer ClusterLightIndexBuffer { get; init; }
	public required IGfxBuffer ClusterWriteCursorBuffer { get; init; }
	public required IGfxBuffer ClusterOverflowBuffer { get; init; }

	public required Int3 Grid { get; init; }
	public required Int2 FramebufferSize { get; init; }
	public required int ClusterCount { get; init; }
	public required int LightIndexCapacity { get; init; }
}
