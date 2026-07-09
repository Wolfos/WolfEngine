#nullable enable

using System;
using System.Numerics;
using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering.Passes;

public readonly struct ShadowMapExecutionBucket
{
	public ShadowMapExecutionBucket(
		GpuDrawKind drawKind,
		GpuDrawBucketId bucketId,
		int executionIndex,
		string debugName,
		SharedDrawGraphicsBufferBindings bufferBindings,
		IGfxPipeline pipeline,
		ReadOnlyMemory<SharedDrawIndirectCommandPage> indirectCommandPages)
	{
		DrawKind = drawKind;
		BucketId = bucketId;
		ExecutionIndex = executionIndex;
		DebugName = debugName;
		BufferBindings = bufferBindings;
		Pipeline = pipeline;
		IndirectCommandPages = indirectCommandPages;
	}

	public GpuDrawKind DrawKind { get; }
	public GpuDrawBucketId BucketId { get; }
	public int ExecutionIndex { get; }
	public string DebugName { get; }
	public SharedDrawGraphicsBufferBindings BufferBindings { get; }
	public IGfxPipeline Pipeline { get; }
	public ReadOnlyMemory<SharedDrawIndirectCommandPage> IndirectCommandPages { get; }
}

public readonly struct ShadowFrameData
{
	public ShadowFrameData(
		bool enabled,
		int cascadeCount,
		Matrix4x4 cascadeViewProjection0,
		Matrix4x4 cascadeViewProjection1,
		Matrix4x4 cascadeViewProjection2,
		float cascadeSplit0,
		float cascadeSplit1,
		float cascadeSplit2,
		float cascadeBlendDistance,
		float maxDistance,
		int shadowedDirectionalLightIndex,
		Vector3 depthBiases,
		float strength,
		int mapResolution)
	{
		Enabled = enabled;
		CascadeCount = cascadeCount;
		CascadeViewProjection0 = cascadeViewProjection0;
		CascadeViewProjection1 = cascadeViewProjection1;
		CascadeViewProjection2 = cascadeViewProjection2;
		CascadeSplit0 = cascadeSplit0;
		CascadeSplit1 = cascadeSplit1;
		CascadeSplit2 = cascadeSplit2;
		CascadeBlendDistance = cascadeBlendDistance;
		MaxDistance = maxDistance;
		ShadowedDirectionalLightIndex = shadowedDirectionalLightIndex;
		DepthBiases = depthBiases;
		Strength = strength;
		MapResolution = mapResolution;
	}

	public bool Enabled { get; }
	public int CascadeCount { get; }
	public Matrix4x4 CascadeViewProjection0 { get; }
	public Matrix4x4 CascadeViewProjection1 { get; }
	public Matrix4x4 CascadeViewProjection2 { get; }
	public float CascadeSplit0 { get; }
	public float CascadeSplit1 { get; }
	public float CascadeSplit2 { get; }
	public float CascadeBlendDistance { get; }
	public float MaxDistance { get; }
	public int ShadowedDirectionalLightIndex { get; }
	public Vector3 DepthBiases { get; }
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
	public required IGfxBuffer? TerrainMaterialBuffer { get; init; }
	public required IGfxBuffer? TerrainLayerBuffer { get; init; }
	public required IGfxBuffer? DrawArgsBuffer { get; init; }
	public required IGfxBuffer? CameraBuffer { get; init; }
	public required IGfxBuffer? MaterialGenerationBuffer { get; init; }
	public required IGfxBuffer? DrawExecutionRangePerBucketBuffer { get; init; }
	public required ulong DrawArgsBaseOffsetBytes { get; init; }
	public required ReadOnlyMemory<ShadowMapExecutionBucket> Buckets { get; init; }
	public required uint FallbackMaxCommandCount { get; init; }
}
