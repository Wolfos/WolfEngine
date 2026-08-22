using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering.Passes;

public readonly struct SharedDrawIndirectEncodeResources
{
	public SharedDrawIndirectEncodeResources(
		IGfxBuffer? instanceBuffer,
		IGfxBuffer? materialBuffer,
		IGfxBuffer? drawArgsBuffer,
		ulong drawArgsBaseOffsetBytes,
		IGfxBuffer? materialGenerationBuffer)
	{
		InstanceBuffer = instanceBuffer;
		MaterialBuffer = materialBuffer;
		DrawArgsBuffer = drawArgsBuffer;
		DrawArgsBaseOffsetBytes = drawArgsBaseOffsetBytes;
		MaterialGenerationBuffer = materialGenerationBuffer;
	}

	public IGfxBuffer? InstanceBuffer { get; }
	public IGfxBuffer? MaterialBuffer { get; }
	public IGfxBuffer? DrawArgsBuffer { get; }
	public ulong DrawArgsBaseOffsetBytes { get; }
	public IGfxBuffer? MaterialGenerationBuffer { get; }

	public static SharedDrawIndirectEncodeResources FromGpuDrawResources(
		GpuDrawResources resources,
		IGfxBuffer? drawArgsBuffer = null,
		ulong drawArgsBaseOffsetBytes = 0)
	{
		ArgumentNullException.ThrowIfNull(resources);
		return new SharedDrawIndirectEncodeResources(
			resources.InstanceBuffer,
			resources.MaterialBuffer,
			drawArgsBuffer ?? resources.DrawArgsBuffer,
			drawArgsBaseOffsetBytes,
			resources.MaterialGenerationBuffer);
	}
}
