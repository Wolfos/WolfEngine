using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering.Passes;

public readonly struct SharedDrawIndirectEncodeResources
{
	public SharedDrawIndirectEncodeResources(
		IGfxBuffer? instanceBuffer,
		IGfxBuffer? materialBuffer,
		IGfxBuffer? drawArgsBuffer,
		ulong drawArgsBaseOffsetBytes,
		IGfxBuffer? materialGenerationBuffer,
		MeshIndexStream indexStream = MeshIndexStream.Main)
	{
		InstanceBuffer = instanceBuffer;
		MaterialBuffer = materialBuffer;
		DrawArgsBuffer = drawArgsBuffer;
		DrawArgsBaseOffsetBytes = drawArgsBaseOffsetBytes;
		MaterialGenerationBuffer = materialGenerationBuffer;
		IndexStream = indexStream;
	}

	public IGfxBuffer? InstanceBuffer { get; }
	public IGfxBuffer? MaterialBuffer { get; }
	public IGfxBuffer? DrawArgsBuffer { get; }
	public ulong DrawArgsBaseOffsetBytes { get; }
	public IGfxBuffer? MaterialGenerationBuffer { get; }
	public MeshIndexStream IndexStream { get; }

	internal SharedDrawIndirectEncodeResources ForLane(in GpuDrawExecutionLaneDefinition lane) => new(
		InstanceBuffer, MaterialBuffer, DrawArgsBuffer, DrawArgsBaseOffsetBytes, MaterialGenerationBuffer,
		IndexStream == MeshIndexStream.Main || lane.DrawKind != GpuDrawKind.Mesh ? MeshIndexStream.Main
			: lane.BucketId == GpuDrawBucketId.AlphaTest ? MeshIndexStream.ShadowAlphaTest : MeshIndexStream.ShadowOpaque);

	public static SharedDrawIndirectEncodeResources FromGpuDrawResources(
		GpuDrawResources resources,
		IGfxBuffer? drawArgsBuffer = null,
		ulong drawArgsBaseOffsetBytes = 0,
		MeshIndexStream indexStream = MeshIndexStream.Main)
	{
		ArgumentNullException.ThrowIfNull(resources);
		return new SharedDrawIndirectEncodeResources(
			resources.InstanceBuffer,
			resources.MaterialBuffer,
			drawArgsBuffer ?? resources.DrawArgsBuffer,
			drawArgsBaseOffsetBytes,
			resources.MaterialGenerationBuffer,
			indexStream);
	}
}
