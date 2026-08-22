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

	public IGfxBuffer? TerrainMaterialBuffer { get; set; }

	public IGfxBuffer? TerrainLayerBuffer { get; set; }

	public IGfxBuffer? DrawArgsBuffer { get; set; }

	public IGfxBuffer? CameraBuffer { get; set; }

	public required ShaderConstantBufferLayout CameraLayout { get; init; }

	public IGfxBuffer? MaterialGenerationBuffer { get; set; }

	public IGfxBuffer? DrawCountPerBucketBuffer { get; set; }

	public IGfxBuffer? DrawExecutionRangePerBucketBuffer { get; set; }

	public ReadOnlyMemory<GBufferExecutionBucket> Buckets { get; set; }

	public uint FallbackMaxCommandCount { get; set; }

	/// <summary>Indirect command slot the buckets' pages were taken from, needed to index the count table.</summary>
	public int IndirectCommandSlot { get; set; }

	/// <summary>Packed geometry shared by every shared draw, bound once for the whole pass.</summary>
	public IGfxBuffer? PackedVertexBuffer { get; set; }
	public IGfxBuffer? PackedIndexBuffer { get; set; }
	public uint PackedVertexStride { get; set; }

	/// <summary>
	/// Per-page compacted command counts when compaction ran this frame, otherwise null, which selects
	/// the full-range execution path.
	/// </summary>
	public IGfxBuffer? CompactedExecutionRangeBuffer { get; set; }
}
