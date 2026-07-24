using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering.Shaders;

namespace WolfEngine.Rendering.Passes;

public sealed class TemporalAntiAliasingPass
{
	private readonly IShaderProvider _shaderCompiler;
	private readonly BindlessResourceRegistry _bindlessRegistry;
	private IGfxPipeline? _pipeline;
	private ReadOnlyMemory<byte> _computeShader;
	private ComputeThreadGroupSize? _threadGroupSize;
	private GraphicsBackendKind? _compiledBackendKind;
	private ShaderPropertyWriter? _bindlessWriter;
	private ShaderPropertyWriter? _settingsWriter;
	private DescriptorHandle _linearSampler = DescriptorHandle.Invalid;

	public TemporalAntiAliasingPass(IShaderProvider shaderCompiler, BindlessResourceRegistry bindlessRegistry)
	{
		_shaderCompiler = shaderCompiler ?? throw new ArgumentNullException(nameof(shaderCompiler));
		_bindlessRegistry = bindlessRegistry ?? throw new ArgumentNullException(nameof(bindlessRegistry));
	}

	public TemporalAntiAliasingPassConfig BuildConfig(
		RenderGraphContext context,
		RenderGraphFrameResources resources,
		IGfxDevice device,
		bool historyValid,
		bool resetHistory)
	{
		ArgumentNullException.ThrowIfNull(context);
		ArgumentNullException.ThrowIfNull(device);

		var pipeline = EnsurePipeline(device);
		_bindlessRegistry.EnsureInitialized(device);
		if (_linearSampler.IsValid == false)
		{
			_linearSampler = _bindlessRegistry.GetSamplerHandle(new SamplerDescriptor(
				FilterMode.Bilinear,
				AddressMode.Clamp,
				AddressMode.Clamp,
				AddressMode.Clamp));
		}

		var currentColor = context.GetTexture(resources.LightingBuffer);
		var velocity = context.GetTexture(resources.GBufferVelocity);
		var material = context.GetTexture(resources.GBufferMaterial);
		var currentDepth = context.GetTexture(resources.GBufferDepth);
		var historyColor = context.GetTexture(resources.HistoryColorRead);
		var historyDepth = context.GetTexture(resources.HistoryDepthRead);
		var output = context.GetTexture(resources.ResolvedSceneColor);

		return new TemporalAntiAliasingPassConfig
		{
			Pipeline = pipeline,
			CurrentColorHandle = _bindlessRegistry.GetTextureHandle(currentColor),
			VelocityHandle = _bindlessRegistry.GetTextureHandle(velocity),
			MaterialHandle = _bindlessRegistry.GetTextureHandle(material),
			CurrentDepthHandle = _bindlessRegistry.RegisterDepthTexture(currentDepth),
			HistoryColorHandle = _bindlessRegistry.GetTextureHandle(historyColor),
			HistoryDepthHandle = _bindlessRegistry.GetTextureHandle(historyDepth),
			OutputHandle = _bindlessRegistry.RegisterRwTexture(output),
			LinearSampler = _linearSampler,
			RenderSize = resources.SceneFramebufferSize,
			Settings = resources.Config.TemporalAntiAliasing,
			HistoryValid = historyValid,
			ResetHistory = resetHistory
		};
	}

