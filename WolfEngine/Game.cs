using System.Numerics;

namespace WolfEngine;

public class Game
{
	private readonly IRenderer _renderer;

	public Game(IRenderer renderer)
	{
		_renderer = renderer;
	}

	public void Run()
	{
		var vertices = new[]
		{
			new Vector4(-0.5f, -0.5f, 0.0f, 1.0f),
			new Vector4( 0.5f, -0.5f, 0.0f, 1.0f),
			new Vector4(-0.5f,  0.5f, 0.0f, 1.0f),
			new Vector4( 0.5f,  0.5f, 0.0f, 1.0f)
		};

		var indices = new uint[]
		{
			0, 1, 2,
			2, 1, 3
		};

		var mesh = new Mesh(vertices, indices);
		_renderer.LoadMesh(mesh, "default.slang");
		_renderer.Run();
	}
}
