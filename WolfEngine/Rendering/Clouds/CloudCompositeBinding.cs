using System.Numerics;
using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering.Clouds;

/// <summary>
/// What a pass that composites the clouds or receives their sun shadow binds: a shader's
/// <c>CloudComposite</c> field (cloud_sampling.slang). Disabled, with every flag clear, when the view has no clouds.
/// </summary>
internal readonly struct CloudCompositeBinding
{
	private const uint CompositeEnabled = 1u;
	private const uint ShadowsEnabled = 2u;

	public uint Flags { get; init; }
	public DescriptorHandle Color { get; init; }
	public DescriptorHandle Depth { get; init; }
	public DescriptorHandle Shadow { get; init; }
	public Vector2 TraceSize { get; init; }
	public Vector2 ShadowMapCentre { get; init; }
	public float BaseHeightAboveCamera { get; init; }
	public Vector3 SunDirection { get; init; }

	public bool Enabled => (Flags & CompositeEnabled) != 0u;

	/// <param name="includeComposite">
	/// Whether the pass composites the clouds themselves, which only deferred lighting does; the others read the
	/// shadow only, and then the trace textures are not bound.
	/// </param>
	public static CloudCompositeBinding Resolve(
		RenderGraphContext context,
		BindlessResourceRegistry bindlessRegistry,
		in CloudViewResources clouds,
		bool includeComposite)
	{
		if (clouds.Enabled == false)
		{
			return default;
		}

		var flags = 0u;
		var color = DescriptorHandle.Invalid;
		var depth = DescriptorHandle.Invalid;
		if (includeComposite)
		{
			flags |= CompositeEnabled;
			color = bindlessRegistry.GetTextureHandle(context.GetTexture(clouds.HistoryColorWrite));
			depth = bindlessRegistry.GetTextureHandle(context.GetTexture(clouds.HistoryDepthWrite));
		}

		var shadow = DescriptorHandle.Invalid;
		if (clouds.CastsShadows)
		{
			flags |= ShadowsEnabled;
			shadow = bindlessRegistry.GetTextureHandle(context.GetTexture(clouds.ShadowMap));
		}

		return new CloudCompositeBinding
		{
			Flags = flags,
			Color = color,
			Depth = depth,
			Shadow = shadow,
			TraceSize = new Vector2(clouds.TraceSize.X, clouds.TraceSize.Y),
			ShadowMapCentre = clouds.ShadowMapCentre,
			BaseHeightAboveCamera = clouds.Frame.BaseHeightAboveCamera,
			SunDirection = clouds.Frame.SunDirection
		};
	}

	/// <summary>Writes the binding into the field at <paramref name="path"/>, by default <c>cloudComposite</c>.</summary>
	public void Write(ShaderPropertyWriter writer, string path = "cloudComposite")
	{
		writer.SetUInt(path + ".flags", Flags);
		if (Flags == 0u)
		{
			return;
		}

		writer.SetUInt(path + ".colorHandle", Color.Value);
		writer.SetUInt(path + ".depthHandle", Depth.Value);
		writer.SetUInt(path + ".shadowHandle", Shadow.Value);
		writer.SetVector4(path + ".trace", new Vector4(TraceSize, 0.0f, 0.0f));
		writer.SetVector4(
			path + ".shadowMap",
			new Vector4(
				ShadowMapCentre.X,
				ShadowMapCentre.Y,
				1.0f / CloudViewResources.ShadowMapExtentMetres,
				BaseHeightAboveCamera));
		writer.SetVector4(path + ".sunDirection", new Vector4(SunDirection, 0.0f));
	}
}
