namespace WolfEngine;

public interface IMaterialFactory
{
	public Material GetMaterial(string shader);
}

public class MaterialFactory : IMaterialFactory
{
	private readonly IShaderCompiler _shaderCompiler;

	public MaterialFactory(IShaderCompiler shaderCompiler)
	{
		_shaderCompiler = shaderCompiler;
	}

	public Material GetMaterial(string shader)
	{
		return new Material(_shaderCompiler, shader);
	}
}