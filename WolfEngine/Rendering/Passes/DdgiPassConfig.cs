using System.Numerics;
using WolfEngine.Mathematics;
using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering.Passes;

public readonly struct DdgiPassConfig
{
	public required IGfxPipeline TracePipeline { get; init; }
	public required IGfxPipeline RelocationTracePipeline { get; init; }
	public required IGfxPipeline ClassifyPipeline { get; init; }
	public required IGfxPipeline RelocatePipeline { get; init; }
	public required IGfxPipeline IrradianceIntegratePipeline { get; init; }
	public required IGfxPipeline VisibilityIntegratePipeline { get; init; }
	public required IGfxTopLevelAccelerationStructure TopLevelAccelerationStructure { get; init; }
	public required DescriptorHandle TraceIrradianceHandle { get; init; }
	public required DescriptorHandle TraceVisibilityHandle { get; init; }
	public required DescriptorHandle TraceIrradianceReadHandle { get; init; }
	public required DescriptorHandle TraceVisibilityReadHandle { get; init; }
	public required DescriptorHandle IrradianceL0HistoryReadHandle { get; init; }
	public required DescriptorHandle IrradianceLyHistoryReadHandle { get; init; }
	public required DescriptorHandle IrradianceLzHistoryReadHandle { get; init; }
	public required DescriptorHandle IrradianceLxHistoryReadHandle { get; init; }
	public required DescriptorHandle VisibilityHistoryReadHandle { get; init; }
	public required DescriptorHandle IrradianceL0HistoryWriteHandle { get; init; }
	public required DescriptorHandle IrradianceLyHistoryWriteHandle { get; init; }
	public required DescriptorHandle IrradianceLzHistoryWriteHandle { get; init; }
	public required DescriptorHandle IrradianceLxHistoryWriteHandle { get; init; }
	public required DescriptorHandle VisibilityHistoryWriteHandle { get; init; }
	public required DescriptorHandle ProbeStateReadHandle { get; init; }
	public required DescriptorHandle ProbeStateCurrentHandle { get; init; }
	public required DescriptorHandle ProbeStateWriteHandle { get; init; }
	public required DescriptorHandle ProbeActivityReadHandle { get; init; }
	public required DescriptorHandle ProbeActivityWriteHandle { get; init; }
	public required DescriptorHandle ProbeRelocationDecisionHandle { get; init; }
	public required DescriptorHandle EnvironmentHandle { get; init; }
	public required DescriptorHandle SamplerHandle { get; init; }
	public required IGfxBuffer InstanceBuffer { get; init; }
	public required IGfxBuffer DrawCommandBuffer { get; init; }
	public required IGfxBuffer MaterialBuffer { get; init; }
	public required IGfxBuffer MeshBuffer { get; init; }
	public required IGfxBuffer InstanceIndexToInstanceHandleBuffer { get; init; }
	public required IGfxBuffer InstanceIndexToTerrainRayTracingResolutionBuffer { get; init; }
	public required IGfxBuffer PackedMeshVertexBuffer { get; init; }
	public required IGfxBuffer PackedMeshIndexBuffer { get; init; }
	public required IGfxBuffer TerrainMaterialBuffer { get; init; }
	public required IGfxBuffer TerrainLayerBuffer { get; init; }
	public required IGfxBuffer IrradianceEstimatorBuffer { get; init; }
	public required IGfxBuffer FirstProbeRelocationDiagnosticBuffer { get; init; }
	public required IGfxBuffer FirstProbeRelocationReadbackBuffer { get; init; }
	public required Int2 IrradianceAtlasSize { get; init; }
	public required Int2 VisibilityAtlasSize { get; init; }
	public required DdgiGridShape GridShape { get; init; }
	public required Vector3 Origin { get; init; }
	public required Int3 StorageOffset { get; init; }
	public required Int3 ScrollDelta { get; init; }
	public required float ProbeSpacing { get; init; }
	public required int RaysPerProbe { get; init; }
	public required int ProbeUpdateFrames { get; init; }
	public required int ProbeUpdateFrameIndex { get; init; }
	public required int ActiveProbeCount { get; init; }
	public required uint ActiveDrawCommandUpperBound { get; init; }
	public required float MaxRayDistance { get; init; }
	public required float NormalBias { get; init; }
	public required float ViewBias { get; init; }
	public required float IrradianceTemporalBlendSpeed { get; init; }
	public required float Hysteresis { get; init; }
	public required float RecursiveBounceEnergy { get; init; }
	public required bool ProbeRelocationEnabled { get; init; }
	public required bool DebugFirstProbeRelocationReadback { get; init; }
	public required int DebugProbeRelocationReadbackIndex { get; init; }
	public required float ProbeMinFrontfaceDistance { get; init; }
	public required float ProbeBackfaceThreshold { get; init; }
	public required float ProbeMaxRelocationDistance { get; init; }
	public required Vector3 DirectLightDirection { get; init; }
	public required Vector3 DirectLightColorIntensity { get; init; }
	public required uint FrameIndex { get; init; }
	public required bool HistoryValid { get; init; }
	public required bool ForceFullProbeUpdate { get; init; }
	public required bool SidecarHitShadingAvailable { get; init; }
}
