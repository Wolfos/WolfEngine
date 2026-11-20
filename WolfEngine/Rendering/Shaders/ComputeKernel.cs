using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering.Passes;

namespace WolfEngine.Rendering.Shaders;

/// <summary>
/// One engine compute shader entry point: its pipeline, thread group size and reflected constant buffer writers,
/// created on first use.
/// </summary>
internal sealed class ComputeKernel
{
	private readonly ShaderProgramId _program;
	private readonly string _entryPoint;
	private readonly string? _shaderVariant;
	private readonly Dictionary<string, ShaderPropertyWriter> _writers = new(StringComparer.Ordinal);
	private IGfxPipeline? _pipeline;
	private ComputeThreadGroupSize? _threadGroupSize;
	private CompiledComputeShaderWithReflection _compiled;

	/// <param name="program">The shader program the entry point belongs to.</param>
	/// <param name="entryPoint">The compute entry point.</param>
	/// <param name="shaderVariant">
	/// Names the pipeline's GPU profiler scope on D3D12. Without it the scope is named after the entry point.
	/// </param>
	public ComputeKernel(ShaderProgramId program, string entryPoint, string? shaderVariant = null)
	{
		_program = program;
		_entryPoint = entryPoint ?? throw new ArgumentNullException(nameof(entryPoint));
		_shaderVariant = shaderVariant;
	}

	public IGfxPipeline Pipeline => _pipeline ?? throw new InvalidOperationException($"'{_entryPoint}' was not created.");

	public ComputeThreadGroupSize ThreadGroupSize => _threadGroupSize
		?? throw new InvalidOperationException($"'{_entryPoint}' was not created.");

	public void EnsureCreated(IGfxDevice device, IShaderProvider shaderProvider)
	{
		if (_pipeline is not null)
		{
			return;
		}

		_compiled = shaderProvider.GetComputeShaderWithReflection(_program, _entryPoint, device.BackendKind);
		_threadGroupSize = _compiled.ThreadGroupSize;
		var shaders = new ShaderBytecodeSet(compute: _compiled.Bytecode, computeThreadGroupSize: _threadGroupSize);
		var pipelineKey = new PipelineKey(
			PassKind.Compute,
			vertexEntryPoint: null,
			pixelEntryPoint: null,
			computeEntryPoint: _entryPoint,
			renderTargets: new RenderTargetFormats(Array.Empty<TextureFormat>()),
			depthStencil: new DepthStencilFormat(TextureFormat.Unknown),
			renderState: default,
			layout: GraphicsLayoutKind.Default,
			shaderVariant: _shaderVariant);
		_pipeline = device.GetOrCreatePipeline(pipelineKey, shaders);
	}

	public ShaderPropertyWriter Writer(string constantBuffer)
	{
		if (_writers.TryGetValue(constantBuffer, out var writer))
		{
			return writer;
		}

		writer = new ShaderPropertyWriter(_compiled.ReflectionLayout.GetConstantBuffer(constantBuffer));
		_writers[constantBuffer] = writer;

		return writer;
	}

	/// <summary>Binds the pipeline, creating it first if needed.</summary>
	public void Bind(IGfxCommandList commandList, IGfxDevice device, IShaderProvider shaderProvider)
	{
		EnsureCreated(device, shaderProvider);
		commandList.BindPipeline(Pipeline);
	}

	/// <summary>Dispatches enough groups to cover <paramref name="width"/> x <paramref name="height"/> x <paramref name="depth"/> threads.</summary>
	public void Dispatch(IGfxCommandList commandList, int width, int height, int depth)
	{
		var (x, y, z) = ThreadGroupSize.GetDispatchGroupCount((uint)width, (uint)height, (uint)depth);
		commandList.Dispatch(x, y, z);
	}
}
