#nullable enable

using System;
using System.Numerics;
using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering.Passes;

public readonly struct GBufferExecutionBucket
{
    public GBufferExecutionBucket(
		GpuDrawKind drawKind,
		GpuDrawBucketId bucketId,
		int executionIndex,
		string debugName,
		IGfxPipeline pipeline,
		IGfxIndirectCommandBuffer indirectCommandBuffer)
    {
		DrawKind = drawKind;
		BucketId = bucketId;
		ExecutionIndex = executionIndex;
		DebugName = debugName;
		Pipeline = pipeline;
		IndirectCommandBuffer = indirectCommandBuffer;
    }

    public GpuDrawKind DrawKind { get; }
    public GpuDrawBucketId BucketId { get; }
    public int ExecutionIndex { get; }
    public string DebugName { get; }
    public IGfxPipeline Pipeline { get; }
	public IGfxIndirectCommandBuffer IndirectCommandBuffer { get; }
}

/// <summary>
/// Describes the API-agnostic parameters required to record the G-buffer pass.
/// </summary>
public struct GBufferPassConfig
{
    public required int FramebufferWidth { get; init; }

    public required int FramebufferHeight { get; init; }

    public required IGfxTexture AlbedoTarget { get; init; }

    public required IGfxTexture NormalTarget { get; init; }

    public required IGfxTexture MaterialTarget { get; init; }
    public required IGfxTexture EmissiveTarget { get; init; }

    public required IGfxTexture VelocityTarget { get; init; }

    public required IGfxTexture DepthTarget { get; init; }

    public ColorRGBA AlbedoClearColor { get; init; }

    public ColorRGBA NormalClearColor { get; init; }

    public ColorRGBA MaterialClearColor { get; init; }
    public ColorRGBA EmissiveClearColor { get; init; }
    public ColorRGBA VelocityClearColor { get; init; }


    public float DepthClearValue { get; init; }

    // Optional skybox
    public IGfxPipeline? SkyboxPipeline { get; set; }
    public DescriptorHandle SkyboxEnvironment { get; set; }
    public DescriptorHandle SkyboxSampler { get; set; }
    public Matrix4x4? InvViewProjection { get; set; }
	public Mesh? SkyboxMesh { get; set; }

	public IGfxBuffer? InstanceBuffer { get; set; }

	public IGfxBuffer? MaterialBuffer { get; set; }

	public IGfxBuffer? DrawArgsBuffer { get; set; }

	public IGfxBuffer? CameraBuffer { get; set; }

	public required ShaderConstantBufferLayout CameraLayout { get; init; }

	public IGfxBuffer? MaterialGenerationBuffer { get; set; }

	public IGfxBuffer? VisibleDrawIdsPerExecutionLaneBuffer { get; set; }

	public IGfxBuffer? DrawCountPerBucketBuffer { get; set; }

	public IGfxBuffer? DrawExecutionRangePerBucketBuffer { get; set; }

	public ReadOnlyMemory<GBufferExecutionBucket> Buckets { get; set; }

	public uint FallbackMaxCommandCount { get; set; }
}
