using System.Diagnostics;
using System.Numerics;
using WolfEngine.ECS;
using WolfEngine.Rendering;
using WolfEngine.Importing;
using WolfEngine.Input;
using WolfEngine.Rendering.UI;
using WolfEngine.Editor;

namespace WolfEngine;

public class Game
{
    private readonly IMaterialFactory _materialFactory;
    private readonly IThreeDFileImporter _fileImporter;
    private readonly RenderGraph _renderGraph;
    private readonly IRenderer _renderer;
    private readonly IInputSystem _inputSystem;
    private readonly IUiFrameProvider _imguiSystem;
    private readonly ITextureFactory _textureFactory;
    private readonly SkyboxRenderer _skyboxRenderer;
    
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
        RenderGraph renderGraph, IRenderer renderer, IInputSystem inputSystem, IUiFrameProvider uiFrameProvider,
        ITextureFactory textureFactory, SkyboxRenderer skyboxRenderer)
    {
        _materialFactory = materialFactory;
        _fileImporter = fileImporter ?? throw new ArgumentNullException(nameof(fileImporter));
        _renderGraph = renderGraph;
        _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
        _inputSystem = inputSystem;
        _imguiSystem = uiFrameProvider ?? throw new ArgumentNullException(nameof(uiFrameProvider));
        _textureFactory = textureFactory ?? throw new ArgumentNullException(nameof(textureFactory));
        _skyboxRenderer = skyboxRenderer ?? throw new ArgumentNullException(nameof(skyboxRenderer));
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
        
        PublishSnapshot();

        _imguiSystem.NewFrame(deltaTime, _renderer.GetWindowSize(), _renderGraph.GetFrameBufferSize());
        _imguiSystem.RunGui(EditorGui.Draw, _world);
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
        
        _camera = _world.CreateEntity("Camera");
        _world.AddComponent(_camera, cameraComponent);
        _world.AddComponent(_camera, cameraTransform);
        _world.AddComponent(_camera, new CameraMover());

        // Light
        var light = _world.CreateEntity("Directional Light");
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
        var meshPath = Path.Combine(AppContext.BaseDirectory, "Assets", "test.glb");
        var scene = _fileImporter.Import(meshPath);
        var runtimeTextures = scene.Textures.Select(_textureFactory.GetTexture).ToList();
        LoadSkybox();
        foreach (var importedMesh in scene.Meshes)
        {
            var entity = _world.CreateEntity(importedMesh.Name);
            var transform = importedMesh.Transform;

            var importedMaterial = scene.Materials[importedMesh.MaterialIndex];
            Texture albedoTexture = null;
            Texture metallicRoughnessTexture = null;
            Texture normalTexture = null;
            Texture emissiveTexture = null;
            Texture occlusionTexture = null;
            if (importedMaterial.BaseColorTextureIndex is { } texIndex &&
                texIndex >= 0 &&
                texIndex < runtimeTextures.Count)
            {
                albedoTexture = runtimeTextures[texIndex];
            }
            if (importedMaterial.MetallicRoughnessTextureIndex is { } mrIndex &&
                mrIndex >= 0 &&
                mrIndex < runtimeTextures.Count)
            {
                metallicRoughnessTexture = runtimeTextures[mrIndex];
            }
            if (importedMaterial.NormalTextureIndex is { } normalIndex &&
                normalIndex >= 0 &&
                normalIndex < runtimeTextures.Count)
            {
                normalTexture = runtimeTextures[normalIndex];
            }
            if (importedMaterial.EmissiveTextureIndex is { } emissiveIndex &&
                emissiveIndex >= 0 &&
                emissiveIndex < runtimeTextures.Count)
            {
                emissiveTexture = runtimeTextures[emissiveIndex];
            }
            if (importedMaterial.OcclusionTextureIndex is { } occlusionIndex &&
                occlusionIndex >= 0 &&
                occlusionIndex < runtimeTextures.Count)
            {
                occlusionTexture = runtimeTextures[occlusionIndex];
            }

            var material = _materialFactory.GetMaterial(
                "gbuffer.slang",
                importedMaterial.BaseColor,
                importedMaterial.MetallicFactor,
                importedMaterial.RoughnessFactor,
                albedoTexture,
                metallicRoughnessTexture,
                normalTexture,
                emissiveTexture,
                occlusionTexture);

            _renderGraph.EnsureMeshResources(importedMesh.Mesh);
            var meshRenderer = new MeshRenderer
            {
                Mesh = importedMesh.Mesh,
                Material = material
            };
            
            _world.AddComponent(entity, transform);
            _world.AddComponent(entity, meshRenderer);
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

    private void LoadSkybox()
    {
        var envPath = Path.Combine(AppContext.BaseDirectory, "Assets", "shanghai_bund_1k.hdr");
        var envTexture = _textureFactory.LoadFromFile(envPath, isSrgb: false);

        var skybox = _skyboxRenderer.CreateSkyboxResources(envTexture);
        _renderGraph.SetSkybox(skybox);
    }
    
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
