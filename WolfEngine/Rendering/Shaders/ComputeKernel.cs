using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering.Passes;

namespace WolfEngine.Rendering.Shaders;

/// <summary>
/// One compute entry point of an engine shader, with the defines it is compiled with. It compiles the shader,
/// creates the pipeline and reflects its constant buffers on first use, and builds them again after a shader
/// reload, so a pass keeps only the kernel and asks it for the rest each time it executes.
/// </summary>
/// <remarks>
/// <see cref="GetPipeline"/> or <see cref="Bind"/> makes the kernel current. <see cref="ThreadGroupSize"/>,
/// <see cref="ReflectionLayout"/>, <see cref="Writer"/> and <see cref="Dispatch(IGfxCommandList, int, int, int)"/>
/// throw until it is, so nothing can read state left over from before a reload. Do not keep the pipeline or a
/// writer past the current execute.
/// </remarks>
internal sealed class ComputeKernel
{
	private readonly ShaderSource _source;
	private readonly ShaderProgramId _program;
	private readonly string _entryPoint;
	private readonly string[] _defines;
	private readonly string? _shaderVariant;
	private readonly Dictionary<string, ShaderPropertyWriter> _writers = new(StringComparer.Ordinal);
	private IGfxPipeline? _pipeline;
	private CompiledComputeShaderWithReflection _compiled;
	private long _builtRevision;

	public ComputeKernel(ShaderSource source, ShaderProgramId program, string entryPoint, params string[] defines)
	{
		_source = source ?? throw new ArgumentNullException(nameof(source));
		ArgumentException.ThrowIfNullOrWhiteSpace(entryPoint);

		_program = program;
		_entryPoint = entryPoint;
		_defines = ShaderSource.NormalizeDefines(defines);
		_shaderVariant = _defines.Length == 0
			? null
			: $"{entryPoint}:{string.Join(';', _defines)}";
	}

	public string EntryPoint => _entryPoint;

	/// <summary>
	/// The name the pipeline's GPU profiler scope carries: the entry point, followed by the defines when there are any.
	/// </summary>
	public string ProfileName => _shaderVariant ?? _entryPoint;

	/// <summary>
	/// The pipeline built for the current shaders. The kernel must be current: call <see cref="GetPipeline"/> or
	/// <see cref="Bind"/> first in the same execute.
	/// </summary>
	public IGfxPipeline Pipeline
	{
		get
		{
			EnsureCurrent();
			return _pipeline!;
		}
	}

	public ComputeThreadGroupSize ThreadGroupSize
	{
		get
		{
			EnsureCurrent();
			return _compiled.ThreadGroupSize;
		}
	}

	public ShaderReflectionLayout ReflectionLayout
	{
		get
		{
			EnsureCurrent();
			return _compiled.ReflectionLayout;
		}
	}

	/// <summary>Returns the kernel's pipeline, compiling the shader and creating the pipeline first if needed.</summary>
	public IGfxPipeline GetPipeline(IGfxDevice device)
	{
		ArgumentNullException.ThrowIfNull(device);

		var appliedRevision = _source.AppliedRevision;
		if (_pipeline is not null && _builtRevision == appliedRevision)
		{
			return _pipeline;
		}

		_compiled = _source.GetComputeShader(_program, _entryPoint, device.BackendKind, _defines);
		_writers.Clear();
		var shaders = new ShaderBytecodeSet(
			compute: _compiled.Bytecode,
			computeThreadGroupSize: _compiled.ThreadGroupSize);
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
		_builtRevision = appliedRevision;

		return _pipeline;
	}

	/// <summary>Binds the kernel's pipeline, creating it first if needed.</summary>
	public void Bind(IGfxCommandList commandList, IGfxDevice device)
	{
		ArgumentNullException.ThrowIfNull(commandList);

		commandList.BindPipeline(GetPipeline(device));
	}

	/// <summary>
	/// The writer for one of the kernel's constant buffers, reflected from the current shader. The kernel must be
	/// current: call <see cref="GetPipeline"/> or <see cref="Bind"/> first in the same execute.
	/// </summary>
	public ShaderPropertyWriter Writer(string constantBuffer)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(constantBuffer);
		EnsureCurrent();

		if (_writers.TryGetValue(constantBuffer, out var writer))
		{
			return writer;
		}

		writer = new ShaderPropertyWriter(_compiled.ReflectionLayout.GetConstantBuffer(constantBuffer));
		_writers[constantBuffer] = writer;

		return writer;
	}

	/// <summary>Dispatches enough groups to cover <paramref name="width"/> x <paramref name="height"/> x <paramref name="depth"/> threads.</summary>
	public void Dispatch(IGfxCommandList commandList, int width, int height, int depth)
	{
		ArgumentNullException.ThrowIfNull(commandList);

		var (x, y, z) = ThreadGroupSize.GetDispatchGroupCount(
			(uint)Math.Max(width, 1),
			(uint)Math.Max(height, 1),
			(uint)Math.Max(depth, 1));
		commandList.Dispatch(x, y, z);
	}

	/// <summary>Dispatches enough groups to cover <paramref name="width"/> x <paramref name="height"/> threads.</summary>
	public void Dispatch(IGfxCommandList commandList, int width, int height)
	{
		Dispatch(commandList, width, height, 1);
	}

	private void EnsureCurrent()
	{
		if (_pipeline is not null && _builtRevision == _source.AppliedRevision) return;

		throw new InvalidOperationException(
			$"Compute kernel '{ProfileName}' is not current. Call GetPipeline or Bind before reading its reflection, writers or thread group size.");
	}
}
