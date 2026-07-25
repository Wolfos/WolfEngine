using WolfEngine.Mathematics;
using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering.Passes;

/// <summary>
/// API-agnostic parameters for the reflection tracing pass. Both tracing modes read the
/// G-buffer plus the previous frame's color pyramid and write traced radiance with a
/// replacement weight that deferred lighting folds into its specular term.
/// </summary>
public readonly struct ReflectionsPassConfig
{
	public required IGfxPipeline Pipeline { get; init; }
	public required ReflectionMode Mode { get; init; }
	public required DescriptorHandle Depth { get; init; }
	public required DescriptorHandle Normal { get; init; }
	public required DescriptorHandle Material { get; init; }
	public required DescriptorHandle Velocity { get; init; }
	public required DescriptorHandle Environment { get; init; }
	public required DescriptorHandle Irradiance { get; init; }
	public required DescriptorHandle PrefilteredEnvironment { get; init; }
	public required DescriptorHandle BrdfLut { get; init; }
	public required DescriptorHandle Output { get; init; }
	public required DescriptorHandle LinearSampler { get; init; }
	/// <summary>Always <see cref="ReflectionsPass.MaxColorPyramidLevels"/> long; the tail repeats the coarsest level.</summary>
	public required DescriptorHandle[] ColorPyramidLevels { get; init; }
	public required int ColorPyramidLevelCount { get; init; }
	public required bool ColorPyramidValid { get; init; }
	public required Int2 DispatchSize { get; init; }
	public required int MaxSteps { get; init; }
	public required int BinarySearchSteps { get; init; }
	public required float MaxRayDistance { get; init; }
	public required float Thickness { get; init; }
	public required float Bias { get; init; }
	public required float MaxRoughness { get; init; }
	public required float EdgeFade { get; init; }
	public required float ScreenReuseFalloff { get; init; }
	public required float ReprojectionStrength { get; init; }
	public required float Intensity { get; init; }
	public IGfxTopLevelAccelerationStructure? TopLevelAccelerationStructure { get; init; }
	public IGfxBuffer? InstanceBuffer { get; init; }
	public IGfxBuffer? MaterialBuffer { get; init; }
	public IGfxBuffer? InstanceIndexToInstanceHandleBuffer { get; init; }
	public IGfxBuffer? MeshBuffer { get; init; }
	public IGfxBuffer? PackedMeshVertexBuffer { get; init; }
	public IGfxBuffer? PackedMeshIndexBuffer { get; init; }
}
