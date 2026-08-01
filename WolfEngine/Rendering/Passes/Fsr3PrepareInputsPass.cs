using WolfEngine.Mathematics;
using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering.Shaders;

namespace WolfEngine.Rendering.Passes;

/// <summary>
/// FSR3 upscaler prepare-inputs pass: the first pass of the effect.
/// </summary>
/// <remarks>
/// Per render-resolution pixel it finds the nearest and farthest depth of a 3x3 neighbourhood,
/// dilates the motion vector towards the nearest sample, and scatters that depth into the
/// previous frame's location so the next frame can distinguish a disocclusion from motion.
/// It also writes the luma every later pass reads.
///
/// The scatter target is a <see cref="TextureFormat.R32Uint"/> image written with
/// <c>InterlockedMin</c> through the uint view of the bindless UAV heap. It must be reset to
/// <see cref="ReconstructedDepthClearValue"/> before each dispatch, because the atomic reduces
/// towards the nearest surface and would otherwise keep last frame's result.
///
/// Like <see cref="Fsr3RcasPass"/> this is not yet wired into the render graph.
/// </remarks>
public sealed class Fsr3PrepareInputsPass
{
	/// <summary>Pixels covered per thread group on each axis, matching upstream's dispatch.</summary>
	private const int ThreadGroupWorkRegionDim = 8;

	/// <summary>
	/// Reset value for the reconstructed-depth target, as a bit-cast float. Under non-inverted
	/// depth the scatter reduces with <c>InterlockedMin</c>, so it starts at the farthest depth;
	/// reverse-Z would flip this to 0 and the atomic to <c>InterlockedMax</c>.
	/// </summary>
	public static readonly uint ReconstructedDepthClearValue = BitConverter.SingleToUInt32Bits(1.0f);

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

	public Fsr3PrepareInputsPass(IShaderProvider shaderCompiler, BindlessResourceRegistry bindlessRegistry)
	{
		_shaderCompiler = shaderCompiler ?? throw new ArgumentNullException(nameof(shaderCompiler));
		_bindlessRegistry = bindlessRegistry ?? throw new ArgumentNullException(nameof(bindlessRegistry));
	}

	public Fsr3PrepareInputsPassConfig BuildConfig(
		RenderGraphContext context,
		IGfxDevice device,
		RenderGraphResourceHandle inputColor,
		RenderGraphResourceHandle inputDepth,
		RenderGraphResourceHandle inputMotionVectors,
		RenderGraphResourceHandle dilatedMotionVectors,
		RenderGraphResourceHandle dilatedDepth,
		RenderGraphResourceHandle farthestDepth,
		RenderGraphResourceHandle currentLuma,
		RenderGraphResourceHandle reconstructedPrevNearestDepth,
		in Fsr3ConstantValues constants)
	{
		ArgumentNullException.ThrowIfNull(context);
		ArgumentNullException.ThrowIfNull(device);

		var pipeline = EnsurePipeline(device);
		_bindlessRegistry.EnsureInitialized(device);
		EnsureSamplers();

		return new Fsr3PrepareInputsPassConfig
		{
			Pipeline = pipeline,
			InputColorHandle = _bindlessRegistry.GetTextureHandle(context.GetTexture(inputColor)),
			InputDepthHandle = _bindlessRegistry.RegisterDepthTexture(context.GetTexture(inputDepth)),
			InputMotionVectorsHandle = _bindlessRegistry.GetTextureHandle(context.GetTexture(inputMotionVectors)),
			DilatedMotionVectorsHandle = _bindlessRegistry.RegisterRwTexture(context.GetTexture(dilatedMotionVectors)),
			DilatedDepthHandle = _bindlessRegistry.RegisterRwTexture(context.GetTexture(dilatedDepth)),
			FarthestDepthHandle = _bindlessRegistry.RegisterRwTexture(context.GetTexture(farthestDepth)),
			CurrentLumaHandle = _bindlessRegistry.RegisterRwTexture(context.GetTexture(currentLuma)),
			ReconstructedPrevNearestDepthHandle =
				_bindlessRegistry.RegisterRwTexture(context.GetTexture(reconstructedPrevNearestDepth)),
			PointSampler = _pointSampler,
			LinearSampler = _linearSampler,
			Constants = constants
		};
	}

