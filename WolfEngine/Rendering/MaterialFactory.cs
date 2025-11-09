using System.Numerics;
using WolfEngine.Rendering;

namespace WolfEngine;

public interface IMaterialFactory
{
	public Material GetMaterial(string shader, Vector4 color);
}

public class MaterialFactory : IMaterialFactory
{
	private readonly RenderGraph _renderGraph;
	public MaterialFactory(IShaderCompiler shaderCompiler, RenderGraph renderGraph)
	{
		_renderGraph = renderGraph;
	}

	public Material GetMaterial(string shader, Vector4 color)
	{
		if (string.IsNullOrWhiteSpace(shader))
		{
			throw new ArgumentException("Shader path cannot be empty.", nameof(shader));
		}

		var material = new Material(shader);
		material.Color = color;
		material.Resources = _renderGraph.EnsureMaterialResources(material);

		return material;
	}
}
