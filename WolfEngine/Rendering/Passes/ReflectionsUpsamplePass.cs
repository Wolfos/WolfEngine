using System.Numerics;
using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering.Shaders;

namespace WolfEngine.Rendering.Passes;

/// <summary>
/// Reconstructs reduced-resolution hardware reflection samples at full resolution. Depth and
/// normal weights prevent radiance from crossing silhouettes or unrelated surfaces.
/// </summary>
public sealed class ReflectionsUpsamplePass
{
	private readonly IShaderProvider _shaderProvider;
	private readonly BindlessResourceRegistry _bindlessRegistry;
	private IGfxPipeline? _pipeline;
	private ReadOnlyMemory<byte> _computeShader;
	private ComputeThreadGroupSize? _threadGroupSize;
	private GraphicsBackendKind? _compiledBackendKind;
	private ShaderPropertyWriter? _bindlessWriter;
	private ShaderPropertyWriter? _settingsWriter;

	public ReflectionsUpsamplePass(IShaderProvider shaderProvider, BindlessResourceRegistry bindlessRegistry)
	{
		_shaderProvider = shaderProvider ?? throw new ArgumentNullException(nameof(shaderProvider));
		_bindlessRegistry = bindlessRegistry ?? throw new ArgumentNullException(nameof(bindlessRegistry));
	}

	public ReflectionsUpsamplePassConfig BuildConfig(
		RenderGraphContext context,
		RenderGraphFrameResources resources,
		IGfxDevice device)
	{
		ArgumentNullException.ThrowIfNull(context);
		ArgumentNullException.ThrowIfNull(device);

		var pipeline = EnsurePipeline(device);
		_bindlessRegistry.EnsureInitialized(device);
		var source = context.GetTexture(resources.ReflectionsTrace);
		var output = context.GetTexture(resources.ReflectionsRadiance);
		return new ReflectionsUpsamplePassConfig
		{
			Pipeline = pipeline,
			DepthHandle = _bindlessRegistry.RegisterDepthTexture(context.GetTexture(resources.GBufferDepth)),
			NormalHandle = _bindlessRegistry.GetTextureHandle(context.GetTexture(resources.GBufferNormal)),
			SourceHandle = _bindlessRegistry.GetTextureHandle(source),
			OutputHandle = _bindlessRegistry.RegisterRwTexture(output),
			FullResolution = resources.SceneFramebufferSize,
			TraceResolution = new(source.Descriptor.Width, source.Descriptor.Height),
			Sharpness = Math.Max(resources.Config.Reflections.RayTracedSettings.UpsampleSharpness, 0.001f)
		};
	}

	public void Record(
		RenderGraphContext context,
		in ReflectionsUpsamplePassConfig config,
		SceneDrawData sceneData)
	{
		ArgumentNullException.ThrowIfNull(context);
		ArgumentNullException.ThrowIfNull(sceneData);

		var commandList = context.CommandList;
		commandList.BindPipeline(config.Pipeline);
		var bindlessWriter = _bindlessWriter
			?? throw new InvalidOperationException("Reflections upsample bindless writer was not initialized.");
		bindlessWriter.Clear();
		bindlessWriter.SetUInt("depthHandle", config.DepthHandle.Value);
		bindlessWriter.SetUInt("normalHandle", config.NormalHandle.Value);
		bindlessWriter.SetUInt("sourceHandle", config.SourceHandle.Value);
		bindlessWriter.SetUInt("outputHandle", config.OutputHandle.Value);
		commandList.SetComputeConstants(bindlessWriter.RegisterIndex, bindlessWriter.AsBytes());

		if (Matrix4x4.Invert(sceneData.InverseProjection, out var projectionMatrix) == false)
		{
			throw new InvalidOperationException("Reflections upsample projection parameters could not be reconstructed.");
		}

		var settingsWriter = _settingsWriter
			?? throw new InvalidOperationException("Reflections upsample settings writer was not initialized.");
		settingsWriter.Clear();
		settingsWriter.SetUInt("fullResolutionX", (uint)Math.Max(config.FullResolution.X, 1));
		settingsWriter.SetUInt("fullResolutionY", (uint)Math.Max(config.FullResolution.Y, 1));
		settingsWriter.SetUInt("traceResolutionX", (uint)Math.Max(config.TraceResolution.X, 1));
		settingsWriter.SetUInt("traceResolutionY", (uint)Math.Max(config.TraceResolution.Y, 1));
		settingsWriter.SetFloat("sharpness", config.Sharpness);
		settingsWriter.SetFloat("projZBias", projectionMatrix.M33);
		settingsWriter.SetFloat("projZScale", projectionMatrix.M43);
		commandList.SetComputeConstants(settingsWriter.RegisterIndex, settingsWriter.AsBytes());

		var threadGroupSize = _threadGroupSize
			?? throw new InvalidOperationException("Reflections upsample threadgroup size was not initialized.");
		var (dispatchX, dispatchY, dispatchZ) = threadGroupSize.GetDispatchGroupCount(
			(uint)Math.Max(config.FullResolution.X, 1),
			(uint)Math.Max(config.FullResolution.Y, 1));
		commandList.Dispatch(dispatchX, dispatchY, dispatchZ);
	}

	private IGfxPipeline EnsurePipeline(IGfxDevice device)
	{
		if (_pipeline is not null)
		{
			if (_compiledBackendKind.HasValue && _compiledBackendKind.Value != device.BackendKind)
			{
				throw new InvalidOperationException(
					$"ReflectionsUpsamplePass is already compiled for backend '{_compiledBackendKind.Value}', " +
					$"but was requested for '{device.BackendKind}'.");
			}
			return _pipeline;
		}

		var compiled = _shaderProvider.GetComputeShaderWithReflection(
			EngineShaderPrograms.ReflectionsUpsample,
			"ReflectionsUpsampleCS",
			device.BackendKind);
		_computeShader = compiled.Bytecode;
		_threadGroupSize = compiled.ThreadGroupSize;
		_bindlessWriter = new ShaderPropertyWriter(compiled.ReflectionLayout.GetConstantBuffer("BindlessHandles"));
		_settingsWriter = new ShaderPropertyWriter(compiled.ReflectionLayout.GetConstantBuffer("UpsampleSettings"));
		_compiledBackendKind = device.BackendKind;

		var key = new PipelineKey(
			PassKind.Compute,
			null,
			null,
			"ReflectionsUpsampleCS",
			default,
			default,
			default,
			shaderVariant: "reflections_upsample.compute.slang");
		_pipeline = device.GetOrCreatePipeline(
			key,
			new ShaderBytecodeSet(compute: _computeShader, computeThreadGroupSize: _threadGroupSize));
		return _pipeline;
	}
}
