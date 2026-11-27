using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering.Shaders;

/// <summary>
/// The fixed-function state one graphics pipeline of a <see cref="GraphicsShader"/> is created with.
/// </summary>
/// <param name="Variant">
/// Names the pipeline's GPU profiler scope. Pipelines of shaders with the same entry points are told apart by it, so
/// two shaders that share entry points must not share a variant. Without one, the shader's program name and defines
/// are used.
/// </param>
internal readonly record struct GraphicsPipelineState(
	RenderTargetFormats RenderTargets,
	DepthStencilFormat DepthStencil,
	RenderStateDescriptor RenderState,
	GraphicsLayoutKind Layout = GraphicsLayoutKind.Default,
	string? Variant = null);

/// <summary>
/// One vertex and pixel entry-point pair of an engine shader, with the defines it is compiled with. It compiles the
/// shader and reflects it on first use and again after a shader reload, and creates a pipeline for each
/// <see cref="GraphicsPipelineState"/> it is drawn with. A pass keeps only the shader and asks it for pipelines each
/// time it executes.
/// </summary>
/// <remarks>
/// <see cref="GetPipeline"/> or <see cref="EnsureCurrent(IGfxDevice)"/> makes the shader current.
/// <see cref="ReflectionLayout"/>, <see cref="Bytecode"/> and <see cref="Writer"/> throw until it is. Do not keep a
/// pipeline or a writer past the current execute.
/// </remarks>
internal sealed class GraphicsShader
{
	private const string EngineProgramPrefix = "engine://shaders/";

	private readonly ShaderSource _source;
	private readonly ShaderProgramId _program;
	private readonly string _vertexEntryPoint;
	private readonly string _pixelEntryPoint;
	private readonly string[] _defines;
	private readonly string _defaultVariant;
	private readonly Dictionary<PipelineKey, IGfxPipeline> _pipelines = new();
	private readonly Dictionary<string, ShaderPropertyWriter> _writers = new(StringComparer.Ordinal);
	private CompiledGraphicsShaderWithReflection? _compiled;
	private long _builtRevision;

	public GraphicsShader(
		ShaderSource source,
		ShaderProgramId program,
		string vertexEntryPoint,
		string pixelEntryPoint,
		params string[] defines)
	{
		_source = source ?? throw new ArgumentNullException(nameof(source));
		ArgumentException.ThrowIfNullOrWhiteSpace(vertexEntryPoint);
		ArgumentException.ThrowIfNullOrWhiteSpace(pixelEntryPoint);

		_program = program;
		_vertexEntryPoint = vertexEntryPoint;
		_pixelEntryPoint = pixelEntryPoint;
		_defines = ShaderSource.NormalizeDefines(defines);

		var programName = program.Value.StartsWith(EngineProgramPrefix, StringComparison.Ordinal)
			? program.Value[EngineProgramPrefix.Length..]
			: program.Value;
		_defaultVariant = _defines.Length == 0
			? programName
			: $"{programName}:{string.Join(';', _defines)}";
	}

	public ShaderReflectionLayout ReflectionLayout => GetCurrent().ReflectionLayout;

	public ShaderBytecodeSet Bytecode => GetCurrent().Bytecode;

	/// <summary>Compiles and reflects the shader for the current applied shader revision if it is not already.</summary>
	public void EnsureCurrent(IGfxDevice device)
	{
		ArgumentNullException.ThrowIfNull(device);

		var appliedRevision = _source.AppliedRevision;
		if (_compiled is not null && _builtRevision == appliedRevision) return;

		_compiled = _source.GetGraphicsShader(
			_program,
			_vertexEntryPoint,
			_pixelEntryPoint,
			device.BackendKind,
			_defines);
		_pipelines.Clear();
		_writers.Clear();
		_builtRevision = appliedRevision;
	}

	/// <summary>Returns the pipeline for <paramref name="state"/>, compiling the shader and creating it first if needed.</summary>
	public IGfxPipeline GetPipeline(IGfxDevice device, in GraphicsPipelineState state)
	{
		EnsureCurrent(device);

		var key = new PipelineKey(
			PassKind.Graphics,
			vertexEntryPoint: _vertexEntryPoint,
			pixelEntryPoint: _pixelEntryPoint,
			computeEntryPoint: null,
			renderTargets: state.RenderTargets,
			depthStencil: state.DepthStencil,
			renderState: state.RenderState,
			layout: state.Layout,
			shaderVariant: state.Variant ?? _defaultVariant);
		if (_pipelines.TryGetValue(key, out var pipeline))
		{
			return pipeline;
		}

		pipeline = device.GetOrCreatePipeline(key, _compiled!.Value.Bytecode);
		_pipelines[key] = pipeline;

		return pipeline;
	}

	/// <summary>
	/// The writer for one of the shader's constant buffers, reflected from the current shader. The shader must be
	/// current: call <see cref="GetPipeline"/> or <see cref="EnsureCurrent(IGfxDevice)"/> first in the same execute.
	/// </summary>
	public ShaderPropertyWriter Writer(string constantBuffer)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(constantBuffer);
		var compiled = GetCurrent();

		if (_writers.TryGetValue(constantBuffer, out var writer))
		{
			return writer;
		}

		writer = new ShaderPropertyWriter(compiled.ReflectionLayout.GetConstantBuffer(constantBuffer));
		_writers[constantBuffer] = writer;

		return writer;
	}

	private CompiledGraphicsShaderWithReflection GetCurrent()
	{
		if (_compiled is { } compiled && _builtRevision == _source.AppliedRevision)
		{
			return compiled;
		}

		throw new InvalidOperationException(
			$"Graphics shader '{_defaultVariant}' is not current. Call GetPipeline or EnsureCurrent before reading its reflection, bytecode or writers.");
	}
}
