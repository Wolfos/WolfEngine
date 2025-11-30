using System.Diagnostics;
using System.Numerics;
using WolfEngine.ECS;
using WolfEngine.Rendering;
using WolfEngine.Importing;
using WolfEngine.Input;
using WolfEngine.Rendering.UI;
using WolfEngine.TestGame;

namespace WolfEngine;

public class Game
{
    private readonly IMaterialFactory _materialFactory;
    private readonly IThreeDFileImporter _fileImporter;
    private readonly IRenderCommandFactory _renderCommandFactory;
    private readonly RenderGraph _renderGraph;
    private readonly IInputSystem _inputSystem;
    private readonly ImGuiUiSystem _imguiSystem;
    
    private Thread _gameThread = null!;
    private volatile bool _running;
    private readonly ManualResetEventSlim _worldReady = new(false);
    
    private World _world;
    private Entity _camera;
    private Entity _monkey;

    private readonly Stopwatch _frameStopwatch = Stopwatch.StartNew();
    private readonly Stopwatch _statsStopwatch = Stopwatch.StartNew();
    private double _frameTimeAccumulatorMs;
    private double _maxFrameTimeMs;
    private int _frameCount;

    private CameraMoverSystem _cameraMoverSystem;
    
    public Game(
        IMaterialFactory materialFactory,
        IThreeDFileImporter fileImporter,
        IRenderCommandFactory renderCommandFactory,
        RenderGraph renderGraph, IInputSystem inputSystem, ImGuiUiSystem imguiSystem)
    {
        _materialFactory = materialFactory;
        _fileImporter = fileImporter ?? throw new ArgumentNullException(nameof(fileImporter));
        _renderCommandFactory = renderCommandFactory ?? throw new ArgumentNullException(nameof(renderCommandFactory));
        _renderGraph = renderGraph;
        _inputSystem = inputSystem;
        _imguiSystem = imguiSystem ?? throw new ArgumentNullException(nameof(imguiSystem));
    }

    public void Run()
    {
        _running = true;

        _gameThread = new Thread(GameLoop) {IsBackground = true, Name = "GameThread"};
        _gameThread.Start();

        _renderGraph.Startup(Startup, delta => { });

        _running = false;
        _gameThread.Join();
    }

    private void GameLoop()
    {
        var stopwatch = Stopwatch.StartNew();
        var last = stopwatch.Elapsed;
        while (_running)
        {
            _worldReady.Wait();

            var now = stopwatch.Elapsed;
            var deltaTime = (float) (now - last).TotalSeconds;
            last = now;

            Update(deltaTime);

            Thread.Sleep(0);
        }
    }

    private void Update(float deltaTime)
    {
        var frameTimeMs = _frameStopwatch.Elapsed.TotalMilliseconds;
        _frameStopwatch.Restart();

        _frameTimeAccumulatorMs += frameTimeMs;
        _maxFrameTimeMs = Math.Max(_maxFrameTimeMs, frameTimeMs);
        _frameCount++;

        if (_statsStopwatch.Elapsed.TotalSeconds >= 2)
        {
             var averageFrameTimeMs = _frameCount > 0 ? _frameTimeAccumulatorMs / _frameCount : 0.0;
             Console.Out.WriteLine($"Frame timing (last 2s): avg {averageFrameTimeMs:F2}ms | max {_maxFrameTimeMs:F2}ms");
            
            //Profiler.Report();

            _frameTimeAccumulatorMs = 0.0;
            _maxFrameTimeMs = 0.0;
            _frameCount = 0;
            _statsStopwatch.Restart();
        }
        
        _cameraMoverSystem.Update(deltaTime);
        
        foreach (var entry in _world.View<Transform, Rotator>())
        {
            ref var transform = ref entry.First;
            ref var rotator = ref entry.Second;
            
            rotator.CurrentRotation += deltaTime * rotator.RotationSpeed;
            transform.LocalRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, rotator.CurrentRotation); 
        }
        PublishSnapshot();

        _imguiSystem.NewFrame(deltaTime, _renderGraph.GetFrameBufferSize());
        _imguiSystem.RunGui(GUI.Draw);
    }

    private void Startup()
    {
        CreateWorld();
        _worldReady.Set();
    }

    private void CreateWorld()
    {
        _world = new();

        // Camera
        _cameraMoverSystem = new(_inputSystem, _world);

        var (cameraComponent, cameraTransform) = CreateCamera();
        
        _camera = _world.CreateEntity();
        _world.AddComponent(_camera, cameraComponent);
        _world.AddComponent(_camera, cameraTransform);
        _world.AddComponent(_camera, new CameraMover());

        // Light
        var light = _world.CreateEntity();
        var lightTransform =
            new Transform(Vector3.Zero, Quaternion.CreateFromAxisAngle(Vector3.UnitX, 130), Vector3.One);
        var directionalLight = new Light
        {
            Type = LightType.Directional,
            Intensity = 1.0f,
            Color = new (1.0f, 1.0f, 1.0f, 1.0f)
        };
        _world.AddComponent(light, lightTransform);
        _world.AddComponent(light, directionalLight);
        
        // Scene
        var meshPath = Path.Combine(AppContext.BaseDirectory, "Models", "BlenderScene.gltf");
        var scene = _fileImporter.Import(meshPath);
        foreach (var importedMesh in scene.Meshes)
        {
            var entity = _world.CreateEntity();
            var transform = importedMesh.Transform;

            var importedMaterial = scene.Materials[importedMesh.MaterialIndex];

            var material = _materialFactory.GetMaterial("gbuffer.slang", importedMaterial.BaseColor);

            _renderGraph.SubmitCommand(_renderCommandFactory.CreateMesh(importedMesh.Mesh));
            var meshRenderer = new MeshRenderer
            {
                Mesh = importedMesh.Mesh,
                Material = material
            };

            var rotator = new Rotator
            {
                RotationSpeed = 0.5f
            };
            
            _world.AddComponent(entity, transform);
            _world.AddComponent(entity, meshRenderer);
            //_world.AddComponent(entity, rotator);
        }
    }

    private void PublishSnapshot()
    {
        // Pick first camera if any
        Transform cameraTransform = default;
        Camera camera = default;
        var hasCamera = false;
        foreach (var entry in _world.View<Transform, Camera>())
        {
            cameraTransform = entry.First;
            camera = entry.Second;
            hasCamera = true;
            break;
        }

        if (hasCamera == false)
        {
            return;
        }

        var snapshot = _renderGraph.BeginSnapshotWrite();
        snapshot.SetCamera(camera, cameraTransform);

        foreach (var entry in _world.View<Transform, MeshRenderer>())
        {
            ref var transform = ref entry.First;
            ref var meshRenderer = ref entry.Second;
            var transformMatrix = transform.GetTransform();
            snapshot.AddDraw(meshRenderer.Mesh, meshRenderer.Material, transformMatrix);
        }

        foreach (var entry in _world.View<Transform, Light>())
        {
            ref var transform = ref entry.First;
            ref var light = ref entry.Second;
            snapshot.AddLight(light, transform);
        }

        _renderGraph.PublishSnapshot();
    }

    public ImGuiUiSystem GetImGuiSystem() => _imguiSystem;

    private static (Camera, Transform) CreateCamera()
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

        var cameraPosition = new Vector3(0.0f, 1.0f, -5.0f);
        var target = Vector3.Zero;
        var up = Vector3.UnitY;
        
        var view = CreateLookAtLeftHanded(cameraPosition, target, up);
        Matrix4x4.Invert(view, out var world);

        var transform = new Transform(world);

        return (camera, transform);
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
