#nullable enable

using System;
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
		Matrix4x4 cascadeViewProjection0,
		Matrix4x4 cascadeViewProjection1,
		Matrix4x4 cascadeViewProjection2,
		float cascadeSplit0,
		float cascadeSplit1,
		float cascadeSplit2,
		float cascadeBlendDistance,
		int shadowedDirectionalLightIndex,
		float depthBias,
		float strength,
		int mapResolution)
	{
		Enabled = enabled;
		CascadeViewProjection0 = cascadeViewProjection0;
		CascadeViewProjection1 = cascadeViewProjection1;
		CascadeViewProjection2 = cascadeViewProjection2;
		CascadeSplit0 = cascadeSplit0;
		CascadeSplit1 = cascadeSplit1;
		CascadeSplit2 = cascadeSplit2;
		CascadeBlendDistance = cascadeBlendDistance;
		ShadowedDirectionalLightIndex = shadowedDirectionalLightIndex;
		DepthBias = depthBias;
		Strength = strength;
		MapResolution = mapResolution;
	}

	public bool Enabled { get; }
	public Matrix4x4 CascadeViewProjection0 { get; }
	public Matrix4x4 CascadeViewProjection1 { get; }
	public Matrix4x4 CascadeViewProjection2 { get; }
	public float CascadeSplit0 { get; }
	public float CascadeSplit1 { get; }
	public float CascadeSplit2 { get; }
	public float CascadeBlendDistance { get; }
	public int ShadowedDirectionalLightIndex { get; }
	public float DepthBias { get; }
	public float Strength { get; }
	public int MapResolution { get; }

	public Matrix4x4 GetCascadeViewProjection(int cascadeIndex) => cascadeIndex switch
	{
		0 => CascadeViewProjection0,
		1 => CascadeViewProjection1,
		2 => CascadeViewProjection2,
		_ => throw new ArgumentOutOfRangeException(nameof(cascadeIndex), cascadeIndex, "Cascade index is out of range.")
	};

	public float GetCascadeSplit(int cascadeIndex) => cascadeIndex switch
	{
		0 => CascadeSplit0,
		1 => CascadeSplit1,
		2 => CascadeSplit2,
		_ => throw new ArgumentOutOfRangeException(nameof(cascadeIndex), cascadeIndex, "Cascade index is out of range.")
	};
}

public struct ShadowMapPassConfig
{
	public required int CascadeIndex { get; init; }
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
