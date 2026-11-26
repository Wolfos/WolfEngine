using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering;

/// <summary>
/// Declarations several passes share, so a node that lights with the sky, or is shadowed by the clouds, declares
/// the same set of resources whichever pass adds it.
/// </summary>
internal static class ViewResourceReads
{
	/// <summary>
	/// What lighting with the sky binds: the sky's environment and its atmosphere LUTs, the image-based lighting
	/// derived from it, and the shared BRDF LUT. Each is declared only when this frame has it.
	/// </summary>
	public static RenderGraphBuilder ReadSkyLighting(
		this RenderGraphBuilder builder,
		in RenderViewResources resources,
		in RenderFrameSharedResources sharedResources)
	{
		if (resources.SkyEnvironment.ViewEnvironment.IsValid)
		{
			builder.ReadTexture(resources.SkyEnvironment.ViewEnvironment, ResourceState.ShaderResource);
		}

		var atmosphere = resources.Atmosphere;
		if (atmosphere.Transmittance.IsValid)
		{
			builder.ReadTexture(atmosphere.Transmittance, ResourceState.ShaderResource);
			builder.ReadTexture(atmosphere.SkyView, ResourceState.ShaderResource);
			builder.ReadTexture(atmosphere.AerialPerspective, ResourceState.ShaderResource);
		}

		if (resources.SkyboxIrradiance.IsValid)
		{
			builder.ReadTexture(resources.SkyboxIrradiance, ResourceState.ShaderResource);
		}

		if (resources.SkyboxPrefilter.IsValid)
		{
			builder.ReadTexture(resources.SkyboxPrefilter, ResourceState.ShaderResource);
		}

		if (sharedResources.SkyboxBrdfLut.IsValid)
		{
			builder.ReadTexture(sharedResources.SkyboxBrdfLut, ResourceState.ShaderResource);
		}

		return builder;
	}

	/// <summary>The cloud shadow map, when this frame draws clouds that cast shadows.</summary>
	public static RenderGraphBuilder ReadCloudShadow(this RenderGraphBuilder builder, in RenderViewResources resources)
	{
		var clouds = resources.Clouds;
		if (clouds.Enabled && clouds.CastsShadows)
		{
			builder.ReadTexture(clouds.ShadowMap, ResourceState.ShaderResource);
		}

		return builder;
	}
}
