using System.Numerics;
using WolfEngine.Mathematics;
using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering.Passes;

public readonly struct DdgiPassConfig
{
	public required IGfxPipeline TracePipeline { get; init; }
	public required IGfxPipeline IntegratePipeline { get; init; }
	public required IGfxPipeline BorderUpdatePipeline { get; init; }
	public required IGfxTopLevelAccelerationStructure TopLevelAccelerationStructure { get; init; }
	public required DescriptorHandle TraceIrradianceHandle { get; init; }
	public required DescriptorHandle TraceVisibilityHandle { get; init; }
	public required DescriptorHandle IrradianceHistoryReadHandle { get; init; }
	public required DescriptorHandle VisibilityHistoryReadHandle { get; init; }
	public required DescriptorHandle IrradianceHistoryWriteHandle { get; init; }
	public required DescriptorHandle VisibilityHistoryWriteHandle { get; init; }
	public required DescriptorHandle EnvironmentHandle { get; init; }
	public required DescriptorHandle SamplerHandle { get; init; }
	public required IGfxBuffer InstanceBuffer { get; init; }
	public required IGfxBuffer MaterialBuffer { get; init; }
	public required IGfxBuffer MeshBuffer { get; init; }
	public required IGfxBuffer InstanceIndexToInstanceHandleBuffer { get; init; }
	public required IGfxBuffer PackedMeshVertexBuffer { get; init; }
	public required IGfxBuffer PackedMeshIndexBuffer { get; init; }
	public required Int2 IrradianceAtlasSize { get; init; }
	public required Int2 VisibilityAtlasSize { get; init; }
	public required DdgiGridShape GridShape { get; init; }
	public required Vector3 Origin { get; init; }
	public required float ProbeSpacing { get; init; }
	public required int RaysPerProbe { get; init; }
	public required int ProbeUpdateFrames { get; init; }
	public required int ProbeUpdateFrameIndex { get; init; }
	public required int ActiveProbeCount { get; init; }
	public required float MaxRayDistance { get; init; }
	public required float NormalBias { get; init; }
	public required float ViewBias { get; init; }
	public required float Hysteresis { get; init; }
	public required Vector3 DirectLightDirection { get; init; }
	public required Vector3 DirectLightColorIntensity { get; init; }
	public required uint FrameIndex { get; init; }
	public required bool HistoryValid { get; init; }
	public required bool ForceFullProbeUpdate { get; init; }
	public required bool SidecarHitShadingAvailable { get; init; }
}
