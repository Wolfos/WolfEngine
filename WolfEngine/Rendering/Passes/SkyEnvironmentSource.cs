using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering.Passes;

/// <summary>
/// Where a view reads sky radiance from this frame: its own environment texture in Procedural and Gradient mode,
/// or the HDRI asset texture in HDRI mode. Passes resolve it to a <see cref="SkyEnvironmentBinding"/> and write it
/// into a shader's <c>SkyEnvironment</c> struct (sky_sampling.slang).
/// </summary>
internal readonly struct SkyEnvironmentSource
{
	/// <summary>The view's environment texture, when the sky is drawn into one.</summary>
	public RenderGraphResourceHandle ViewEnvironment { get; init; }

	/// <summary>The HDRI texture, once its upload has published a view. Not a graph resource.</summary>
	public IGfxTexture? HdriTexture { get; init; }

	public bool IsCube { get; init; }
	public int MipCount { get; init; }

	/// <summary>Width of an equirectangular texture's top mip, or the face size of a cube.</summary>
	public int BaseSize { get; init; }

	public float RotationRadians { get; init; }
	public float Intensity { get; init; }

	public SkyEnvironmentBinding Resolve(RenderGraphContext context, BindlessResourceRegistry bindlessRegistry)
	{
		var handle = DescriptorHandle.Invalid;
		if (ViewEnvironment.IsValid)
		{
			handle = bindlessRegistry.GetTextureHandle(context.GetTexture(ViewEnvironment));
		}
		else if (HdriTexture is not null)
		{
			handle = HdriTexture.ShaderResourceView;
		}

		return new SkyEnvironmentBinding(
			handle,
			IsCube,
			(uint)Math.Max(MipCount, 1),
			(uint)Math.Max(BaseSize, 1),
			RotationRadians,
			handle.IsValid ? Intensity : 0.0f);
	}
}

/// <summary>A <see cref="SkyEnvironmentSource"/> resolved to a bindless handle for one pass.</summary>
public readonly record struct SkyEnvironmentBinding(
	DescriptorHandle Handle,
	bool IsCube,
	uint MipCount,
	uint BaseSize,
	float RotationRadians,
	float Intensity)
{
	public static SkyEnvironmentBinding None => new(DescriptorHandle.Invalid, false, 1, 1, 0.0f, 0.0f);

	/// <summary>Writes the binding into the shader's <c>SkyEnvironment skyEnvironment</c> constant.</summary>
	internal void Write(ShaderPropertyWriter writer)
	{
		writer.SetUInt("skyEnvironment.handle", Handle.Value);
		writer.SetUInt("skyEnvironment.isCube", IsCube ? 1u : 0u);
		writer.SetUInt("skyEnvironment.mipCount", MipCount);
		writer.SetUInt("skyEnvironment.baseSize", BaseSize);
		writer.SetFloat("skyEnvironment.yawCos", MathF.Cos(RotationRadians));
		writer.SetFloat("skyEnvironment.yawSin", MathF.Sin(RotationRadians));
		writer.SetFloat("skyEnvironment.intensity", Intensity);
	}
}
