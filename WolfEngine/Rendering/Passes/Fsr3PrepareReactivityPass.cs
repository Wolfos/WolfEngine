using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering.Shaders;

namespace WolfEngine.Rendering.Passes;

/// <summary>
/// FSR3 prepare-reactivity pass: classifies history confidence at render resolution and
/// seeds display-resolution locks for thin features.
/// </summary>
/// <remarks>
/// The reconstructed-depth input remains bound through the R32Uint UAV alias so its stored
/// float bits can be read exactly; the engine's regular bindless SRV view is float-typed.
/// The accumulation texture is intentionally bound as both SRV and UAV because this pass
/// reprojects its previous value and writes the current value in-place, matching upstream.
///
/// Not yet wired into the render graph.
/// </remarks>
public sealed class Fsr3PrepareReactivityPass
{
	private const int ThreadGroupWorkRegionDim = 8;

	private readonly ComputeKernel _kernel;
	private readonly BindlessResourceRegistry _bindlessRegistry;
	private DescriptorHandle _pointSampler = DescriptorHandle.Invalid;
	private DescriptorHandle _linearSampler = DescriptorHandle.Invalid;

	public Fsr3PrepareReactivityPass(ShaderSource shaderSource, BindlessResourceRegistry bindlessRegistry)
	{
		ArgumentNullException.ThrowIfNull(shaderSource);

		_kernel = new ComputeKernel(shaderSource, EngineShaderPrograms.Fsr3PrepareReactivity, "Fsr3PrepareReactivityCS");
		_bindlessRegistry = bindlessRegistry ?? throw new ArgumentNullException(nameof(bindlessRegistry));
	}

	public Fsr3PrepareReactivityPassConfig BuildConfig(
		RenderGraphContext context,
		IGfxDevice device,
		RenderGraphResourceHandle reconstructedPrevNearestDepth,
		RenderGraphResourceHandle dilatedMotionVectors,
		RenderGraphResourceHandle dilatedDepth,
		RenderGraphResourceHandle inputDepth,
		RenderGraphResourceHandle inputMotionVectors,
		RenderGraphResourceHandle reactiveMask,
		RenderGraphResourceHandle transparencyAndCompositionMask,
		RenderGraphResourceHandle accumulationRead,
		RenderGraphResourceHandle accumulationWrite,
		RenderGraphResourceHandle shadingChange,
		RenderGraphResourceHandle currentLuma,
		RenderGraphResourceHandle exposure,
		RenderGraphResourceHandle dilatedReactiveMasks,
		RenderGraphResourceHandle newLocks,
		in Fsr3ConstantValues constants,
		float alphaTestReactiveScale,
		float transparencyAndCompositionMaskScale)
	{
		ArgumentNullException.ThrowIfNull(context);
		ArgumentNullException.ThrowIfNull(device);

		var pipeline = _kernel.GetPipeline(device);
		_bindlessRegistry.EnsureInitialized(device);
		EnsureSamplers();

		var accumulationReadTexture = context.GetTexture(accumulationRead);
		var accumulationWriteTexture = context.GetTexture(accumulationWrite);
		return new Fsr3PrepareReactivityPassConfig
		{
			Pipeline = pipeline,
			ReconstructedPrevNearestDepthHandle = _bindlessRegistry.RegisterRwTexture(
				context.GetTexture(reconstructedPrevNearestDepth)),
			DilatedMotionVectorsHandle = _bindlessRegistry.GetTextureHandle(
				context.GetTexture(dilatedMotionVectors)),
			DilatedDepthHandle = _bindlessRegistry.GetTextureHandle(context.GetTexture(dilatedDepth)),
			InputDepthHandle = _bindlessRegistry.GetTextureHandle(context.GetTexture(inputDepth)),
			InputMotionVectorsHandle = _bindlessRegistry.GetTextureHandle(context.GetTexture(inputMotionVectors)),
			ReactiveMaskHandle = _bindlessRegistry.GetTextureHandle(context.GetTexture(reactiveMask)),
			TransparencyAndCompositionMaskHandle = _bindlessRegistry.GetTextureHandle(
				context.GetTexture(transparencyAndCompositionMask)),
			AccumulationReadHandle = _bindlessRegistry.GetTextureHandle(accumulationReadTexture),
			ShadingChangeHandle = _bindlessRegistry.GetTextureHandle(context.GetTexture(shadingChange)),
			CurrentLumaHandle = _bindlessRegistry.GetTextureHandle(context.GetTexture(currentLuma)),
			ExposureHandle = _bindlessRegistry.GetTextureHandle(context.GetTexture(exposure)),
			DilatedReactiveMasksHandle = _bindlessRegistry.RegisterRwTexture(
				context.GetTexture(dilatedReactiveMasks)),
			NewLocksHandle = _bindlessRegistry.RegisterRwTexture(context.GetTexture(newLocks)),
			AccumulationWriteHandle = _bindlessRegistry.RegisterRwTexture(accumulationWriteTexture),
			PointSampler = _pointSampler,
			LinearSampler = _linearSampler,
			Constants = constants,
			AlphaTestReactiveScale = Math.Clamp(alphaTestReactiveScale, 0.0f, 1.0f),
			TransparencyAndCompositionMaskScale = Math.Clamp(
				transparencyAndCompositionMaskScale, 0.0f, 1.0f)
		};
	}

