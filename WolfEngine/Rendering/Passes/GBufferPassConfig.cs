#nullable enable

using System.Numerics;
using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering.Passes;

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

    public required IGfxTexture DepthTarget { get; init; }

    public Vector4 AlbedoClearColor { get; init; }

    public Vector4 NormalClearColor { get; init; }

    public Vector4 MaterialClearColor { get; init; }
    public Vector4 EmissiveClearColor { get; init; }


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

	public IGfxPipeline? GBufferPipeline { get; set; }

	public IGfxIndirectCommandBuffer? IndirectCommandBuffer { get; set; }

	public IGfxBuffer? VisibleDrawIdsBuffer { get; set; }

	public IGfxBuffer? DrawCountBuffer { get; set; }

	public IGfxBuffer? DrawExecutionRangeBuffer { get; set; }

	public uint FallbackMaxCommandCount { get; set; }
}
