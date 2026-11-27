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

	private readonly ComputeKernel _kernel;
	private readonly BindlessResourceRegistry _bindlessRegistry;
	private DescriptorHandle _pointSampler = DescriptorHandle.Invalid;
	private DescriptorHandle _linearSampler = DescriptorHandle.Invalid;

	public Fsr3ShadingChangePass(ShaderSource shaderSource, BindlessResourceRegistry bindlessRegistry)
	{
		ArgumentNullException.ThrowIfNull(shaderSource);

		_kernel = new ComputeKernel(shaderSource, EngineShaderPrograms.Fsr3ShadingChange, "Fsr3ShadingChangeCS");
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

		var pipeline = _kernel.GetPipeline(device);
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

		var bindlessWriter = _kernel.Writer("Fsr3BindlessHandles");
		bindlessWriter.Clear();
		for (var mip = 0; mip < config.SpdMipReadHandles.Length; mip++)
		{
			bindlessWriter.SetUInt($"spdMipReadHandles[{mip}]", config.SpdMipReadHandles[mip].Value);
		}

		bindlessWriter.SetUInt("shadingChangeHandle", config.ShadingChangeHandle.Value);
		bindlessWriter.SetUInt("pointSamplerHandle", config.PointSampler.Value);
		bindlessWriter.SetUInt("linearSamplerHandle", config.LinearSampler.Value);
		commandList.SetComputeConstants(bindlessWriter.RegisterIndex, bindlessWriter.AsBytes());

		var constantsWriter = _kernel.Writer("cbFSR3Upscaler");
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
}
