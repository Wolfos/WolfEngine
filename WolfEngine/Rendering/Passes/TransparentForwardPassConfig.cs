using WolfEngine.Rendering.Abstraction;
using System.Numerics;

namespace WolfEngine.Rendering.Passes;

public readonly struct TransparentExecutionBucket
{
	public TransparentExecutionBucket(int bucketIndex, string debugName, IGfxPipeline pipeline, IGfxIndirectCommandBuffer indirectCommandBuffer)
	{
		BucketIndex = bucketIndex;
		DebugName = debugName;
		Pipeline = pipeline;
		IndirectCommandBuffer = indirectCommandBuffer;
	}

	public int BucketIndex { get; }
	public string DebugName { get; }
	public IGfxPipeline Pipeline { get; }
	public IGfxIndirectCommandBuffer IndirectCommandBuffer { get; }
}

/// <summary>
/// Describes the API-agnostic parameters required to record the forward transparent pass.
/// </summary>
public struct TransparentForwardPassConfig
{
	public required int FramebufferWidth { get; init; }
	public required int FramebufferHeight { get; init; }

	public required IGfxTexture LightingTarget { get; init; }
	public required IGfxTexture DepthTarget { get; init; }
	public required IGfxTexture ShadowMapDepth { get; init; }

	public required DescriptorHandle SkyboxEnvironment { get; init; }
	public required DescriptorHandle SkyboxIrradiance { get; init; }
	public required DescriptorHandle SkyboxPrefilter { get; init; }
	public required DescriptorHandle SkyboxBrdfLut { get; init; }
	public required DescriptorHandle LinearSampler { get; init; }
	public required DescriptorHandle ShadowMapHandle { get; init; }
	public required DescriptorHandle ShadowSampler { get; init; }

	public required Matrix4x4 ShadowViewProjection { get; init; }
	public required int ShadowedDirectionalLightIndex { get; init; }
	public required float ShadowDepthBias { get; init; }
	public required float ShadowStrength { get; init; }
	public required bool ShadowsEnabled { get; init; }
	public required float ShadowTexelSizeX { get; init; }
	public required float ShadowTexelSizeY { get; init; }

	public required IGfxBuffer? InstanceBuffer { get; init; }
	public required IGfxBuffer? MaterialBuffer { get; init; }
	public required IGfxBuffer? DrawArgsBuffer { get; init; }
	public required IGfxBuffer? CameraBuffer { get; init; }
	public required IGfxBuffer? TransparentEnvironmentBuffer { get; init; }
	public required IGfxBuffer? TransparentLightingBuffer { get; init; }
	public required IGfxBuffer? MaterialGenerationBuffer { get; init; }
	public required IGfxBuffer? VisibleDrawIdsPerBucketBuffer { get; init; }
	public required IGfxBuffer? DrawExecutionRangePerBucketBuffer { get; init; }

	public required ReadOnlyMemory<TransparentExecutionBucket> Buckets { get; init; }
	public required uint FallbackMaxCommandCount { get; init; }
}
