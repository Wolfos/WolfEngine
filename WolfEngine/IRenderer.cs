namespace WolfEngine;

public interface IRenderer
{
	void LoadMesh(Mesh mesh, string shaderPath);
	void Run();
}
