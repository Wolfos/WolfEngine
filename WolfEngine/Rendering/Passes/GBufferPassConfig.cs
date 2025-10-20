#nullable enable

using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering.Passes;

/// <summary>
/// Describes the API-agnostic parameters required to record the G-buffer pass.
/// </summary>
public sealed class GBufferPassConfig
{
    public required PipelineKey Pipeline { get; init; }

    public required IGfxTexture AlbedoTarget { get; init; }

    public required IGfxTexture NormalTarget { get; init; }

    public required IGfxTexture MaterialTarget { get; init; }

    public required IGfxTexture DepthTarget { get; init; }

    public float[] AlbedoClearColor { get; init; } = [0.0f, 0.0f, 0.0f, 1.0f];

    public float[] NormalClearColor { get; init; } = [0.5f, 0.5f, 1.0f, 1.0f];

    public float[] MaterialClearColor { get; init; } = [0.0f, 0.0f, 0.0f, 1.0f];
}
