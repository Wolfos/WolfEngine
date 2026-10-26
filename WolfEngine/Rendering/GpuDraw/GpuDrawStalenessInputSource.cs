namespace WolfEngine.Rendering;

/// <summary>Reads the staleness inputs from the shared draw buffers, the renderer and the device's bindless table.</summary>
internal sealed class GpuDrawStalenessInputSource : IDrawRecordStalenessInputSource
{
	private readonly GpuDrawResources _gpuDrawResources;
	private readonly IRenderer _renderer;

	public GpuDrawStalenessInputSource(GpuDrawResources gpuDrawResources, IRenderer renderer)
	{
		_gpuDrawResources = gpuDrawResources ?? throw new ArgumentNullException(nameof(gpuDrawResources));
		_renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
	}

	public DrawRecordStalenessInputs Read() =>
		new(
			_gpuDrawResources.IndirectBindingVersion,
			_renderer.PackedGeometryGeneration,
			_renderer.GetGfxDevice().GlobalTable.Generation);
}
