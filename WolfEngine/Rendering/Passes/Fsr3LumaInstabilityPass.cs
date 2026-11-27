using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering.Shaders;

namespace WolfEngine.Rendering.Passes;

/// <summary>FSR3 temporal luma-instability classification pass.</summary>
public sealed class Fsr3LumaInstabilityPass
{
	private const int ThreadGroupDim = 8;
	private readonly ComputeKernel _kernel;
	private readonly BindlessResourceRegistry _bindlessRegistry;
	private DescriptorHandle _pointSampler = DescriptorHandle.Invalid;
	private DescriptorHandle _linearSampler = DescriptorHandle.Invalid;

	public Fsr3LumaInstabilityPass(ShaderSource shaderSource, BindlessResourceRegistry bindlessRegistry)
	{
		ArgumentNullException.ThrowIfNull(shaderSource);

		_kernel = new ComputeKernel(shaderSource, EngineShaderPrograms.Fsr3LumaInstability, "Fsr3LumaInstabilityCS");
		_bindlessRegistry = bindlessRegistry ?? throw new ArgumentNullException(nameof(bindlessRegistry));
	}

	public Fsr3LumaInstabilityPassConfig BuildConfig(RenderGraphContext context, IGfxDevice device,
		RenderGraphResourceHandle exposure, RenderGraphResourceHandle dilatedReactiveMasks,
		RenderGraphResourceHandle dilatedMotionVectors, RenderGraphResourceHandle lumaHistoryRead,
		RenderGraphResourceHandle lumaHistoryWrite,
		RenderGraphResourceHandle farthestDepthMip1, RenderGraphResourceHandle currentLuma,
		RenderGraphResourceHandle lumaInstability, in Fsr3ConstantValues constants)
	{
		ArgumentNullException.ThrowIfNull(context);
		ArgumentNullException.ThrowIfNull(device);
		var pipeline = _kernel.GetPipeline(device);
		_bindlessRegistry.EnsureInitialized(device);
		EnsureSamplers();
		var historyRead = context.GetTexture(lumaHistoryRead);
		var historyWrite = context.GetTexture(lumaHistoryWrite);
		return new Fsr3LumaInstabilityPassConfig
		{
			Pipeline = pipeline,
			ExposureHandle = _bindlessRegistry.GetTextureHandle(context.GetTexture(exposure)),
			DilatedReactiveMasksHandle = _bindlessRegistry.GetTextureHandle(context.GetTexture(dilatedReactiveMasks)),
			DilatedMotionVectorsHandle = _bindlessRegistry.GetTextureHandle(context.GetTexture(dilatedMotionVectors)),
			LumaHistoryReadHandle = _bindlessRegistry.GetTextureHandle(historyRead),
			LumaHistoryWriteHandle = _bindlessRegistry.RegisterRwTexture(historyWrite),
			FarthestDepthMip1Handle = _bindlessRegistry.GetTextureHandle(context.GetTexture(farthestDepthMip1)),
			CurrentLumaHandle = _bindlessRegistry.GetTextureHandle(context.GetTexture(currentLuma)),
			LumaInstabilityHandle = _bindlessRegistry.RegisterRwTexture(context.GetTexture(lumaInstability)),
			PointSampler = _pointSampler,
			LinearSampler = _linearSampler,
			Constants = constants
		};
	}

	public void Record(RenderGraphContext context, in Fsr3LumaInstabilityPassConfig config)
	{
		var commandList = context.CommandList;
		commandList.BindPipeline(config.Pipeline);
		var handles = _kernel.Writer("Fsr3BindlessHandles");
		handles.Clear();
		handles.SetUInt("exposureHandle", config.ExposureHandle.Value);
		handles.SetUInt("dilatedReactiveMasksHandle", config.DilatedReactiveMasksHandle.Value);
		handles.SetUInt("dilatedMotionVectorsReadHandle", config.DilatedMotionVectorsHandle.Value);
		handles.SetUInt("lumaHistoryReadHandle", config.LumaHistoryReadHandle.Value);
		handles.SetUInt("lumaHistoryWriteHandle", config.LumaHistoryWriteHandle.Value);
		handles.SetUInt("farthestDepthMip1Handle", config.FarthestDepthMip1Handle.Value);
		handles.SetUInt("currentLumaReadHandle", config.CurrentLumaHandle.Value);
		handles.SetUInt("lumaInstabilityHandle", config.LumaInstabilityHandle.Value);
		handles.SetUInt("pointSamplerHandle", config.PointSampler.Value);
		handles.SetUInt("linearSamplerHandle", config.LinearSampler.Value);
		commandList.SetComputeConstants(handles.RegisterIndex, handles.AsBytes());

		var constants = _kernel.Writer("cbFSR3Upscaler");
		Fsr3Constants.Write(constants, config.Constants);
		commandList.SetComputeConstants(constants.RegisterIndex, constants.AsBytes());
		commandList.Dispatch((uint)Math.Max((config.Constants.RenderSize.X + 7) / ThreadGroupDim, 1),
			(uint)Math.Max((config.Constants.RenderSize.Y + 7) / ThreadGroupDim, 1), 1);
	}

	private void EnsureSamplers()
	{
		if (!_pointSampler.IsValid) _pointSampler = _bindlessRegistry.GetSamplerHandle(new SamplerDescriptor(
			FilterMode.Point, AddressMode.Clamp, AddressMode.Clamp, AddressMode.Clamp));
		if (!_linearSampler.IsValid) _linearSampler = _bindlessRegistry.GetSamplerHandle(new SamplerDescriptor(
			FilterMode.Bilinear, AddressMode.Clamp, AddressMode.Clamp, AddressMode.Clamp));
	}
}
