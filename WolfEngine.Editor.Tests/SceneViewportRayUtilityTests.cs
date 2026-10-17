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
			pointerAvailable: true,
			pointerCaptured: false,
			rightMousePressStartedHere: false,
			imageMin: Vector2.Zero,
			imageMax: new Vector2(800.0f, 600.0f));

		Assert.That(ViewProjection.TryCreate(cameraWorldTransform.WorldToLocal, camera.Perspective, out var viewProjection), Is.True);
		var builtRay = SceneViewportRayUtility.TryBuildWorldRay(viewportState, new Vector2(400.0f, 300.0f), viewProjection, out var ray);

		Assert.That(builtRay, Is.True);
		Assert.That(ray.Direction.Y, Is.LessThan(-0.8f));
		Assert.That(MathF.Abs(ray.Direction.X), Is.LessThan(0.1f));
		Assert.That(MathF.Abs(ray.Direction.Z), Is.LessThan(0.1f));
		Assert.That(ray.Direction.Length(), Is.EqualTo(1.0f).Within(1e-5f));
	}

	[Test]
	public void TryViewportPointToRay_UsesTopLeftCoordinatesAndAllowsExtrapolation()
	{
		var camera = CreateCamera(800, 600, 70.0f);
		var cameraWorldTransform = CreateCameraWorldTransform(new Vector3(0.0f, 5.0f, 0.0f), Vector3.Zero, Vector3.UnitZ);
		Assert.That(ViewProjection.TryCreate(cameraWorldTransform.WorldToLocal, camera.Perspective, out var viewProjection), Is.True);

		Assert.That(viewProjection.TryViewportPointToRay(new Vector2(0.5f, 0.5f), out var center), Is.True);
		Assert.That(viewProjection.TryViewportPointToRay(new Vector2(-0.25f, 1.25f), out var outside), Is.True);
		Assert.That(center.Direction.Y, Is.LessThan(-0.8f));
		Assert.That(Vector3.Distance(center.Origin, outside.Origin), Is.GreaterThan(0.0f));
	}

	[Test]
	public void TryViewportPointToRay_OrthographicRaysStartAtNearPlaneAndStayParallel()
	{
		var projection = Matrix4x4.CreateOrthographicOffCenterLeftHanded(-4.0f, 4.0f, -3.0f, 3.0f, 2.0f, 20.0f);
		Assert.That(ViewProjection.TryCreate(Matrix4x4.Identity, projection, out var viewProjection), Is.True);

		Assert.That(viewProjection.TryViewportPointToRay(new Vector2(0.5f, 0.5f), out var center), Is.True);
		Assert.That(viewProjection.TryViewportPointToRay(new Vector2(0.0f, 0.0f), out var corner), Is.True);
		Assert.That(center.Origin.Z, Is.EqualTo(2.0f).Within(1e-4f));
		Assert.That(corner.Origin.Z, Is.EqualTo(2.0f).Within(1e-4f));
		Assert.That(Vector3.Distance(center.Direction, corner.Direction), Is.LessThan(1e-5f));
		Assert.That(center.Direction, Is.EqualTo(Vector3.UnitZ));
	}

	[Test]
	public void TryViewportPointToRay_InvalidSnapshotsFail()
	{
		Assert.That(default(ViewProjection).TryViewportPointToRay(Vector2.Zero, out _), Is.False);
		Assert.That(ViewProjection.TryCreate(Matrix4x4.Identity, default, out _), Is.False);
		var nonFiniteProjection = Matrix4x4.Identity;
		nonFiniteProjection.M11 = float.NaN;
		Assert.That(ViewProjection.TryCreate(Matrix4x4.Identity, nonFiniteProjection, out _), Is.False);
	}

	[Test]
	public void TryScreenPointToRay_OffsetAndScaledRectanglesMapTheSameViewportPoint()
	{
		var camera = CreateCamera(800, 600, 70.0f);
		var cameraWorldTransform = CreateCameraWorldTransform(Vector3.Zero, Vector3.UnitZ, Vector3.UnitY);
		Assert.That(ViewProjection.TryCreate(cameraWorldTransform.WorldToLocal, camera.Perspective, out var viewProjection), Is.True);

		Assert.That(viewProjection.TryScreenPointToRay(new Vector2(220.0f, 180.0f),
			new Vector2(20.0f, 30.0f), new Vector2(820.0f, 630.0f), out var first), Is.True);
		Assert.That(viewProjection.TryScreenPointToRay(new Vector2(500.0f, 500.0f),
			new Vector2(100.0f, 200.0f), new Vector2(1700.0f, 1400.0f), out var scaled), Is.True);
		Assert.That(Vector3.Distance(first.Origin, scaled.Origin), Is.LessThan(1e-5f));
		Assert.That(Vector3.Distance(first.Direction, scaled.Direction), Is.LessThan(1e-5f));
	}

	[Test]
	public void Ray_NormalizesLargeFiniteDirectionsWithoutOverflow()
	{
		var ray = new Ray(Vector3.Zero, new Vector3(float.MaxValue, float.MaxValue, 0.0f));

		Assert.That(ray.IsValid, Is.True);
		Assert.That(ray.Direction.Length(), Is.EqualTo(1.0f).Within(1e-5f));
		Assert.That(ray.Direction.X, Is.EqualTo(ray.Direction.Y).Within(1e-5f));
	}

	[Test]
	public void TryScreenPointToRay_RejectsNonFiniteRectangleExtent()
	{
		var camera = CreateCamera(800, 600, 70.0f);
		var cameraWorldTransform = CreateCameraWorldTransform(Vector3.Zero, Vector3.UnitZ, Vector3.UnitY);
		Assert.That(ViewProjection.TryCreate(cameraWorldTransform.WorldToLocal, camera.Perspective, out var viewProjection), Is.True);

		Assert.That(
			viewProjection.TryScreenPointToRay(Vector2.Zero,
				new Vector2(-float.MaxValue, 0.0f), new Vector2(float.MaxValue, 1.0f), out _),
			Is.False);
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
			TerrainAsset = new AssetRef<TerrainAsset> { NodeId = heightmapId },
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
			pointerAvailable: true,
			pointerCaptured: false,
			rightMousePressStartedHere: false,
			imageMin: Vector2.Zero,
			imageMax: new Vector2(800.0f, 600.0f));
		Assert.That(ViewProjection.TryCreate(cameraWorldTransform.WorldToLocal, camera.Perspective, out var viewProjection), Is.True);
		Assert.That(SceneViewportRayUtility.TryBuildWorldRay(viewportState, new Vector2(400.0f, 300.0f), viewProjection, out var ray), Is.True);

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

	private static TerrainAsset CreateTerrainAssetFromHeightTexture(Texture heightTexture)
	{
		var topMip = heightTexture.MipLevels[0];
		var heightData = new byte[topMip.Width * topMip.Height * 2];
		for (var i = 0; i < topMip.Width * topMip.Height; i++)
		{
			var height = (ushort)(topMip.Data[i * 4] * 257);
			var offset = i * 2;
			heightData[offset] = (byte)(height & 0xFF);
			heightData[offset + 1] = (byte)(height >> 8);
		}

		var heightmap = new Texture(heightTexture.Name, topMip.Width, topMip.Height, false, TextureFormat.R16Unorm, [new TextureMipData(topMip.Width, topMip.Height, heightData)]);
		var indexData = new byte[topMip.Width * topMip.Height * 4];
		var weightData = new byte[topMip.Width * topMip.Height * 4];
		for (var i = 0; i < topMip.Width * topMip.Height; i++)
		{
			weightData[i * 4] = 255;
		}

		var layerMips = TerrainLayerMapUtility.GenerateLayerMipChain(
			new TextureMipData(topMip.Width, topMip.Height, indexData),
			new TextureMipData(topMip.Width, topMip.Height, weightData));
		var layerIndexMap = new Texture($"{heightTexture.Name}_layers", topMip.Width, topMip.Height, false, TextureFormat.Rgba8Uint, layerMips.Indices);
		var layerWeightMap = new Texture($"{heightTexture.Name}_weights", topMip.Width, topMip.Height, false, TextureFormat.Rgba8Unorm, layerMips.Weights);
		return new TerrainAsset(heightTexture.Name, heightmap, layerIndexMap, layerWeightMap);
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

			if (expectedType.IsInstanceOfType(asset))
			{
				return asset;
			}

			return expectedType == typeof(TerrainAsset) && asset is Texture heightTexture
				? CreateTerrainAssetFromHeightTexture(heightTexture)
				: null;
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
