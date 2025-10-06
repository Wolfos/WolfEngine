using System.Numerics;

namespace WolfEngine;

public class Game
{
    private readonly IRenderer _renderer;
    private readonly IShaderCompiler _shaderCompiler;

    private Mesh _mesh = null!;
    private Material _material = null!;

    public Game(IRenderer renderer, IShaderCompiler shaderCompiler)
    {
        _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
        _shaderCompiler = shaderCompiler ?? throw new ArgumentNullException(nameof(shaderCompiler));
    }

    public void Run()
    {
        var meshPath = Path.Combine(AppContext.BaseDirectory, "Models", "Monkey.obj");
        _mesh = new Mesh(meshPath);
        _material = new Material(_shaderCompiler, "default.slang", new Vector4(1, 0, 0, 1));

        _renderer.SubmitCommand(RenderCommand.CreateMesh(_mesh));
        _renderer.SubmitCommand(RenderCommand.CreateMaterial(_material));
        _renderer.Run(Update);
    }

    private void Update()
    {
        _renderer.SubmitCommand(RenderCommand.DrawMesh(_mesh, _material, Matrix4x4.Identity));
    }
}
