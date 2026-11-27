using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering.Shaders;

namespace WolfEngine.Rendering.Passes;

/// <summary>
/// FSR3 upscaler luma pyramid pass: single-pass downsample of the luma and farthest-depth
/// targets, plus the frame's auto exposure.
/// </summary>
/// <remarks>
/// Reduces prepare_inputs' outputs into a six-level mip chain in one dispatch. At the 1x1
/// level a single lane derives the exposure that RCAS and accumulate consume, so this pass is
/// what takes the effect off a placeholder exposure.
///
/// Two ordering requirements the render graph has to honour:
/// <list type="bullet">
/// <item>The global atomic counter must be zero before the dispatch. The shader resets it on
/// the way out, so steady state is self-sustaining, but the first frame after allocation - and
/// any frame after a dropped or partial dispatch - needs an explicit clear.</item>
/// <item>The mip chain must be cleared too. Upstream calls the mips aliasable but explicitly
/// excludes them from aliasing for this reason: the last thread group reads mip 5 texels that
/// may never have been written when the render size does not fill the final tile.</item>
/// </list>
///
/// Not yet wired into the render graph.
/// </remarks>
public sealed class Fsr3LumaPyramidPass
{
	/// <summary>Pyramid levels the shader declares. Fixed by the callbacks, not by render size.</summary>
	public const int SpdMipCount = 6;

	private readonly ComputeKernel _kernel;
	private readonly BindlessResourceRegistry _bindlessRegistry;
	private DescriptorHandle _pointSampler = DescriptorHandle.Invalid;
	private DescriptorHandle _linearSampler = DescriptorHandle.Invalid;

	public Fsr3LumaPyramidPass(ShaderSource shaderSource, BindlessResourceRegistry bindlessRegistry)
	{
		ArgumentNullException.ThrowIfNull(shaderSource);

		_kernel = new ComputeKernel(shaderSource, EngineShaderPrograms.Fsr3LumaPyramid, "Fsr3LumaPyramidCS");
		_bindlessRegistry = bindlessRegistry ?? throw new ArgumentNullException(nameof(bindlessRegistry));
	}

	public Fsr3LumaPyramidPassConfig BuildConfig(
		RenderGraphContext context,
		IGfxDevice device,
		RenderGraphResourceHandle currentLuma,
		RenderGraphResourceHandle farthestDepth,
		RenderGraphResourceHandle farthestDepthMip1,
		RenderGraphResourceHandle frameInfo,
		RenderGraphResourceHandle spdGlobalAtomic,
		ReadOnlySpan<RenderGraphResourceHandle> spdMips,
		in Fsr3ConstantValues constants)
	{
		ArgumentNullException.ThrowIfNull(context);
		ArgumentNullException.ThrowIfNull(device);
		if (spdMips.Length != SpdMipCount)
		{
			throw new ArgumentException(
				$"The luma pyramid needs exactly {SpdMipCount} mip targets, got {spdMips.Length}.",
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

		return new Fsr3LumaPyramidPassConfig
		{
			Pipeline = pipeline,
			CurrentLumaHandle = _bindlessRegistry.GetTextureHandle(context.GetTexture(currentLuma)),
			FarthestDepthHandle = _bindlessRegistry.GetTextureHandle(context.GetTexture(farthestDepth)),
			FarthestDepthMip1Handle = _bindlessRegistry.RegisterRwTexture(context.GetTexture(farthestDepthMip1)),
			FrameInfoHandle = _bindlessRegistry.RegisterRwTexture(context.GetTexture(frameInfo)),
			SpdGlobalAtomicHandle = _bindlessRegistry.RegisterRwTexture(context.GetTexture(spdGlobalAtomic)),
			SpdMipHandles = mipHandles,
			PointSampler = _pointSampler,
			LinearSampler = _linearSampler,
			Constants = constants,
			SpdSetup = Fsr3SpdSetup.Create(constants.RenderSize)
		};
	}

	public void Record(RenderGraphContext context, in Fsr3LumaPyramidPassConfig config)
	{
		ArgumentNullException.ThrowIfNull(context);

		var commandList = context.CommandList;
		commandList.BindPipeline(config.Pipeline);

		var bindlessWriter = _kernel.Writer("Fsr3BindlessHandles");
		bindlessWriter.Clear();
		bindlessWriter.SetUInt("currentLumaReadHandle", config.CurrentLumaHandle.Value);
		bindlessWriter.SetUInt("farthestDepthReadHandle", config.FarthestDepthHandle.Value);
		bindlessWriter.SetUInt("farthestDepthMip1Handle", config.FarthestDepthMip1Handle.Value);
		bindlessWriter.SetUInt("frameInfoHandle", config.FrameInfoHandle.Value);
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
