using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering.Shaders;

namespace WolfEngine.Rendering.Passes;

/// <summary>
/// FSR3 upscaler shading change pass: collapses the shading-change pyramid into a single
/// half-render-resolution mask.
/// </summary>
/// <remarks>
/// Reads the first three pyramid levels and keeps the strongest change that the pixels
/// underneath agreed on the direction of. The result feeds prepare_reactivity, which uses it
/// to decide where the accumulator should trust the current frame over its history.
///
/// Not yet wired into the render graph.
/// </remarks>
public sealed class Fsr3ShadingChangePass
{
	/// <summary>Thread group size, matching upstream's dispatch over half render resolution.</summary>
	private const int ThreadGroupWorkRegionDim = 8;

	/// <summary>
	/// Pyramid levels the shader samples. Fewer than the pyramid has: only the finest three
	/// carry a change signal sharp enough to be useful.
	/// </summary>
	public const int SampledMipCount = 3;

	private readonly IShaderProvider _shaderCompiler;
	private readonly BindlessResourceRegistry _bindlessRegistry;
	private IGfxPipeline? _pipeline;
	private ReadOnlyMemory<byte> _computeShader;
	private ComputeThreadGroupSize? _threadGroupSize;
	private GraphicsBackendKind? _compiledBackendKind;
	private ShaderPropertyWriter? _bindlessWriter;
	private ShaderPropertyWriter? _constantsWriter;
	private DescriptorHandle _pointSampler = DescriptorHandle.Invalid;
	private DescriptorHandle _linearSampler = DescriptorHandle.Invalid;

	public Fsr3ShadingChangePass(IShaderProvider shaderCompiler, BindlessResourceRegistry bindlessRegistry)
	{
		_shaderCompiler = shaderCompiler ?? throw new ArgumentNullException(nameof(shaderCompiler));
		_bindlessRegistry = bindlessRegistry ?? throw new ArgumentNullException(nameof(bindlessRegistry));
	}

	public Fsr3ShadingChangePassConfig BuildConfig(
		RenderGraphContext context,
		IGfxDevice device,
		ReadOnlySpan<RenderGraphResourceHandle> spdMips,
		RenderGraphResourceHandle shadingChange,
		in Fsr3ConstantValues constants)
	{
		ArgumentNullException.ThrowIfNull(context);
		ArgumentNullException.ThrowIfNull(device);
		if (spdMips.Length != Fsr3ShadingChangePyramidPass.SpdMipCount)
		{
			throw new ArgumentException(
				$"Expected {Fsr3ShadingChangePyramidPass.SpdMipCount} pyramid levels, got {spdMips.Length}.",
				nameof(spdMips));
		}

		var pipeline = EnsurePipeline(device);
		_bindlessRegistry.EnsureInitialized(device);
		EnsureSamplers();

		var mipHandles = new DescriptorHandle[Fsr3ShadingChangePyramidPass.SpdMipCount];
		for (var mip = 0; mip < mipHandles.Length; mip++)
		{
			mipHandles[mip] = _bindlessRegistry.GetTextureHandle(context.GetTexture(spdMips[mip]));
		}

		return new Fsr3ShadingChangePassConfig
		{
			Pipeline = pipeline,
			SpdMipReadHandles = mipHandles,
			ShadingChangeHandle = _bindlessRegistry.RegisterRwTexture(context.GetTexture(shadingChange)),
			PointSampler = _pointSampler,
			LinearSampler = _linearSampler,
			Constants = constants
		};
	}

