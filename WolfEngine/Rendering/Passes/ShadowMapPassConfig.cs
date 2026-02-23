#nullable enable

using System.Numerics;
using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering.Passes;

public readonly struct ShadowMapExecutionBucket
{
	public ShadowMapExecutionBucket(int bucketIndex, string debugName, IGfxPipeline pipeline, IGfxIndirectCommandBuffer indirectCommandBuffer)
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

public readonly struct ShadowFrameData
{
	public ShadowFrameData(
		bool enabled,
		Matrix4x4 viewProjection,
		int shadowedDirectionalLightIndex,
		float depthBias,
		float strength,
		int mapResolution)
	{
		Enabled = enabled;
		ViewProjection = viewProjection;
		ShadowedDirectionalLightIndex = shadowedDirectionalLightIndex;
		DepthBias = depthBias;
		Strength = strength;
		MapResolution = mapResolution;
	}

	public bool Enabled { get; }
	public Matrix4x4 ViewProjection { get; }
	public int ShadowedDirectionalLightIndex { get; }
	public float DepthBias { get; }
	public float Strength { get; }
	public int MapResolution { get; }
}

public struct ShadowMapPassConfig
{
	public required int FramebufferWidth { get; init; }
	public required int FramebufferHeight { get; init; }
	public required IGfxTexture DepthTarget { get; init; }
	public required IGfxBuffer? InstanceBuffer { get; init; }
	public required IGfxBuffer? MaterialBuffer { get; init; }
	public required IGfxBuffer? DrawArgsBuffer { get; init; }
	public required IGfxBuffer? CameraBuffer { get; init; }
	public required IGfxBuffer? MaterialGenerationBuffer { get; init; }
	public required IGfxBuffer? VisibleDrawIdsPerBucketBuffer { get; init; }
	public required IGfxBuffer? DrawExecutionRangePerBucketBuffer { get; init; }
	public required ReadOnlyMemory<ShadowMapExecutionBucket> Buckets { get; init; }
	public required uint FallbackMaxCommandCount { get; init; }
}
