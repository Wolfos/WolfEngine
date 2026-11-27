using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering.Shaders;

namespace WolfEngine.Rendering.Passes;

/// <summary>
/// FSR3 upscaler RCAS pass - the first slice of the FidelityFX FSR3 port.
/// </summary>
/// <remarks>
/// This pass establishes the scaffolding every other FSR3 pass will reuse: the shared
/// <c>cbFSR3Upscaler</c> constant block, the bindless callbacks layer, and the ffx_core
/// subset. The other passes plug into the same shared writers as they land.
///
/// It is not yet wired into the render graph. RCAS is meaningful only on the accumulate
/// pass's output, so it stays inert until accumulate lands; what it proves today is that
/// a faithfully ported FFX pass compiles and reflects on both DXIL and metallib.
/// </remarks>
public sealed class Fsr3RcasPass
{
	/// <summary>
	/// Pixels covered per thread group on each axis. The shader runs 64 lanes per group but
	/// each lane filters four pixels, so the dispatch count does not follow from the
	/// threadgroup size the way the engine's other compute passes do.
	/// </summary>
	private const int ThreadGroupWorkRegionDim = 16;

	private readonly ComputeKernel _kernel;
	private readonly BindlessResourceRegistry _bindlessRegistry;
	private DescriptorHandle _pointSampler = DescriptorHandle.Invalid;
	private DescriptorHandle _linearSampler = DescriptorHandle.Invalid;

	public Fsr3RcasPass(ShaderSource shaderSource, BindlessResourceRegistry bindlessRegistry)
	{
		ArgumentNullException.ThrowIfNull(shaderSource);

		_kernel = new ComputeKernel(shaderSource, EngineShaderPrograms.Fsr3Rcas, "Fsr3RcasCS");
		_bindlessRegistry = bindlessRegistry ?? throw new ArgumentNullException(nameof(bindlessRegistry));
	}

	public Fsr3RcasPassConfig BuildConfig(
		RenderGraphContext context,
		IGfxDevice device,
		RenderGraphResourceHandle input,
		RenderGraphResourceHandle output,
		RenderGraphResourceHandle exposure,
		in Fsr3ConstantValues constants,
		float sharpness,
		bool enabled)
	{
		ArgumentNullException.ThrowIfNull(context);
		ArgumentNullException.ThrowIfNull(device);

		var pipeline = _kernel.GetPipeline(device);
		_bindlessRegistry.EnsureInitialized(device);
		EnsureSamplers();

		return new Fsr3RcasPassConfig
		{
			Pipeline = pipeline,
			InputHandle = _bindlessRegistry.GetTextureHandle(context.GetTexture(input)),
			OutputHandle = _bindlessRegistry.RegisterRwTexture(context.GetTexture(output)),
			ExposureHandle = _bindlessRegistry.GetTextureHandle(context.GetTexture(exposure)),
			PointSampler = _pointSampler,
			LinearSampler = _linearSampler,
			Constants = constants,
			Sharpness = Math.Clamp(sharpness, 0.0f, 1.0f),
			Enabled = enabled
		};
	}

	public void Record(RenderGraphContext context, in Fsr3RcasPassConfig config)
	{
		ArgumentNullException.ThrowIfNull(context);

		var commandList = context.CommandList;
		commandList.BindPipeline(config.Pipeline);

		var bindlessWriter = _kernel.Writer("Fsr3BindlessHandles");
		bindlessWriter.Clear();
		bindlessWriter.SetUInt("rcasInputHandle", config.InputHandle.Value);
		bindlessWriter.SetUInt("upscaledOutputHandle", config.OutputHandle.Value);
		bindlessWriter.SetUInt("exposureHandle", config.ExposureHandle.Value);
		bindlessWriter.SetUInt("pointSamplerHandle", config.PointSampler.Value);
		bindlessWriter.SetUInt("linearSamplerHandle", config.LinearSampler.Value);
		commandList.SetComputeConstants(bindlessWriter.RegisterIndex, bindlessWriter.AsBytes());

		WriteSharedConstants(commandList, config);
		WriteRcasConstants(commandList, config);

		var dispatchX = (uint)Math.Max(
			(config.UpscaleSize.X + ThreadGroupWorkRegionDim - 1) / ThreadGroupWorkRegionDim, 1);
		var dispatchY = (uint)Math.Max(
			(config.UpscaleSize.Y + ThreadGroupWorkRegionDim - 1) / ThreadGroupWorkRegionDim, 1);
		commandList.Dispatch(dispatchX, dispatchY, 1);
	}

	/// <summary>
	/// Writes <c>cbFSR3Upscaler</c>, the block every FSR3 pass shares.
	/// </summary>
	private void WriteSharedConstants(IGfxCommandList commandList, in Fsr3RcasPassConfig config)
	{
		var writer = _kernel.Writer("cbFSR3Upscaler");
		Fsr3Constants.Write(writer, config.Constants);
		commandList.SetComputeConstants(writer.RegisterIndex, writer.AsBytes());
	}

	/// <summary>
	/// Writes <c>cbRCAS</c>, reproducing <c>FsrRcasCon</c> from ffx_fsr1.h against the
	/// stops remap FSR3 applies in ffx_fsr3upscaler.cpp.
	/// </summary>
	private void WriteRcasConstants(IGfxCommandList commandList, in Fsr3RcasPassConfig config)
	{
		var writer = _kernel.Writer("cbRCAS");

		// ffx_fsr3upscaler.cpp: sharpenessRemapped = (-2 * sharpness) + 2, so a dispatch
		// sharpness of 1 becomes 0 stops (maximum) and 0 becomes 2 stops.
		var sharpnessStops = (-2.0f * config.Sharpness) + 2.0f;

		// FsrRcasCon: transform from stops to a linear value, then bit-cast.
		var linearSharpness = MathF.Pow(2.0f, -sharpnessStops);

		writer.Clear();
		writer.SetUInt("rcasConfigX", BitConverter.SingleToUInt32Bits(linearSharpness));
		// Upstream leaves yzw unused. Carry the host's enable flag in y so the disabled
		// path can copy the accumulated image without compiling another permutation.
		writer.SetUInt("rcasConfigY", config.Enabled ? 1u : 0u);
		commandList.SetComputeConstants(writer.RegisterIndex, writer.AsBytes());
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
}
