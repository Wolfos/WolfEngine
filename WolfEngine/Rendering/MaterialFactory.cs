using System;
using System.Numerics;
using WolfEngine.Rendering;
using WolfEngine.Rendering.Shaders;

namespace WolfEngine;

public interface IMaterialFactory
{
	Material GetMaterial(
		string shader,
		ColorRGBA color,
		float metallicFactor = 1.0f,
		float roughnessFactor = 1.0f,
		float normalScale = 1.0f,
		Vector3? emissiveFactor = null,
		float emissiveIntensity = 1.0f,
		Texture albedoTexture = null,
		Texture ormTexture = null,
		Texture normalTexture = null,
		Texture emissiveTexture = null,
		AlphaMode alphaMode = AlphaMode.Opaque,
		float alphaCutoff = 0.5f);
}

public class MaterialFactory : IMaterialFactory
{
	private readonly RenderGraph _renderGraph;
	private readonly ITextureFactory _textureFactory;

	public MaterialFactory(IShaderProvider shaderCompiler, RenderGraph renderGraph, ITextureFactory textureFactory)
	{
		_renderGraph = renderGraph;
		_textureFactory = textureFactory;
	}

	public Material GetMaterial(
		string shader,
		ColorRGBA color,
		float metallicFactor = 1.0f,
		float roughnessFactor = 1.0f,
		float normalScale = 1.0f,
		Vector3? emissiveFactor = null,
		float emissiveIntensity = 1.0f,
		Texture albedoTexture = null,
		Texture ormTexture = null,
		Texture normalTexture = null,
		Texture emissiveTexture = null,
		AlphaMode alphaMode = AlphaMode.Opaque,
		float alphaCutoff = 0.5f)
	{
		if (string.IsNullOrWhiteSpace(shader))
		{
			throw new ArgumentException("Shader path cannot be empty.", nameof(shader));
		}

		var material = new Material(shader)
		{
			Color = color,
			MetallicFactor = metallicFactor,
			RoughnessFactor = roughnessFactor,
			NormalScale = normalScale,
			EmissiveFactor = emissiveFactor ?? Vector3.Zero,
			EmissiveIntensity = Math.Max(0.0f, emissiveIntensity),
			AlbedoTexture = albedoTexture ?? _textureFactory.GetWhiteTexture(),
			OrmTexture = ormTexture ?? _textureFactory.GetWhiteTexture(),
			NormalTexture = normalTexture ?? _textureFactory.GetNeutralNormalTexture(),
			EmissiveTexture = emissiveTexture ?? _textureFactory.GetWhiteTexture(),
			AlphaMode = alphaMode,
			AlphaCutoff = alphaCutoff
		};
		_renderGraph.EnsureMaterialResources(material);

		return material;
	}
}
