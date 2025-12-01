using System.Numerics;
using WolfEngine.Rendering;

namespace WolfEngine;

public interface IMaterialFactory
{
	Material GetMaterial(string shader, Vector4 color, Texture? albedoTexture = null);
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

	public Material GetMaterial(string shader, Vector4 color, Texture? albedoTexture = null)
	{
		if (string.IsNullOrWhiteSpace(shader))
		{
			throw new ArgumentException("Shader path cannot be empty.", nameof(shader));
		}

		var material = new Material(shader);
		material.Color = color;
		material.AlbedoTexture = albedoTexture ?? _textureFactory.GetWhiteTexture();
		material.Resources = _renderGraph.EnsureMaterialResources(material);

		return material;
	}
}
