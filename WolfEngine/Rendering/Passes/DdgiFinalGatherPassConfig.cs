using System.Numerics;
using WolfEngine.Mathematics;
using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering.Passes;

/// <summary>
/// API-agnostic parameters for the DDGI final gather trace. It reads the G-buffer, the integrated probe atlases
/// and the scene's ray tracing tables, and writes one traced diffuse sample per trace pixel.
/// </summary>
public readonly struct DdgiFinalGatherPassConfig
{
	public required IGfxPipeline Pipeline { get; init; }
	public required DescriptorHandle Depth { get; init; }
	public required DescriptorHandle Normal { get; init; }
	public required DescriptorHandle Output { get; init; }
	public required DescriptorHandle Environment { get; init; }
	public required DescriptorHandle Irradiance { get; init; }
	public required DescriptorHandle LinearSampler { get; init; }
	public required DescriptorHandle ErrorTexture { get; init; }
	public required DescriptorHandle DdgiIrradianceL0 { get; init; }
	public required DescriptorHandle DdgiIrradianceLy { get; init; }
	public required DescriptorHandle DdgiIrradianceLz { get; init; }
	public required DescriptorHandle DdgiIrradianceLx { get; init; }
	public required DescriptorHandle DdgiVisibility { get; init; }
	public required DescriptorHandle DdgiProbeState { get; init; }
	public required DescriptorHandle DdgiProbeActivity { get; init; }
	public required DescriptorHandle DdgiProbeRelocationDecision { get; init; }
	public required Int2 FramebufferSize { get; init; }
	public required Int2 DispatchSize { get; init; }
	public required uint FrameIndex { get; init; }
	public required bool SidecarHitShadingAvailable { get; init; }
	public required float MaxRayDistance { get; init; }
	public required float Bias { get; init; }
	public required float FireflyClamp { get; init; }
	public required Vector3 DirectLightDirection { get; init; }
	public required Vector3 DirectLightColorIntensity { get; init; }
	public required Vector3 DdgiOrigin { get; init; }
	public required float DdgiProbeSpacing { get; init; }
	public required Int3 DdgiStorageOffset { get; init; }
	public required DdgiGridShape DdgiGridShape { get; init; }
	public required int DdgiVisibilityTileInteriorSize { get; init; }
	public required float DdgiMaxRayDistance { get; init; }
	public required float DdgiViewBias { get; init; }
	public required float DdgiHorizontalBlendDistance { get; init; }
	public required float DdgiVerticalBlendDistance { get; init; }
	public required bool DdgiProbeRelocationEnabled { get; init; }
	public required IGfxTopLevelAccelerationStructure TopLevelAccelerationStructure { get; init; }
	public required IGfxBuffer InstanceBuffer { get; init; }
	public required IGfxBuffer MaterialBuffer { get; init; }
	public required IGfxBuffer InstanceIndexToInstanceHandleBuffer { get; init; }
	public required IGfxBuffer MeshBuffer { get; init; }
	public required IGfxBuffer PackedMeshVertexBuffer { get; init; }
	public required IGfxBuffer PackedMeshIndexBuffer { get; init; }
	public required IGfxBuffer TerrainMaterialBuffer { get; init; }
	public required IGfxBuffer TerrainLayerBuffer { get; init; }
	public required IGfxBuffer InstanceIndexToTerrainRayTracingResolutionBuffer { get; init; }
}
