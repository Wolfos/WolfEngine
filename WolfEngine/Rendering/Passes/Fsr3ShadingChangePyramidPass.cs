using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering.Shaders;

namespace WolfEngine.Rendering.Passes;

/// <summary>
/// FSR3 upscaler shading change pyramid pass.
/// </summary>
/// <remarks>
/// Measures how much the shading changed between frames, independently of geometry motion,
/// and reduces that into a pyramid so <see cref="Fsr3ShadingChangePass"/> can look at it
/// across scales.
///
/// The same two ordering requirements as <see cref="Fsr3LumaPyramidPass"/> apply: the SPD
/// counter must be zero before dispatch, and the mip chain must be cleared.
///
/// Level 0 of this pyramid is at half render resolution, not render resolution, because SPD's
/// first reduction is already a 2x downsample of the render-resolution difference.
///
/// Not yet wired into the render graph.
/// </remarks>
public sealed class Fsr3ShadingChangePyramidPass
{
	/// <summary>Pyramid levels the shader declares.</summary>
	public const int SpdMipCount = 6;

	private readonly ComputeKernel _kernel;
	private readonly BindlessResourceRegistry _bindlessRegistry;
	private DescriptorHandle _pointSampler = DescriptorHandle.Invalid;
	private DescriptorHandle _linearSampler = DescriptorHandle.Invalid;

	public Fsr3ShadingChangePyramidPass(ShaderSource shaderSource, BindlessResourceRegistry bindlessRegistry)
	{
		ArgumentNullException.ThrowIfNull(shaderSource);

		_kernel = new ComputeKernel(shaderSource, EngineShaderPrograms.Fsr3ShadingChangePyramid, "Fsr3ShadingChangePyramidCS");
		_bindlessRegistry = bindlessRegistry ?? throw new ArgumentNullException(nameof(bindlessRegistry));
	}

	public Fsr3ShadingChangePyramidPassConfig BuildConfig(
		RenderGraphContext context,
		IGfxDevice device,
		RenderGraphResourceHandle currentLuma,
		RenderGraphResourceHandle previousLuma,
		RenderGraphResourceHandle dilatedMotionVectors,
		RenderGraphResourceHandle exposure,
		RenderGraphResourceHandle spdGlobalAtomic,
		ReadOnlySpan<RenderGraphResourceHandle> spdMips,
		in Fsr3ConstantValues constants)
	{
		ArgumentNullException.ThrowIfNull(context);
		ArgumentNullException.ThrowIfNull(device);
		if (spdMips.Length != SpdMipCount)
		{
			throw new ArgumentException(
				$"The shading change pyramid needs exactly {SpdMipCount} mip targets, got {spdMips.Length}.",
				nameof(spdMips));
		}

		var pipeline = _kernel.GetPipeline(device);
		_bindlessRegistry.EnsureInitialized(device);
		EnsureSamplers();

		var mipHandles = new DescriptorHandle[SpdMipCount];
		for (var mip = 0; mip < SpdMipCount; mip++)
		{
			mipHandles[mip] = _bindlessRegistry.RegisterRwTexture(context.GetTexture(spdMips[mip]));
		}

		return new Fsr3ShadingChangePyramidPassConfig
		{
			Pipeline = pipeline,
			CurrentLumaHandle = _bindlessRegistry.GetTextureHandle(context.GetTexture(currentLuma)),
			PreviousLumaHandle = _bindlessRegistry.GetTextureHandle(context.GetTexture(previousLuma)),
			DilatedMotionVectorsHandle = _bindlessRegistry.GetTextureHandle(context.GetTexture(dilatedMotionVectors)),
			ExposureHandle = _bindlessRegistry.GetTextureHandle(context.GetTexture(exposure)),
			SpdGlobalAtomicHandle = _bindlessRegistry.RegisterRwTexture(context.GetTexture(spdGlobalAtomic)),
			SpdMipHandles = mipHandles,
			PointSampler = _pointSampler,
			LinearSampler = _linearSampler,
			Constants = constants,
			SpdSetup = Fsr3SpdSetup.Create(constants.RenderSize)
		};
	}

	public void Record(RenderGraphContext context, in Fsr3ShadingChangePyramidPassConfig config)
	{
		ArgumentNullException.ThrowIfNull(context);

		var commandList = context.CommandList;
		commandList.BindPipeline(config.Pipeline);

		var bindlessWriter = _kernel.Writer("Fsr3BindlessHandles");
		bindlessWriter.Clear();
		bindlessWriter.SetUInt("currentLumaReadHandle", config.CurrentLumaHandle.Value);
		bindlessWriter.SetUInt("previousLumaHandle", config.PreviousLumaHandle.Value);
		bindlessWriter.SetUInt("dilatedMotionVectorsReadHandle", config.DilatedMotionVectorsHandle.Value);
		bindlessWriter.SetUInt("exposureHandle", config.ExposureHandle.Value);
		bindlessWriter.SetUInt("spdGlobalAtomicHandle", config.SpdGlobalAtomicHandle.Value);
		bindlessWriter.SetUInt("spdMip0Handle", config.SpdMipHandles[0].Value);
		bindlessWriter.SetUInt("spdMip1Handle", config.SpdMipHandles[1].Value);
		bindlessWriter.SetUInt("spdMip2Handle", config.SpdMipHandles[2].Value);
		bindlessWriter.SetUInt("spdMip3Handle", config.SpdMipHandles[3].Value);
		bindlessWriter.SetUInt("spdMip4Handle", config.SpdMipHandles[4].Value);
		bindlessWriter.SetUInt("spdMip5Handle", config.SpdMipHandles[5].Value);
		bindlessWriter.SetUInt("pointSamplerHandle", config.PointSampler.Value);
		bindlessWriter.SetUInt("linearSamplerHandle", config.LinearSampler.Value);
		commandList.SetComputeConstants(bindlessWriter.RegisterIndex, bindlessWriter.AsBytes());

		var constantsWriter = _kernel.Writer("cbFSR3Upscaler");
		Fsr3Constants.Write(constantsWriter, config.Constants);
		commandList.SetComputeConstants(constantsWriter.RegisterIndex, constantsWriter.AsBytes());

		var spdWriter = _kernel.Writer("cbSPD");
		var setup = config.SpdSetup;
		spdWriter.Clear();
		spdWriter.SetUInt("spdMips", setup.MipCount);
		spdWriter.SetUInt("spdNumWorkGroups", setup.NumWorkGroups);
		spdWriter.SetUInt("spdWorkGroupOffsetX", (uint)Math.Max(setup.WorkGroupOffset.X, 0));
		spdWriter.SetUInt("spdWorkGroupOffsetY", (uint)Math.Max(setup.WorkGroupOffset.Y, 0));
		spdWriter.SetUInt("spdRenderSizeX", (uint)Math.Max(config.Constants.RenderSize.X, 1));
		spdWriter.SetUInt("spdRenderSizeY", (uint)Math.Max(config.Constants.RenderSize.Y, 1));
		commandList.SetComputeConstants(spdWriter.RegisterIndex, spdWriter.AsBytes());

		commandList.Dispatch(
			(uint)Math.Max(setup.DispatchThreadGroupCount.X, 1),
			(uint)Math.Max(setup.DispatchThreadGroupCount.Y, 1),
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