	public void Record(RenderGraphContext context, in TemporalAntiAliasingPassConfig config)
	{
		ArgumentNullException.ThrowIfNull(context);

		var commandList = context.CommandList;
		commandList.BindPipeline(config.Pipeline);

		var bindlessWriter = _bindlessWriter
			?? throw new InvalidOperationException("TAA bindless writer was not initialized.");
		bindlessWriter.Clear();
		bindlessWriter.SetUInt("currentColorHandle", config.CurrentColorHandle.Value);
		bindlessWriter.SetUInt("velocityHandle", config.VelocityHandle.Value);
		bindlessWriter.SetUInt("materialHandle", config.MaterialHandle.Value);
		bindlessWriter.SetUInt("currentDepthHandle", config.CurrentDepthHandle.Value);
		bindlessWriter.SetUInt("historyColorHandle", config.HistoryColorHandle.Value);
		bindlessWriter.SetUInt("historyDepthHandle", config.HistoryDepthHandle.Value);
		bindlessWriter.SetUInt("outputHandle", config.OutputHandle.Value);
		bindlessWriter.SetUInt("samplerHandle", config.LinearSampler.Value);
		commandList.SetComputeConstants(bindlessWriter.RegisterIndex, bindlessWriter.AsBytes());

		var settingsWriter = _settingsWriter
			?? throw new InvalidOperationException("TAA settings writer was not initialized.");
		var settings = config.Settings;
		settingsWriter.Clear();
		settingsWriter.SetUInt("renderSizeX", (uint)Math.Max(config.RenderSize.X, 1));
		settingsWriter.SetUInt("renderSizeY", (uint)Math.Max(config.RenderSize.Y, 1));
		settingsWriter.SetUInt("historyValid", config.HistoryValid ? 1u : 0u);
		settingsWriter.SetUInt("resetHistory", config.ResetHistory ? 1u : 0u);
		settingsWriter.SetFloat("opaqueDepthThreshold", MathF.Max(settings.OpaqueDepthThreshold, 0.0f));
		settingsWriter.SetFloat("alphaTestDepthThreshold", MathF.Max(settings.AlphaTestDepthThreshold, 0.0f));
		settingsWriter.SetFloat("opaqueClampSigma", MathF.Max(settings.OpaqueClampSigma, 0.0f));
		settingsWriter.SetFloat("alphaTestClampSigma", MathF.Max(settings.AlphaTestClampSigma, 0.0f));
		settingsWriter.SetFloat("lowMotionClampExpansion", MathF.Max(settings.LowMotionClampExpansion, 0.0f));
		settingsWriter.SetFloat("highMotionClampExpansion", MathF.Max(settings.HighMotionClampExpansion, 0.0f));
		settingsWriter.SetFloat("clampExpansionMotionScale", MathF.Max(settings.ClampExpansionMotionScale, 0.0f));
		settingsWriter.SetFloat("lowMotionOpaqueHistoryWeight", Math.Clamp(settings.LowMotionOpaqueHistoryWeight, 0.0f, 0.9999f));
		settingsWriter.SetFloat("highMotionOpaqueHistoryWeight", Math.Clamp(settings.HighMotionOpaqueHistoryWeight, 0.0f, 0.9999f));
		settingsWriter.SetFloat("opaqueHistoryMotionScale", MathF.Max(settings.OpaqueHistoryMotionScale, 0.0f));
		settingsWriter.SetFloat("lowMotionAlphaTestHistoryWeight", Math.Clamp(settings.LowMotionAlphaTestHistoryWeight, 0.0f, 0.9999f));
		settingsWriter.SetFloat("highMotionAlphaTestHistoryWeight", Math.Clamp(settings.HighMotionAlphaTestHistoryWeight, 0.0f, 0.9999f));
		settingsWriter.SetFloat("alphaTestHistoryMotionScale", MathF.Max(settings.AlphaTestHistoryMotionScale, 0.0f));
		commandList.SetComputeConstants(settingsWriter.RegisterIndex, settingsWriter.AsBytes());

		var threadGroupSize = _threadGroupSize
			?? throw new InvalidOperationException("TAA threadgroup size was not initialized.");
		var (dispatchX, dispatchY, dispatchZ) = threadGroupSize.GetDispatchGroupCount(
			(uint)Math.Max(config.RenderSize.X, 1),
			(uint)Math.Max(config.RenderSize.Y, 1));
		commandList.Dispatch(dispatchX, dispatchY, dispatchZ);
	}

	private IGfxPipeline EnsurePipeline(IGfxDevice device)
	{
		if (_pipeline is not null)
		{
			if (_compiledBackendKind.HasValue && _compiledBackendKind.Value != device.BackendKind)
			{
				throw new InvalidOperationException(
					$"TemporalAntiAliasingPass is already compiled for backend '{_compiledBackendKind.Value}', but was requested for '{device.BackendKind}'.");
			}

			return _pipeline;
		}

		EnsureReflectionWriters(device.BackendKind);
		var pipelineKey = new PipelineKey(
			PassKind.Compute,
			vertexEntryPoint: null,
			pixelEntryPoint: null,
			computeEntryPoint: "TaaResolveCS",
			renderTargets: new RenderTargetFormats(Array.Empty<TextureFormat>()),
			depthStencil: new DepthStencilFormat(TextureFormat.Unknown),
			renderState: default,
			shaderVariant: "taa_resolve.compute.slang");
		_pipeline = device.GetOrCreatePipeline(
			pipelineKey,
			new ShaderBytecodeSet(compute: _computeShader, computeThreadGroupSize: _threadGroupSize));
		return _pipeline;
	}

	private void EnsureReflectionWriters(GraphicsBackendKind backendKind)
	{
		if (_compiledBackendKind.HasValue &&
		    _compiledBackendKind.Value == backendKind &&
		    _bindlessWriter is not null &&
		    _settingsWriter is not null &&
		    _computeShader.IsEmpty == false &&
		    _threadGroupSize.HasValue)
		{
			return;
		}

		var compiled = _shaderCompiler.GetComputeShaderWithReflection(
			EngineShaderPrograms.TaaResolve,
			"TaaResolveCS",
			backendKind);
		_computeShader = compiled.Bytecode;
		_threadGroupSize = compiled.ThreadGroupSize;
		var reflection = compiled.ReflectionLayout;
		_bindlessWriter = new ShaderPropertyWriter(reflection.GetConstantBuffer("BindlessHandles"));
		_settingsWriter = new ShaderPropertyWriter(reflection.GetConstantBuffer("TaaSettings"));
		_compiledBackendKind = backendKind;
	}
}
