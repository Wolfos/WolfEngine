using System.Numerics;
using WolfEngine.Rendering;

namespace WolfEngine;

public interface IMaterialFactory
{
	Material GetMaterial(
		string shader,
		Vector4 color,
		Texture? albedoTexture = null,
		Texture? metallicRoughnessTexture = null,
		Texture? normalTexture = null,
		Texture? emissiveTexture = null,
		Texture? occlusionTexture = null);
}

public class MaterialFactory : IMaterialFactory
{
	private readonly RenderGraph _renderGraph;
	private readonly ITextureFactory _textureFactory;

	public MaterialFactory(IShaderCompiler shaderCompiler, RenderGraph renderGraph, ITextureFactory textureFactory)
	{
		_renderGraph = renderGraph;
		_textureFactory = textureFactory;
	}

	public Material GetMaterial(
		string shader,
		Vector4 color,
		Texture? albedoTexture = null,
		Texture? metallicRoughnessTexture = null,
		Texture? normalTexture = null,
		Texture? emissiveTexture = null,
		Texture? occlusionTexture = null)
	{
		if (string.IsNullOrWhiteSpace(shader))
		{
			throw new ArgumentException("Shader path cannot be empty.", nameof(shader));
		}

		var material = new Material(shader);
		material.Color = color;
		material.AlbedoTexture = albedoTexture ?? _textureFactory.GetWhiteTexture();
		material.MetallicRoughnessTexture = metallicRoughnessTexture ?? _textureFactory.GetWhiteTexture();
		material.NormalTexture = normalTexture ?? _textureFactory.GetNeutralNormalTexture();
		material.EmissiveTexture = emissiveTexture ?? _textureFactory.GetBlackTexture();
		material.OcclusionTexture = occlusionTexture ?? _textureFactory.GetWhiteTexture();
		material.Resources = _renderGraph.EnsureMaterialResources(material);

		return material;
	}
}
