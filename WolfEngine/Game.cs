using System.IO;
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
		var meshPath = Path.Combine(AppContext.BaseDirectory, "Models", "Monkey.obj");
		var mesh = new Mesh(meshPath);
		_renderer.LoadMesh(mesh, "default.slang");
		_renderer.Run();
	}
}
