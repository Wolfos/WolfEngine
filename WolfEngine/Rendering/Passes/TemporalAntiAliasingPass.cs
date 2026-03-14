using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering.Passes;

public sealed class TemporalAntiAliasingPass
{
	private readonly IShaderCompiler _shaderCompiler;
	private readonly BindlessResourceRegistry _bindlessRegistry;
	private IGfxPipeline? _pipeline;
	private ReadOnlyMemory<byte> _computeShader;
	private GraphicsBackendKind? _compiledBackendKind;
	private ShaderPropertyWriter? _bindlessWriter;
	private ShaderPropertyWriter? _settingsWriter;
	private DescriptorHandle _linearSampler = DescriptorHandle.Invalid;

	public TemporalAntiAliasingPass(IShaderCompiler shaderCompiler, BindlessResourceRegistry bindlessRegistry)
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
		var currentDepth = context.GetTexture(resources.GBufferDepth);
		var historyColor = context.GetTexture(resources.HistoryColorRead);
		var historyDepth = context.GetTexture(resources.HistoryDepthRead);
		var output = context.GetTexture(resources.ResolvedSceneColor);

		return new TemporalAntiAliasingPassConfig
		{
			Pipeline = pipeline,
			CurrentColorHandle = _bindlessRegistry.GetTextureHandle(currentColor),
			VelocityHandle = _bindlessRegistry.GetTextureHandle(velocity),
			CurrentDepthHandle = _bindlessRegistry.RegisterDepthTexture(currentDepth),
			HistoryColorHandle = _bindlessRegistry.GetTextureHandle(historyColor),
			HistoryDepthHandle = _bindlessRegistry.GetTextureHandle(historyDepth),
			OutputHandle = _bindlessRegistry.RegisterRwTexture(output),
			LinearSampler = _linearSampler,
			RenderSize = resources.SceneFramebufferSize,
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
		bindlessWriter.SetUInt("currentDepthHandle", config.CurrentDepthHandle.Value);
		bindlessWriter.SetUInt("historyColorHandle", config.HistoryColorHandle.Value);
		bindlessWriter.SetUInt("historyDepthHandle", config.HistoryDepthHandle.Value);
		bindlessWriter.SetUInt("outputHandle", config.OutputHandle.Value);
		bindlessWriter.SetUInt("samplerHandle", config.LinearSampler.Value);
		commandList.SetComputeConstants(bindlessWriter.RegisterIndex, bindlessWriter.AsBytes());

		var settingsWriter = _settingsWriter
			?? throw new InvalidOperationException("TAA settings writer was not initialized.");
		settingsWriter.Clear();
		settingsWriter.SetUInt("renderSizeX", (uint)Math.Max(config.RenderSize.X, 1));
		settingsWriter.SetUInt("renderSizeY", (uint)Math.Max(config.RenderSize.Y, 1));
		settingsWriter.SetUInt("historyValid", config.HistoryValid ? 1u : 0u);
		settingsWriter.SetUInt("resetHistory", config.ResetHistory ? 1u : 0u);
		commandList.SetComputeConstants(settingsWriter.RegisterIndex, settingsWriter.AsBytes());

		var dispatchX = (uint)((config.RenderSize.X + 7) / 8);
		var dispatchY = (uint)((config.RenderSize.Y + 7) / 8);
		commandList.Dispatch(dispatchX, dispatchY, 1);
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
			computeEntryPoint: "CSMain",
			renderTargets: new RenderTargetFormats(Array.Empty<TextureFormat>()),
			depthStencil: new DepthStencilFormat(TextureFormat.Unknown),
			renderState: default,
			shaderVariant: "taa_resolve.compute.slang");
		_pipeline = device.GetOrCreatePipeline(pipelineKey, new ShaderBytecodeSet(compute: _computeShader));
		return _pipeline;
	}

	private void EnsureReflectionWriters(GraphicsBackendKind backendKind)
	{
		if (_compiledBackendKind.HasValue &&
		    _compiledBackendKind.Value == backendKind &&
		    _bindlessWriter is not null &&
		    _settingsWriter is not null &&
		    _computeShader.IsEmpty == false)
		{
			return;
		}

		var compiled = _shaderCompiler.GetComputeShaderWithReflection(
			"taa_resolve.compute.slang",
			"CSMain",
			backendKind);
		_computeShader = compiled.Bytecode;
		var reflection = compiled.ReflectionLayout;
		_bindlessWriter = new ShaderPropertyWriter(reflection.GetConstantBuffer("BindlessHandles"));
		_settingsWriter = new ShaderPropertyWriter(reflection.GetConstantBuffer("TaaSettings"));
		_compiledBackendKind = backendKind;
	}
}
