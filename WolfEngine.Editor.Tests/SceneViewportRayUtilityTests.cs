using System.Numerics;
using WolfEngine.AssetPipeline;
using WolfEngine.Editor.UI;
using WolfEngine.ECS;
using WolfEngine.Mathematics;
using WolfEngine.Physics;
using WolfEngine.Rendering;
using WolfEngine.Rendering.UI;

namespace WolfEngine.Editor.Tests;

[TestFixture]
public sealed class SceneViewportRayUtilityTests
{
	[TearDown]
	public void TearDown()
	{
		AssetDatabase.ClearInstanceRegistry();
	}

	[Test]
	public void TryBuildWorldRay_CenterViewportPointsTowardScene()
	{
		var camera = CreateCamera(800, 600, 70.0f);
		var cameraWorldTransform = CreateCameraWorldTransform(new Vector3(0.0f, 5.0f, 0.0f), Vector3.Zero, Vector3.UnitZ);
		var viewportState = new SceneViewportUiState(
			visible: true,
			contentSizePixels: new Int2(800, 600),
			resolutionScale: 1.0f,
			requestedDebugViewId: SceneDebugViewIds.FinalColor,
			hovered: true,
			focused: true,
			rightMousePressStartedHere: false,
			imageMin: Vector2.Zero,
			imageMax: new Vector2(800.0f, 600.0f));

		var builtInverse = SceneViewportRayUtility.TryBuildInverseViewProjection(camera, cameraWorldTransform, out var inverseViewProjection);
		var builtRay = SceneViewportRayUtility.TryBuildWorldRay(viewportState, new Vector2(400.0f, 300.0f), inverseViewProjection, out var ray);

		Assert.That(builtInverse, Is.True);
		Assert.That(builtRay, Is.True);
		Assert.That(ray.Direction.Y, Is.LessThan(-0.8f));
		Assert.That(MathF.Abs(ray.Direction.X), Is.LessThan(0.1f));
		Assert.That(MathF.Abs(ray.Direction.Z), Is.LessThan(0.1f));
	}

	[Test]
	[Explicit("Exercises native Jolt terrain heightfield integration.")]
	public void TryRaycast_FromViewportRay_HitsTerrainWithoutMeshColliderAuthoring()
	{
		using var registry = new TestAssetRegistry();
		var heightmapId = Guid.NewGuid();
		registry.Register(heightmapId, CreateHeightTexture("terrain-flat", 5, 5, 0));

		var world = new World(WorldTag.Authoring);
		var terrain = world.CreateEntity("Terrain", Matrix4x4.Identity);
		world.AddComponent(terrain, new TerrainComponent
		{
			HeightmapAsset = new AssetRef<Texture> { NodeId = heightmapId },
			WorldSizeMeters = new Vector2(4.0f, 4.0f),
			HeightScaleMeters = 4.0f,
			ChunkSizeMeters = 4.0f,
			LodCount = 3,
			Lod0ResolutionInQuads = 4,
			LodDistancesMeters = [120.0f, 320.0f]
		});

		var camera = CreateCamera(800, 600, 70.0f);
		var cameraWorldTransform = CreateCameraWorldTransform(new Vector3(0.0f, 5.0f, 0.0f), Vector3.Zero, Vector3.UnitZ);
		var viewportState = new SceneViewportUiState(
			visible: true,
			contentSizePixels: new Int2(800, 600),
			resolutionScale: 1.0f,
			requestedDebugViewId: SceneDebugViewIds.FinalColor,
			hovered: true,
			focused: true,
			rightMousePressStartedHere: false,
			imageMin: Vector2.Zero,
			imageMax: new Vector2(800.0f, 600.0f));
		Assert.That(SceneViewportRayUtility.TryBuildInverseViewProjection(camera, cameraWorldTransform, out var inverseViewProjection), Is.True);
		Assert.That(SceneViewportRayUtility.TryBuildWorldRay(viewportState, new Vector2(400.0f, 300.0f), inverseViewProjection, out var ray), Is.True);

		using var physics = new RigidbodySystem();
		var hitSomething = physics.TryRaycast(world, ray.Origin, ray.Direction * 20.0f, out var hit);

		Assert.That(hitSomething, Is.True);
		Assert.That(hit.Entity, Is.EqualTo(terrain));
		Assert.That(hit.Point.Y, Is.EqualTo(0.0f).Within(0.05f));
	}

	private static Camera CreateCamera(int width, int height, float fov)
	{
		var camera = new Camera
		{
			ScreenResolution = new Int2(width, height)
		};
		camera.SetPerspective(fov);
		return camera;
	}

	private static WorldTransform CreateCameraWorldTransform(Vector3 position, Vector3 target, Vector3 up)
	{
		var view = CreateLookAtLeftHanded(position, target, up);
		Matrix4x4.Invert(view, out var localToWorld);
		return new WorldTransform
		{
			LocalToWorld = localToWorld,
			WorldToLocal = view
		};
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

	private static Texture CreateHeightTexture(string name, int width, int height, byte normalizedHeight)
	{
		var data = new byte[width * height * 4];
		for (var i = 0; i < width * height; i++)
		{
			var offset = i * 4;
			data[offset] = normalizedHeight;
			data[offset + 1] = 0;
			data[offset + 2] = 0;
			data[offset + 3] = 255;
		}

		return new Texture(name, width, height, false, TextureFormat.Rgba8Unorm, [new TextureMipData(width, height, data)]);
	}

	private sealed class TestAssetRegistry : IAssetInstanceRegistry, IDisposable
	{
		private readonly Dictionary<Guid, object> _assets = new();

		public TestAssetRegistry()
		{
			AssetDatabase.SetInstanceRegistry(this);
		}

		public void Register(Guid assetId, object asset)
		{
			_assets[assetId] = asset;
		}

		public object? GetInstance(Guid assetId, Type expectedType)
		{
			if (_assets.TryGetValue(assetId, out var asset) == false)
			{
				return null;
			}

			return expectedType.IsInstanceOfType(asset) ? asset : null;
		}

		public void RefreshProject(string projectRootPath, AssetDatabase database)
		{
		}

		public void InvalidateAssets(IEnumerable<Guid> assetIds)
		{
			foreach (var assetId in assetIds)
			{
				_assets.Remove(assetId);
			}
		}

		public void ClearCachedInstances()
		{
			_assets.Clear();
		}

		public void Clear()
		{
			_assets.Clear();
		}

		public void Dispose()
		{
			AssetDatabase.ClearInstanceRegistry();
		}
	}
}