	public void Record(RenderGraphContext context, in Fsr3PrepareReactivityPassConfig config)
	{
		ArgumentNullException.ThrowIfNull(context);

		var commandList = context.CommandList;
		commandList.BindPipeline(config.Pipeline);

		var bindlessWriter = _kernel.Writer("Fsr3BindlessHandles");
		bindlessWriter.Clear();
		bindlessWriter.SetUInt("reconstructedPrevNearestDepthHandle", config.ReconstructedPrevNearestDepthHandle.Value);
		bindlessWriter.SetUInt("dilatedMotionVectorsReadHandle", config.DilatedMotionVectorsHandle.Value);
		bindlessWriter.SetUInt("dilatedDepthHandle", config.DilatedDepthHandle.Value);
		bindlessWriter.SetUInt("inputDepthHandle", config.InputDepthHandle.Value);
		bindlessWriter.SetUInt("inputMotionVectorsHandle", config.InputMotionVectorsHandle.Value);
		bindlessWriter.SetUInt("reactiveMaskHandle", config.ReactiveMaskHandle.Value);
		bindlessWriter.SetUInt(
			"transparencyAndCompositionMaskHandle", config.TransparencyAndCompositionMaskHandle.Value);
		bindlessWriter.SetUInt("accumulationReadHandle", config.AccumulationReadHandle.Value);
		bindlessWriter.SetUInt("shadingChangeHandle", config.ShadingChangeHandle.Value);
		bindlessWriter.SetUInt("currentLumaReadHandle", config.CurrentLumaHandle.Value);
		bindlessWriter.SetUInt("exposureHandle", config.ExposureHandle.Value);
		bindlessWriter.SetUInt("dilatedReactiveMasksHandle", config.DilatedReactiveMasksHandle.Value);
		bindlessWriter.SetUInt("newLocksHandle", config.NewLocksHandle.Value);
		bindlessWriter.SetUInt("accumulationWriteHandle", config.AccumulationWriteHandle.Value);
		bindlessWriter.SetUInt("pointSamplerHandle", config.PointSampler.Value);
		bindlessWriter.SetUInt("linearSamplerHandle", config.LinearSampler.Value);
		commandList.SetComputeConstants(bindlessWriter.RegisterIndex, bindlessWriter.AsBytes());

		var constantsWriter = _kernel.Writer("cbFSR3Upscaler");
		Fsr3Constants.Write(constantsWriter, config.Constants);
		commandList.SetComputeConstants(constantsWriter.RegisterIndex, constantsWriter.AsBytes());

		var settingsWriter = _kernel.Writer("cbPrepareReactivity");
		settingsWriter.Clear();
		settingsWriter.SetFloat("alphaTestReactiveScale", config.AlphaTestReactiveScale);
		settingsWriter.SetFloat(
			"transparencyAndCompositionMaskScale",
			config.TransparencyAndCompositionMaskScale);
		commandList.SetComputeConstants(settingsWriter.RegisterIndex, settingsWriter.AsBytes());

		var renderSize = config.Constants.RenderSize;
		commandList.Dispatch(
			(uint)Math.Max((renderSize.X + ThreadGroupWorkRegionDim - 1) / ThreadGroupWorkRegionDim, 1),
			(uint)Math.Max((renderSize.Y + ThreadGroupWorkRegionDim - 1) / ThreadGroupWorkRegionDim, 1),
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
}
