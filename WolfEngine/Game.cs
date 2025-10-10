using System.Diagnostics;
using System.Numerics;

namespace WolfEngine;

public class Game
{
    private readonly IRenderer _renderer;
    private readonly IShaderCompiler _shaderCompiler;

    private Mesh _mesh;
    private Material _material;
    private Camera _camera;
    private bool _initialized;
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();

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

        var time = (float)_stopwatch.Elapsed.TotalSeconds;
        var transform = Matrix4x4.CreateRotationY(time * 0.5f);

        _renderer.SubmitCommand(RenderCommand.DrawMesh(_mesh!, _material!, transform));
    }

    private void InitializeContent()
    {
        var meshPath = Path.Combine(AppContext.BaseDirectory, "Models", "Monkey.obj");
        _mesh = new Mesh(meshPath);
        _material = new Material(_shaderCompiler, "default.slang", new Vector4(1.0f, 0.0f, 0.0f, 1.0f));
        _camera = CreateCamera();

        _renderer.SubmitCommand(RenderCommand.CreateMesh(_mesh));
        _renderer.SubmitCommand(RenderCommand.CreateMaterial(_material));
        _renderer.SubmitCommand(RenderCommand.SetCamera(_camera));

        _initialized = true;
    }

    private static Camera CreateCamera()
    {
        const int screenWidth = 1280;
        const int screenHeight = 720;
        const float fieldOfView = 70.0f;

        var camera = new Camera
        {
            ScreenResolutionX = screenWidth,
            ScreenResolutionY = screenHeight
        };
        camera.SetPerspective(fieldOfView);

        var cameraPosition = new Vector3(0.0f, 0.0f, -5.0f);
        var target = Vector3.Zero;
        var up = Vector3.UnitY;
        camera.Transform = CreateLookAtLeftHanded(cameraPosition, target, up);
        camera.Position = cameraPosition;

        return camera;
    }

    private static Matrix4x4 CreateLookAtLeftHanded(Vector3 position, Vector3 target, Vector3 up)
    {
        var zAxis = Vector3.Normalize(target - position);
        var xAxis = Vector3.Normalize(Vector3.Cross(up, zAxis));
        var yAxis = Vector3.Cross(zAxis, xAxis);

        return new Matrix4x4(
            xAxis.X, yAxis.X, zAxis.X, 0.0f,
            xAxis.Y, yAxis.Y, zAxis.Y, 0.0f,
            xAxis.Z, yAxis.Z, zAxis.Z, 0.0f,
            -Vector3.Dot(xAxis, position),
            -Vector3.Dot(yAxis, position),
            -Vector3.Dot(zAxis, position),
            1.0f);
    }
}