	public void Record(RenderGraphContext context, in Fsr3ShadingChangePassConfig config)
	{
		ArgumentNullException.ThrowIfNull(context);

		var commandList = context.CommandList;
		commandList.BindPipeline(config.Pipeline);

		var bindlessWriter = _bindlessWriter
			?? throw new InvalidOperationException("FSR3 shading change bindless writer was not initialized.");
		bindlessWriter.Clear();
		for (var mip = 0; mip < config.SpdMipReadHandles.Length; mip++)
		{
			bindlessWriter.SetUInt($"spdMipReadHandles[{mip}]", config.SpdMipReadHandles[mip].Value);
		}

		bindlessWriter.SetUInt("shadingChangeHandle", config.ShadingChangeHandle.Value);
		bindlessWriter.SetUInt("pointSamplerHandle", config.PointSampler.Value);
		bindlessWriter.SetUInt("linearSamplerHandle", config.LinearSampler.Value);
		commandList.SetComputeConstants(bindlessWriter.RegisterIndex, bindlessWriter.AsBytes());

		var constantsWriter = _constantsWriter
			?? throw new InvalidOperationException("FSR3 constants writer was not initialized.");
		Fsr3Constants.Write(constantsWriter, config.Constants);
		commandList.SetComputeConstants(constantsWriter.RegisterIndex, constantsWriter.AsBytes());

		// Half render resolution, matching ShadingChangeRenderSize() in the shader.
		var shadingChangeWidth = Math.Max(config.Constants.RenderSize.X / 2, 1);
		var shadingChangeHeight = Math.Max(config.Constants.RenderSize.Y / 2, 1);
		commandList.Dispatch(
			(uint)Math.Max((shadingChangeWidth + ThreadGroupWorkRegionDim - 1) / ThreadGroupWorkRegionDim, 1),
			(uint)Math.Max((shadingChangeHeight + ThreadGroupWorkRegionDim - 1) / ThreadGroupWorkRegionDim, 1),
			1);
	}

	private void EnsureSamplers()
	{
		if (_pointSampler.IsValid == false)
		{
			_pointSampler = _bindlessRegistry.GetSamplerHandle(new SamplerDescriptor(
				FilterMode.Point, AddressMode.Clamp, AddressMode.Clamp, AddressMode.Clamp));
		}

		if (_linearSampler.IsValid == false)
		{
			_linearSampler = _bindlessRegistry.GetSamplerHandle(new SamplerDescriptor(
				FilterMode.Bilinear, AddressMode.Clamp, AddressMode.Clamp, AddressMode.Clamp));
		}
	}

	private IGfxPipeline EnsurePipeline(IGfxDevice device)
	{
		if (_pipeline is not null)
		{
			if (_compiledBackendKind.HasValue && _compiledBackendKind.Value != device.BackendKind)
			{
				throw new InvalidOperationException(
					$"Fsr3ShadingChangePass is already compiled for backend '{_compiledBackendKind.Value}', but was requested for '{device.BackendKind}'.");
			}

			return _pipeline;
		}

		EnsureReflectionWriters(device.BackendKind);
		var pipelineKey = new PipelineKey(
			PassKind.Compute,
			vertexEntryPoint: null,
			pixelEntryPoint: null,
			computeEntryPoint: "Fsr3ShadingChangeCS",
			renderTargets: new RenderTargetFormats(Array.Empty<TextureFormat>()),
			depthStencil: new DepthStencilFormat(TextureFormat.Unknown),
			renderState: default,
			shaderVariant: "fsr3_shading_change.compute.slang");
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
		    _constantsWriter is not null &&
		    _computeShader.IsEmpty == false &&
		    _threadGroupSize.HasValue)
		{
			return;
		}

		var compiled = _shaderCompiler.GetComputeShaderWithReflection(
			EngineShaderPrograms.Fsr3ShadingChange,
			"Fsr3ShadingChangeCS",
			backendKind);
		_computeShader = compiled.Bytecode;
		_threadGroupSize = compiled.ThreadGroupSize;
		var reflection = compiled.ReflectionLayout;
		_bindlessWriter = new ShaderPropertyWriter(reflection.GetConstantBuffer("Fsr3BindlessHandles"));
		_constantsWriter = new ShaderPropertyWriter(reflection.GetConstantBuffer("cbFSR3Upscaler"));
		_compiledBackendKind = backendKind;
	}
}
