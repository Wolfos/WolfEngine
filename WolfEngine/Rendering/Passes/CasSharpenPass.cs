using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering.Shaders;

namespace WolfEngine.Rendering.Passes;

public sealed class CasSharpenPass
{
	private readonly IShaderProvider _shaderCompiler;
	private readonly BindlessResourceRegistry _bindlessRegistry;
	private IGfxPipeline? _pipeline;
	private ReadOnlyMemory<byte> _computeShader;
	private ComputeThreadGroupSize? _threadGroupSize;
	private GraphicsBackendKind? _compiledBackendKind;
	private ShaderPropertyWriter? _bindlessWriter;
	private ShaderPropertyWriter? _settingsWriter;

	public CasSharpenPass(IShaderProvider shaderCompiler, BindlessResourceRegistry bindlessRegistry)
	{
		_shaderCompiler = shaderCompiler ?? throw new ArgumentNullException(nameof(shaderCompiler));
		_bindlessRegistry = bindlessRegistry ?? throw new ArgumentNullException(nameof(bindlessRegistry));
	}

	public CasSharpenPassConfig BuildConfig(
		RenderGraphContext context,
		RenderGraphFrameResources resources,
		IGfxDevice device)
	{
		ArgumentNullException.ThrowIfNull(context);
		ArgumentNullException.ThrowIfNull(device);

		var pipeline = EnsurePipeline(device);
		_bindlessRegistry.EnsureInitialized(device);

		var input = context.GetTexture(resources.TonemappedLinearSceneColor);
		var output = context.GetTexture(resources.DisplayLinearSceneColor);
		return new CasSharpenPassConfig
		{
			Pipeline = pipeline,
			InputHandle = _bindlessRegistry.GetTextureHandle(input),
			OutputHandle = _bindlessRegistry.RegisterRwTexture(output),
			RenderSize = resources.FramebufferSize,
			SharpenEnabled = resources.Config.AntiAliasing.UsesCasSharpening,
			Sharpness = Math.Clamp(resources.Config.AntiAliasing.Taa.CasSharpness, 0.0f, 1.0f)
		};
	}

	public void Record(RenderGraphContext context, in CasSharpenPassConfig config)
	{
		ArgumentNullException.ThrowIfNull(context);

		var commandList = context.CommandList;
		commandList.BindPipeline(config.Pipeline);

		var bindlessWriter = _bindlessWriter
			?? throw new InvalidOperationException("CAS sharpen bindless writer was not initialized.");
		bindlessWriter.Clear();
		bindlessWriter.SetUInt("inputHandle", config.InputHandle.Value);
		bindlessWriter.SetUInt("outputHandle", config.OutputHandle.Value);
		commandList.SetComputeConstants(bindlessWriter.RegisterIndex, bindlessWriter.AsBytes());

		var settingsWriter = _settingsWriter
			?? throw new InvalidOperationException("CAS sharpen settings writer was not initialized.");
		settingsWriter.Clear();
		settingsWriter.SetUInt("renderSizeX", (uint)Math.Max(config.RenderSize.X, 1));
		settingsWriter.SetUInt("renderSizeY", (uint)Math.Max(config.RenderSize.Y, 1));
		settingsWriter.SetUInt("sharpenEnabled", config.SharpenEnabled ? 1u : 0u);
		settingsWriter.SetUInt("casConst0X", BitConverter.SingleToUInt32Bits(1.0f));
		settingsWriter.SetUInt("casConst0Y", BitConverter.SingleToUInt32Bits(1.0f));
		settingsWriter.SetUInt("casConst0Z", BitConverter.SingleToUInt32Bits(0.0f));
		settingsWriter.SetUInt("casConst0W", BitConverter.SingleToUInt32Bits(0.0f));

		var peak = -1.0f / Lerp(8.0f, 5.0f, config.Sharpness);
		settingsWriter.SetUInt("casConst1X", BitConverter.SingleToUInt32Bits(peak));
		settingsWriter.SetUInt("casConst1Y", BitConverter.SingleToUInt32Bits(peak));
		settingsWriter.SetUInt("casConst1Z", BitConverter.SingleToUInt32Bits(8.0f));
		settingsWriter.SetUInt("casConst1W", 0u);
		commandList.SetComputeConstants(settingsWriter.RegisterIndex, settingsWriter.AsBytes());

		var threadGroupSize = _threadGroupSize
			?? throw new InvalidOperationException("CAS sharpen threadgroup size was not initialized.");
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
					$"CasSharpenPass is already compiled for backend '{_compiledBackendKind.Value}', but was requested for '{device.BackendKind}'.");
			}

			return _pipeline;
		}

		EnsureReflectionWriters(device.BackendKind);
		var pipelineKey = new PipelineKey(
			PassKind.Compute,
			vertexEntryPoint: null,
			pixelEntryPoint: null,
			computeEntryPoint: "CasSharpenCS",
			renderTargets: new RenderTargetFormats(Array.Empty<TextureFormat>()),
			depthStencil: new DepthStencilFormat(TextureFormat.Unknown),
			renderState: default,
			shaderVariant: "cas_sharpen.compute.slang");
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
			EngineShaderPrograms.CasSharpen,
			"CasSharpenCS",
			backendKind);
		_computeShader = compiled.Bytecode;
		_threadGroupSize = compiled.ThreadGroupSize;
		var reflection = compiled.ReflectionLayout;
		_bindlessWriter = new ShaderPropertyWriter(reflection.GetConstantBuffer("BindlessHandles"));
		_settingsWriter = new ShaderPropertyWriter(reflection.GetConstantBuffer("CasSettings"));
		_compiledBackendKind = backendKind;
	}

	private static float Lerp(float start, float end, float amount)
	{
		return start + ((end - start) * amount);
	}
}
