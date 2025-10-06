using System;
using System.IO;
using System.Numerics;

namespace WolfEngine;

public class Game
{
    private readonly IRenderer _renderer;
    private readonly IShaderCompiler _shaderCompiler;

    private Mesh? _mesh;
    private Material? _material;
    private bool _initialized;

    public Game(IRenderer renderer, IShaderCompiler shaderCompiler)
    {
        _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
        _shaderCompiler = shaderCompiler ?? throw new ArgumentNullException(nameof(shaderCompiler));
    }

    public void Update()
    {
        if (_initialized == false)
        {
            InitializeContent();
        }

        if (_mesh is null || _material is null)
        {
            return;
        }

        _renderer.SubmitCommand(RenderCommand.DrawMesh(_mesh, _material, Matrix4x4.Identity));
    }

    private void InitializeContent()
    {
        var meshPath = Path.Combine(AppContext.BaseDirectory, "Models", "Monkey.obj");
        _mesh = new Mesh(meshPath);
        _material = new Material(_shaderCompiler, "default.slang", new Vector4(1.0f, 0.0f, 0.0f, 1.0f));

        _renderer.SubmitCommand(RenderCommand.CreateMesh(_mesh));
        _renderer.SubmitCommand(RenderCommand.CreateMaterial(_material));

        _initialized = true;
    }
}
