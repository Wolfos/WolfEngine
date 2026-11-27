using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering.Shaders;

namespace WolfEngine.Rendering.Passes;

/// <summary>FSR3 temporal accumulation and native-resolution reconstruction pass.</summary>
public sealed class Fsr3AccumulatePass
{
	private const int ThreadGroupDim = 8;
	private readonly ComputeKernel _kernel;
	private readonly BindlessResourceRegistry _bindlessRegistry;
	private DescriptorHandle _pointSampler = DescriptorHandle.Invalid;
	private DescriptorHandle _linearSampler = DescriptorHandle.Invalid;

	public Fsr3AccumulatePass(ShaderSource shaderSource, BindlessResourceRegistry bindlessRegistry)
	{
		ArgumentNullException.ThrowIfNull(shaderSource);

		_kernel = new ComputeKernel(shaderSource, EngineShaderPrograms.Fsr3Accumulate, "Fsr3AccumulateCS");
		_bindlessRegistry = bindlessRegistry ?? throw new ArgumentNullException(nameof(bindlessRegistry));
	}

	public Fsr3AccumulatePassConfig BuildConfig(RenderGraphContext context, IGfxDevice device,
		RenderGraphResourceHandle exposure, RenderGraphResourceHandle inputColor,
		RenderGraphResourceHandle dilatedMotionVectors, RenderGraphResourceHandle dilatedReactiveMasks,
		RenderGraphResourceHandle farthestDepthMip1, RenderGraphResourceHandle lumaInstability,
		RenderGraphResourceHandle newLocks, RenderGraphResourceHandle historyRead,
		RenderGraphResourceHandle historyWrite, in Fsr3ConstantValues constants)
	{
		ArgumentNullException.ThrowIfNull(context);
		ArgumentNullException.ThrowIfNull(device);
		var pipeline = _kernel.GetPipeline(device);
		_bindlessRegistry.EnsureInitialized(device);
		EnsureSamplers();
		return new Fsr3AccumulatePassConfig
		{
			Pipeline = pipeline,
			ExposureHandle = _bindlessRegistry.GetTextureHandle(context.GetTexture(exposure)),
			InputColorHandle = _bindlessRegistry.GetTextureHandle(context.GetTexture(inputColor)),
			DilatedMotionVectorsHandle = _bindlessRegistry.GetTextureHandle(context.GetTexture(dilatedMotionVectors)),
			DilatedReactiveMasksHandle = _bindlessRegistry.GetTextureHandle(context.GetTexture(dilatedReactiveMasks)),
			FarthestDepthMip1Handle = _bindlessRegistry.GetTextureHandle(context.GetTexture(farthestDepthMip1)),
			LumaInstabilityHandle = _bindlessRegistry.GetTextureHandle(context.GetTexture(lumaInstability)),
			NewLocksHandle = _bindlessRegistry.RegisterRwTexture(context.GetTexture(newLocks)),
			HistoryReadHandle = _bindlessRegistry.GetTextureHandle(context.GetTexture(historyRead)),
			HistoryWriteHandle = _bindlessRegistry.RegisterRwTexture(context.GetTexture(historyWrite)),
			PointSampler = _pointSampler,
			LinearSampler = _linearSampler,
			Constants = constants
		};
	}

	public void Record(RenderGraphContext context, in Fsr3AccumulatePassConfig config)
	{
		var commandList = context.CommandList;
		commandList.BindPipeline(config.Pipeline);
		var handles = _kernel.Writer("Fsr3BindlessHandles");
		handles.Clear();
		handles.SetUInt("exposureHandle", config.ExposureHandle.Value);
		handles.SetUInt("inputColorHandle", config.InputColorHandle.Value);
		handles.SetUInt("dilatedMotionVectorsReadHandle", config.DilatedMotionVectorsHandle.Value);
		handles.SetUInt("dilatedReactiveMasksHandle", config.DilatedReactiveMasksHandle.Value);
		handles.SetUInt("farthestDepthMip1Handle", config.FarthestDepthMip1Handle.Value);
		handles.SetUInt("lumaInstabilityHandle", config.LumaInstabilityHandle.Value);
		handles.SetUInt("newLocksHandle", config.NewLocksHandle.Value);
		handles.SetUInt("internalUpscaledColorHandle", config.HistoryReadHandle.Value);
		handles.SetUInt("internalUpscaledColorWriteHandle", config.HistoryWriteHandle.Value);
		handles.SetUInt("pointSamplerHandle", config.PointSampler.Value);
		handles.SetUInt("linearSamplerHandle", config.LinearSampler.Value);
		commandList.SetComputeConstants(handles.RegisterIndex, handles.AsBytes());
		var constants = _kernel.Writer("cbFSR3Upscaler");
		Fsr3Constants.Write(constants, config.Constants);
		commandList.SetComputeConstants(constants.RegisterIndex, constants.AsBytes());
		commandList.Dispatch((uint)Math.Max((config.Constants.UpscaleSize.X + 7) / ThreadGroupDim, 1),
			(uint)Math.Max((config.Constants.UpscaleSize.Y + 7) / ThreadGroupDim, 1), 1);
	}

	private void EnsureSamplers()
	{
		if (!_pointSampler.IsValid) _pointSampler = _bindlessRegistry.GetSamplerHandle(new SamplerDescriptor(
			FilterMode.Point, AddressMode.Clamp, AddressMode.Clamp, AddressMode.Clamp));
		if (!_linearSampler.IsValid) _linearSampler = _bindlessRegistry.GetSamplerHandle(new SamplerDescriptor(
			FilterMode.Bilinear, AddressMode.Clamp, AddressMode.Clamp, AddressMode.Clamp));
	}
}
