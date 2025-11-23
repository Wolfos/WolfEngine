using System.Linq;
using System.Numerics;
using WolfEngine.ECS;
using WolfEngine.Rendering;
using WolfEngine.Importing;
using WolfEngine.TestGame;

namespace WolfEngine;

public class Game
{
    private readonly IMaterialFactory _materialFactory;
    private readonly IThreeDFileImporter _fileImporter;
    private readonly IRenderCommandFactory _renderCommandFactory;
    private readonly RenderGraph _renderGraph;
    
    private World _world;
    private Entity _camera;
    private Entity _monkey;
    
    public Game(
        IMaterialFactory materialFactory,
        IThreeDFileImporter fileImporter,
        IRenderCommandFactory renderCommandFactory,
        RenderGraph renderGraph)
    {
        _materialFactory = materialFactory;
        _fileImporter = fileImporter ?? throw new ArgumentNullException(nameof(fileImporter));
        _renderCommandFactory = renderCommandFactory ?? throw new ArgumentNullException(nameof(renderCommandFactory));
        _renderGraph = renderGraph;

        _renderGraph.Startup(Startup, Update);
    }

    private void Update(float deltaTime)
    {
        foreach (var entry in _world.View<Transform, Rotator>())
        {
            ref var transform = ref entry.First;
            ref var rotator = ref entry.Second;
            
            rotator.CurrentRotation += deltaTime * rotator.RotationSpeed;
            transform.LocalRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, rotator.CurrentRotation); 
        }
        
        // TODO: Probably only want one camera per render pass
        // TODO: Move these two loops to render pass
        foreach (var entry in _world.View<Transform, Camera>())
        {
            ref var transform = ref entry.First;
            ref var camera = ref entry.Second;
            
            var command = _renderCommandFactory.SetCamera(ref camera, ref transform);
            _renderGraph.SubmitCommand(command);
        }
        
        
        foreach (var entry in _world.View<Transform, MeshRenderer>())
        {
            ref var transform = ref entry.First;
            ref var meshRenderer = ref entry.Second;

            var transformMatrix = transform.GetTransform();
            
            var command = _renderCommandFactory.DrawMesh(ref meshRenderer, ref transformMatrix);
            _renderGraph.SubmitCommand(command);
        }
    }

    private void Startup()
    {
        CreateWorld();
    }

    private void CreateWorld()
    {
        _world = new();

        var (cameraComponent, cameraTransform) = CreateCamera();
        
        _camera = _world.CreateEntity();
        _world.AddComponent(_camera, cameraComponent);
        _world.AddComponent(_camera, cameraTransform);
        
        var meshPath = Path.Combine(AppContext.BaseDirectory, "Models", "DamagedHelmet.gltf");
        var scene = _fileImporter.Import(meshPath);
        foreach (var importedMesh in scene.Meshes)
        {
            
            var entity = _world.CreateEntity();
            var transform = importedMesh.Transform;

            var mat = _materialFactory.GetMaterial("gbuffer.slang", new(1.0f, 0.0f, 0.0f, 1.0f));

            _renderGraph.SubmitCommand(_renderCommandFactory.CreateMesh(importedMesh.Mesh));
            var meshRenderer = new MeshRenderer
            {
                Mesh = importedMesh.Mesh,
                Material = mat
            };

            var rotator = new Rotator
            {
                RotationSpeed = 1.0f
            };
            
            _world.AddComponent(entity, transform);
            _world.AddComponent(entity, meshRenderer);
            //_world.AddComponent(entity, rotator);
        }
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

        var cameraPosition = new Vector3(0.0f, 1.0f, -3.0f);
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
