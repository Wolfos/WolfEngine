using System;
using WolfEngine.Rendering.Abstraction;
using System.Numerics;
using WolfEngine.Mathematics;

namespace WolfEngine.Rendering.Passes;

public readonly struct TransparentExecutionBucket
{
	public TransparentExecutionBucket(
		GpuDrawKind drawKind,
		GpuDrawBucketId bucketId,
		int executionIndex,
		string debugName,
		SharedDrawGraphicsBufferBindings bufferBindings,
		GraphicsPassBindingSet passBindings,
		IGfxPipeline pipeline,
		ReadOnlyMemory<SharedDrawIndirectCommandPage> indirectCommandPages)
	{
		DrawKind = drawKind;
		BucketId = bucketId;
		ExecutionIndex = executionIndex;
		DebugName = debugName;
		BufferBindings = bufferBindings;
		PassBindings = passBindings ?? throw new ArgumentNullException(nameof(passBindings));
		Pipeline = pipeline;
		IndirectCommandPages = indirectCommandPages;
	}

	public GpuDrawKind DrawKind { get; }
	public GpuDrawBucketId BucketId { get; }
	public int ExecutionIndex { get; }
	public string DebugName { get; }
	public SharedDrawGraphicsBufferBindings BufferBindings { get; }
	public GraphicsPassBindingSet PassBindings { get; }
	public IGfxPipeline Pipeline { get; }
	public ReadOnlyMemory<SharedDrawIndirectCommandPage> IndirectCommandPages { get; }
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
	public required IGfxTexture ShadowMapDepth0 { get; init; }
	public required IGfxTexture ShadowMapDepth1 { get; init; }
	public required IGfxTexture ShadowMapDepth2 { get; init; }

	public required DescriptorHandle SkyboxEnvironment { get; init; }
	public required DescriptorHandle SkyboxIrradiance { get; init; }
	public required DescriptorHandle SkyboxPrefilter { get; init; }
	public required DescriptorHandle SkyboxBrdfLut { get; init; }
	public required DescriptorHandle LinearSampler { get; init; }
	public required DescriptorHandle ShadowMapHandle0 { get; init; }
	public required DescriptorHandle ShadowMapHandle1 { get; init; }
	public required DescriptorHandle ShadowMapHandle2 { get; init; }
	public required DescriptorHandle ShadowSampler { get; init; }
	public required DescriptorHandle DdgiProbeState { get; init; }
	public required bool DdgiProbeStateAvailable { get; init; }
	public required bool DdgiProbeRelocationEnabled { get; init; }
	public required DdgiGridShape DdgiGridShape { get; init; }
	public required Int3 DdgiStorageOffset { get; init; }

	public required Matrix4x4 ShadowViewProjection0 { get; init; }
	public required Matrix4x4 ShadowViewProjection1 { get; init; }
	public required Matrix4x4 ShadowViewProjection2 { get; init; }
	public required float ShadowSplit0 { get; init; }
	public required float ShadowSplit1 { get; init; }
	public required float ShadowSplit2 { get; init; }
	public required float ShadowCascadeBlendDistance { get; init; }
	public required int ShadowedDirectionalLightIndex { get; init; }
	public required Vector3 ShadowDepthBiases { get; init; }
	public required float ShadowStrength { get; init; }
	public required bool ShadowsEnabled { get; init; }
	public required float ShadowMaxDistance { get; init; }
	public required float ShadowTexelSizeX { get; init; }
	public required float ShadowTexelSizeY { get; init; }
	public required float NearPlane { get; init; }
	public required float FarPlane { get; init; }

	public required IGfxBuffer? InstanceBuffer { get; init; }
	public required IGfxBuffer? MaterialBuffer { get; init; }
	public required IGfxBuffer? DrawArgsBuffer { get; init; }
	public required IGfxBuffer? CameraBuffer { get; init; }
	public required IGfxBuffer? TransparentEnvironmentBuffer { get; init; }
	public required IGfxBuffer? TransparentLightingBuffer { get; init; }
	public required IGfxBuffer? DdgiDebugBuffer { get; init; }
	public required IGfxBuffer? PointLightBuffer { get; init; }
	public required IGfxBuffer? ClusterHeaderBuffer { get; init; }
	public required IGfxBuffer? ClusterLightIndexBuffer { get; init; }
	public required IGfxBuffer? MaterialGenerationBuffer { get; init; }
	public required IGfxBuffer? DrawExecutionRangePerBucketBuffer { get; init; }

	public required ReadOnlyMemory<TransparentExecutionBucket> Buckets { get; init; }
	public required uint FallbackMaxCommandCount { get; init; }

	/// <summary>Packed geometry shared by every shared draw, bound once for the whole pass.</summary>
	public IGfxBuffer? PackedVertexBuffer { get; init; }
	public IGfxBuffer? PackedIndexBuffer { get; init; }
	public uint PackedVertexStride { get; init; }
}