	public void Record(RenderGraphContext context, in Fsr3PrepareInputsPassConfig config)
	{
		ArgumentNullException.ThrowIfNull(context);

		var commandList = context.CommandList;
		commandList.BindPipeline(config.Pipeline);

		var bindlessWriter = _bindlessWriter
			?? throw new InvalidOperationException("FSR3 prepare-inputs bindless writer was not initialized.");
		bindlessWriter.Clear();
		bindlessWriter.SetUInt("inputColorHandle", config.InputColorHandle.Value);
		bindlessWriter.SetUInt("inputDepthHandle", config.InputDepthHandle.Value);
		bindlessWriter.SetUInt("inputMotionVectorsHandle", config.InputMotionVectorsHandle.Value);
		bindlessWriter.SetUInt("dilatedMotionVectorsHandle", config.DilatedMotionVectorsHandle.Value);
		bindlessWriter.SetUInt("dilatedDepthHandle", config.DilatedDepthHandle.Value);
		bindlessWriter.SetUInt("farthestDepthHandle", config.FarthestDepthHandle.Value);
		bindlessWriter.SetUInt("currentLumaHandle", config.CurrentLumaHandle.Value);
		bindlessWriter.SetUInt(
			"reconstructedPrevNearestDepthHandle",
			config.ReconstructedPrevNearestDepthHandle.Value);
		bindlessWriter.SetUInt("pointSamplerHandle", config.PointSampler.Value);
		bindlessWriter.SetUInt("linearSamplerHandle", config.LinearSampler.Value);
		commandList.SetComputeConstants(bindlessWriter.RegisterIndex, bindlessWriter.AsBytes());

		var constantsWriter = _constantsWriter
			?? throw new InvalidOperationException("FSR3 constants writer was not initialized.");
		Fsr3Constants.Write(constantsWriter, config.Constants);
		commandList.SetComputeConstants(constantsWriter.RegisterIndex, constantsWriter.AsBytes());

		var renderSize = config.Constants.RenderSize;
		var dispatchX = (uint)Math.Max(
			(renderSize.X + ThreadGroupWorkRegionDim - 1) / ThreadGroupWorkRegionDim, 1);
		var dispatchY = (uint)Math.Max(
			(renderSize.Y + ThreadGroupWorkRegionDim - 1) / ThreadGroupWorkRegionDim, 1);
		commandList.Dispatch(dispatchX, dispatchY, 1);
	}

	private void EnsureSamplers()
	{
		if (_pointSampler.IsValid == false)
		{
			_pointSampler = _bindlessRegistry.GetSamplerHandle(new SamplerDescriptor(
				FilterMode.Point,
				AddressMode.Clamp,
				AddressMode.Clamp,
				AddressMode.Clamp));
		}

		if (_linearSampler.IsValid == false)
		{
			_linearSampler = _bindlessRegistry.GetSamplerHandle(new SamplerDescriptor(
				FilterMode.Bilinear,
				AddressMode.Clamp,
				AddressMode.Clamp,
				AddressMode.Clamp));
		}
	}

	private IGfxPipeline EnsurePipeline(IGfxDevice device)
	{
		if (_pipeline is not null)
		{
			if (_compiledBackendKind.HasValue && _compiledBackendKind.Value != device.BackendKind)
			{
				throw new InvalidOperationException(
					$"Fsr3PrepareInputsPass is already compiled for backend '{_compiledBackendKind.Value}', but was requested for '{device.BackendKind}'.");
			}

			return _pipeline;
		}

		EnsureReflectionWriters(device.BackendKind);
		var pipelineKey = new PipelineKey(
			PassKind.Compute,
			vertexEntryPoint: null,
			pixelEntryPoint: null,
			computeEntryPoint: "Fsr3PrepareInputsCS",
			renderTargets: new RenderTargetFormats(Array.Empty<TextureFormat>()),
			depthStencil: new DepthStencilFormat(TextureFormat.Unknown),
			renderState: default,
			shaderVariant: "fsr3_prepare_inputs.compute.slang");
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
			EngineShaderPrograms.Fsr3PrepareInputs,
			"Fsr3PrepareInputsCS",
			backendKind);
		_computeShader = compiled.Bytecode;
		_threadGroupSize = compiled.ThreadGroupSize;
		var reflection = compiled.ReflectionLayout;
		_bindlessWriter = new ShaderPropertyWriter(reflection.GetConstantBuffer("Fsr3BindlessHandles"));
		_constantsWriter = new ShaderPropertyWriter(reflection.GetConstantBuffer("cbFSR3Upscaler"));
		_compiledBackendKind = backendKind;
	}
}
