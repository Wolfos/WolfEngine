using System.Numerics;
using System.Reflection;
using Moq;
using WolfEngine.AssetPipeline;
using WolfEngine.Mathematics;
using WolfEngine.Rendering;

namespace WolfEngine.Tests;

[TestFixture]
public sealed class TerrainTests
{
	[TearDown]
	public void TearDown()
	{
		AssetDatabase.ClearInstanceRegistry();
	}

	[Test]
	public void TerrainLayerSet_SupportsMoreThanFourLayers()
	{
		var layerSet = new TerrainLayerSet
		{
			ActiveLayerCount = TerrainLayerSet.MaxLayerCount
		};
		layerSet.EnsureLayerCapacity(TerrainLayerSet.MaxLayerCount);
		layerSet.Layers[TerrainLayerSet.MaxLayerCount - 1].Scale = 48.0f;

		Assert.That(layerSet.ResolvedLayerCount, Is.EqualTo(TerrainLayerSet.MaxLayerCount));
		Assert.That(layerSet.GetLayer(TerrainLayerSet.MaxLayerCount - 1).Scale, Is.EqualTo(48.0f).Within(0.0001f));
	}

	[Test]
	public void DecodeHeightSamples_UsesTopMipRedChannel()
	{
		var texture = new Texture(
			"height",
			2,
			2,
			isSrgb: false,
			TextureFormat.Rgba8Unorm,
			[
				new TextureMipData(2, 2, [10, 100, 200, 255, 20, 0, 0, 255, 30, 0, 0, 255, 40, 0, 0, 255]),
				new TextureMipData(1, 1, [255, 0, 0, 255])
			]);

		var decodeMethod = typeof(TerrainRuntimeData).GetMethod("DecodeHeightSamples", BindingFlags.NonPublic | BindingFlags.Static)
			?? throw new AssertionException("DecodeHeightSamples method was not found.");
		var parameters = new object?[] { texture, 0, 0 };
		var result = (float[]?)decodeMethod.Invoke(null, parameters);

		Assert.That(result, Is.Not.Null);
		Assert.That((int)parameters[1]!, Is.EqualTo(2));
		Assert.That((int)parameters[2]!, Is.EqualTo(2));
		Assert.That(result!, Has.Length.EqualTo(4));
		Assert.That(result[0], Is.EqualTo(10.0f / 255.0f).Within(0.0001f));
		Assert.That(result[1], Is.EqualTo(20.0f / 255.0f).Within(0.0001f));
		Assert.That(result[2], Is.EqualTo(30.0f / 255.0f).Within(0.0001f));
		Assert.That(result[3], Is.EqualTo(40.0f / 255.0f).Within(0.0001f));
	}

	[Test]
	public void EnsureBuilt_GeneratesExpectedChunkMeshesAndLods()
	{
		using var registry = new TestAssetRegistry();
		var heightmapId = Guid.NewGuid();
		registry.Register(heightmapId, CreateHeightTexture("height-5x5", 5, 5));

		var component = new TerrainComponent
		{
			TerrainAsset = new AssetRef<TerrainAsset> { NodeId = heightmapId },
			WorldSizeMeters = new Vector2(64.0f, 64.0f),
			HeightScaleMeters = 16.0f,
			ChunkSizeMeters = 64.0f,
			LodCount = 3,
			Lod0ResolutionInQuads = 4,
			LodDistancesMeters = [120.0f, 320.0f]
		};
		var runtime = new TerrainRuntimeData();

		Assert.That(runtime.EnsureBuilt(component), Is.True);
		Assert.That(runtime.HeightSampleWidth, Is.EqualTo(5));
		Assert.That(runtime.HeightSampleHeight, Is.EqualTo(5));
		Assert.That(runtime.Chunks, Has.Count.EqualTo(1));

		Assert.That(runtime.SharedLodMeshes, Has.Count.EqualTo(3));
		Assert.That(runtime.SharedLodMeshes[0].Vertices, Has.Length.EqualTo(45));
		Assert.That(runtime.SharedLodMeshes[0].Indices, Has.Length.EqualTo(192));
		Assert.That(runtime.SharedLodMeshes[1].Vertices, Has.Length.EqualTo(21));
		Assert.That(runtime.SharedLodMeshes[1].Indices, Has.Length.EqualTo(72));
		Assert.That(runtime.SharedLodMeshes[2].Vertices, Has.Length.EqualTo(12));
		Assert.That(runtime.SharedLodMeshes[2].Indices, Has.Length.EqualTo(30));
	}

	[Test]
	public void TerrainComponent_RayTracingResolutionDefaultsAndClamps()
	{
		var component = new TerrainComponent();

		Assert.That(component.GetResolvedRayTracingResolutionInQuads(), Is.EqualTo(16));

		component.RayTracingResolutionInQuads = -4;
		Assert.That(component.GetResolvedRayTracingResolutionInQuads(), Is.EqualTo(16));

		component.RayTracingResolutionInQuads = 0;
		Assert.That(component.GetResolvedRayTracingResolutionInQuads(), Is.EqualTo(16));

		component.RayTracingResolutionInQuads = 512;
		Assert.That(component.GetResolvedRayTracingResolutionInQuads(), Is.EqualTo(256));

		component.RayTracingResolutionInQuads = 12;
		Assert.That(component.GetResolvedRayTracingResolutionInQuads(), Is.EqualTo(12));
	}

	[Test]
	public void EnsureBuilt_CreatesRayTracingChunksWithConfiguredResolution()
	{
		using var registry = new TestAssetRegistry();
		var heightmapId = Guid.NewGuid();
		registry.Register(heightmapId, CreateHeightTexture("height-5x5", 5, 5));

		var component = new TerrainComponent
		{
			TerrainAsset = new AssetRef<TerrainAsset> { NodeId = heightmapId },
			WorldSizeMeters = new Vector2(128.0f, 64.0f),
			HeightScaleMeters = 16.0f,
			ChunkSizeMeters = 64.0f,
			LodCount = 2,
			Lod0ResolutionInQuads = 4,
			RayTracingResolutionInQuads = 12,
			LodDistancesMeters = [120.0f]
		};
		var runtime = new TerrainRuntimeData();

		Assert.That(runtime.EnsureBuilt(component), Is.True);
		Assert.That(runtime.Chunks, Has.Count.EqualTo(2));
		Assert.That(runtime.RayTracingChunks, Has.Count.EqualTo(2));
		Assert.That(runtime.RayTracingChunks.Select(chunk => chunk.ResolutionInQuads), Is.All.EqualTo(12));
		Assert.That(runtime.RayTracingChunks[0].InstanceData.ChunkOriginSize, Is.EqualTo(runtime.Chunks[0].InstanceData.ChunkOriginSize));
		Assert.That(runtime.RayTracingChunks[1].InstanceData.HeightmapUvScaleOffset, Is.EqualTo(runtime.Chunks[1].InstanceData.HeightmapUvScaleOffset));
	}

	[Test]
	public void MarkHeightmapEdited_IncrementsOnlyIntersectingRayTracingChunkRevision()
	{
		using var registry = new TestAssetRegistry();
		var heightmapId = Guid.NewGuid();
		registry.Register(heightmapId, CreateHeightTexture("height-9x5", 9, 5));

		var component = new TerrainComponent
		{
			TerrainAsset = new AssetRef<TerrainAsset> { NodeId = heightmapId },
			WorldSizeMeters = new Vector2(128.0f, 64.0f),
			HeightScaleMeters = 16.0f,
			ChunkSizeMeters = 64.0f,
			LodCount = 1,
			Lod0ResolutionInQuads = 4,
			RayTracingResolutionInQuads = 8,
			LodDistancesMeters = []
		};
		var runtime = new TerrainRuntimeData();
		Assert.That(runtime.EnsureBuilt(component), Is.True);
		var leftRevision = runtime.RayTracingChunks[0].GeometryRevision;
		var rightRevision = runtime.RayTracingChunks[1].GeometryRevision;

		runtime.MarkHeightmapEdited(new TerrainHeightmapDirtyRegion(0, 0, 2, 5, 9, 5));
		Assert.That(runtime.EnsureBuilt(component), Is.True);

		Assert.That(runtime.RayTracingChunks[0].GeometryRevision, Is.EqualTo(leftRevision + 1));
		Assert.That(runtime.RayTracingChunks[1].GeometryRevision, Is.EqualTo(rightRevision));
	}

	[Test]
	public void CollectChunkDrawRecords_SelectsLodsByDistanceWithoutCpuFrustumCulling()
	{
		using var registry = new TestAssetRegistry();
		var heightmapId = Guid.NewGuid();
		registry.Register(heightmapId, CreateHeightTexture("height-5x5", 5, 5));

		var component = new TerrainComponent
		{
			TerrainAsset = new AssetRef<TerrainAsset> { NodeId = heightmapId },
			WorldSizeMeters = new Vector2(64.0f, 64.0f),
			HeightScaleMeters = 8.0f,
			ChunkSizeMeters = 64.0f,
			LodCount = 3,
			Lod0ResolutionInQuads = 4,
			LodDistancesMeters = [120.0f, 320.0f]
		};
		var runtime = new TerrainRuntimeData();
		Assert.That(runtime.EnsureBuilt(component), Is.True);

		var resourceScheduler = Mock.Of<IRenderResourceScheduler>();
		var material = new Material("__terrain__");
		var nearCamera = new Vector3(0.0f, 40.0f, -60.0f);
		var records = new List<TerrainChunkDrawRecord>();

		runtime.CollectChunkDrawRecords(resourceScheduler, material, nearCamera, Matrix4x4.Identity, records);

		Assert.That(records, Has.Count.EqualTo(1));
		Assert.That(records[0].Mesh, Is.SameAs(runtime.SharedLodMeshes[0]));
		Assert.That(records[0].Material, Is.SameAs(material));
		Assert.That(records[0].ChunkIndex, Is.EqualTo(0));
		Assert.That(records[0].Surface.LayerCount, Is.EqualTo(1));

		records.Clear();
		var farCamera = new Vector3(0.0f, 50.0f, -400.0f);
		runtime.CollectChunkDrawRecords(resourceScheduler, material, farCamera, Matrix4x4.Identity, records);

		Assert.That(records, Has.Count.EqualTo(1));
		Assert.That(records[0].Mesh, Is.SameAs(runtime.SharedLodMeshes[2]));
		Assert.That(records[0].Material, Is.SameAs(material));

		records.Clear();
		runtime.CollectChunkDrawRecords(
			resourceScheduler,
			material,
			nearCamera,
			Matrix4x4.CreateTranslation(5000.0f, 0.0f, 0.0f),
			records);

		Assert.That(records, Has.Count.EqualTo(1));
		Assert.That(records[0].Mesh, Is.SameAs(runtime.SharedLodMeshes[2]));
		Assert.That(records[0].Material, Is.SameAs(material));
	}

	[Test]
	public void EnsureBuilt_ChunkGridAndSharedLodsFollowTerrainSettings()
	{
		using var registry = new TestAssetRegistry();
		var heightmapId = Guid.NewGuid();
		registry.Register(heightmapId, CreateHeightTexture("height-5x5", 5, 5));

		var component = new TerrainComponent
		{
			TerrainAsset = new AssetRef<TerrainAsset> { NodeId = heightmapId },
			WorldSizeMeters = new Vector2(128.0f, 128.0f),
			HeightScaleMeters = 16.0f,
			ChunkSizeMeters = 32.0f,
			LodCount = 4,
			Lod0ResolutionInQuads = 8,
			LodDistancesMeters = [80.0f, 160.0f, 320.0f]
		};
		var runtime = new TerrainRuntimeData();

		Assert.That(runtime.EnsureBuilt(component), Is.True);
		Assert.That(runtime.Chunks, Has.Count.EqualTo(16));
		Assert.That(runtime.SharedLodMeshes, Has.Count.EqualTo(4));
		Assert.That(runtime.SharedLodMeshes[0].Vertices.Length, Is.GreaterThan(runtime.SharedLodMeshes[1].Vertices.Length));
		Assert.That(runtime.SharedLodMeshes[1].Vertices.Length, Is.GreaterThan(runtime.SharedLodMeshes[2].Vertices.Length));
	}

	[Test]
	public void EnsureBuilt_UsesAuthoringPreviewForRenderingWithoutRefreshingHeightSamples()
	{
		using var registry = new TestAssetRegistry();
		var heightmapId = Guid.NewGuid();
		var sourceTexture = CreateHeightTexture("height-source", 5, 5, 0);
		registry.Register(heightmapId, sourceTexture);

		var component = new TerrainComponent
		{
			TerrainAsset = new AssetRef<TerrainAsset> { NodeId = heightmapId },
			WorldSizeMeters = new Vector2(64.0f, 64.0f),
			HeightScaleMeters = 8.0f,
			ChunkSizeMeters = 64.0f,
			LodCount = 2,
			Lod0ResolutionInQuads = 4,
			LodDistancesMeters = [120.0f]
		};
		var runtime = new TerrainRuntimeData();
		Assert.That(runtime.EnsureBuilt(component), Is.True);
		var baselineVersion = runtime.RuntimeVersion;

		component.AuthoringPreviewHeightmap = CreateHeightTexture("height-preview", 5, 5, 255);
		var resourceScheduler = Mock.Of<IRenderResourceScheduler>();
		var material = new Material("__terrain__");
		var records = new List<TerrainChunkDrawRecord>();

		Assert.That(runtime.EnsureBuilt(component), Is.True);
		runtime.CollectChunkDrawRecords(resourceScheduler, material, new Vector3(0.0f, 10.0f, -20.0f), Matrix4x4.Identity, records);

		Assert.That(runtime.RuntimeVersion, Is.EqualTo(baselineVersion));
		Assert.That(records, Has.Count.EqualTo(1));
		Assert.That(records[0].Surface.Heightmap, Is.SameAs(component.AuthoringPreviewHeightmap));
	}

	[Test]
	public void CollectChunkDrawRecords_ReusesSharedMaterialAcrossCalls()
	{
		using var registry = new TestAssetRegistry();
		var heightmapId = Guid.NewGuid();
		registry.Register(heightmapId, CreateHeightTexture("height-5x5", 5, 5));

		var component = new TerrainComponent
		{
			TerrainAsset = new AssetRef<TerrainAsset> { NodeId = heightmapId },
			WorldSizeMeters = new Vector2(64.0f, 64.0f),
			HeightScaleMeters = 8.0f,
			ChunkSizeMeters = 64.0f,
			LodCount = 3,
			Lod0ResolutionInQuads = 4,
			LodDistancesMeters = [120.0f, 320.0f]
		};
		var runtime = new TerrainRuntimeData();
		Assert.That(runtime.EnsureBuilt(component), Is.True);

		var resourceScheduler = Mock.Of<IRenderResourceScheduler>();
		var material = new Material("__terrain__");
		var records = new List<TerrainChunkDrawRecord>();
		var camera = new Vector3(0.0f, 40.0f, -60.0f);

		runtime.CollectChunkDrawRecords(resourceScheduler, material, camera, Matrix4x4.Identity, records);
		Assert.That(records, Has.Count.EqualTo(1));
		var firstMaterial = records[0].Material;

		records.Clear();
		runtime.CollectChunkDrawRecords(resourceScheduler, material, camera, Matrix4x4.Identity, records);
		Assert.That(records, Has.Count.EqualTo(1));
		Assert.That(records[0].Material, Is.SameAs(firstMaterial));
	}

	[Test]
	public void EnsureBuilt_RebuildsWhenHeightTextureContentChanges()
	{
		using var registry = new TestAssetRegistry();
		var heightmapId = Guid.NewGuid();
		var texture = CreateHeightTexture("height-5x5", 5, 5);
		registry.Register(heightmapId, texture);

		var component = new TerrainComponent
		{
			TerrainAsset = new AssetRef<TerrainAsset> { NodeId = heightmapId },
			WorldSizeMeters = new Vector2(64.0f, 64.0f),
			HeightScaleMeters = 16.0f,
			ChunkSizeMeters = 64.0f,
			LodCount = 3,
			Lod0ResolutionInQuads = 4,
			LodDistancesMeters = [120.0f, 320.0f]
		};
		var runtime = new TerrainRuntimeData();

		Assert.That(runtime.EnsureBuilt(component), Is.True);
		var initialChunkCount = runtime.Chunks.Count;
		var initialSharedMeshes = runtime.SharedLodMeshes.ToArray();

		registry.Register(heightmapId, CreateTerrainAssetFromHeightTexture(CreateHeightTexture("height-9x9", 9, 9)));

		Assert.That(runtime.EnsureBuilt(component), Is.True);
		Assert.That(runtime.HeightSampleWidth, Is.EqualTo(9));
		Assert.That(runtime.HeightSampleHeight, Is.EqualTo(9));
		Assert.That(runtime.Chunks.Count, Is.EqualTo(initialChunkCount));
		Assert.That(runtime.SharedLodMeshes.SequenceEqual(initialSharedMeshes), Is.True);
	}

	[Test]
	public void EnsureBuilt_ReleasesSupersededChunkMeshesAfterRebuild()
	{
		using var registry = new TestAssetRegistry();
		var heightmapId = Guid.NewGuid();
		registry.Register(heightmapId, CreateHeightTexture("height-5x5", 5, 5));

		var component = new TerrainComponent
		{
			TerrainAsset = new AssetRef<TerrainAsset> { NodeId = heightmapId },
			WorldSizeMeters = new Vector2(64.0f, 64.0f),
			HeightScaleMeters = 16.0f,
			ChunkSizeMeters = 64.0f,
			LodCount = 3,
			Lod0ResolutionInQuads = 4,
			LodDistancesMeters = [120.0f, 320.0f]
		};
		var runtime = new TerrainRuntimeData();
		Assert.That(runtime.EnsureBuilt(component), Is.True);
		var initialMeshes = runtime.SharedLodMeshes.ToArray();

		component.WorldSizeMeters = new Vector2(128.0f, 128.0f);
		Assert.That(runtime.EnsureBuilt(component), Is.True);

		var resourceScheduler = new Mock<IRenderResourceScheduler>();
		runtime.ReleasePendingMeshResources(resourceScheduler.Object);

		foreach (var mesh in initialMeshes)
		{
			resourceScheduler.Verify(value => value.ReleaseMeshResources(mesh), Times.Once);
		}
	}

	[Test]
	public void EnsureBuilt_FlatTerrainChunksUseConservativeBounds()
	{
		using var registry = new TestAssetRegistry();
		var heightmapId = Guid.NewGuid();
		registry.Register(heightmapId, CreateHeightTexture("height-flat", 5, 5, 0));

		var component = new TerrainComponent
		{
			TerrainAsset = new AssetRef<TerrainAsset> { NodeId = heightmapId },
			WorldSizeMeters = new Vector2(64.0f, 64.0f),
			HeightScaleMeters = 32.0f,
			ChunkSizeMeters = 64.0f,
			LodCount = 3,
			Lod0ResolutionInQuads = 4,
			LodDistancesMeters = [120.0f, 320.0f]
		};
		var runtime = new TerrainRuntimeData();

		Assert.That(runtime.EnsureBuilt(component), Is.True);
		Assert.That(runtime.Chunks, Has.Count.EqualTo(1));
		var bounds = runtime.Chunks[0].LocalBounds;
		Assert.That(bounds.Center, Is.EqualTo(new Vector3(0.0f, 16.0f, 0.0f)));
		Assert.That(bounds.Radius, Is.EqualTo(new Vector3(32.0f, 16.0f, 32.0f).Length()).Within(0.0001f));
	}

	[Test]
	public void EnsureBuilt_RefusesBuildWhenChunkTileCountExceedsLimit()
	{
		using var registry = new TestAssetRegistry();
		var heightmapId = Guid.NewGuid();
		registry.Register(heightmapId, CreateHeightTexture("height-402x402", 402, 402));

		var component = new TerrainComponent
		{
			TerrainAsset = new AssetRef<TerrainAsset> { NodeId = heightmapId },
			WorldSizeMeters = new Vector2(402.0f, 402.0f),
			HeightScaleMeters = 16.0f,
			ChunkSizeMeters = 1.0f,
			LodCount = 3,
			Lod0ResolutionInQuads = 4,
			LodDistancesMeters = [120.0f, 320.0f]
		};
		var runtime = new TerrainRuntimeData();

		Assert.That(runtime.EnsureBuilt(component), Is.False);
		Assert.That(runtime.Chunks, Is.Empty);
	}

	[Test]
	public void TrySampleSurface_ReturnsExpectedHeightAndNormal()
	{
		using var registry = new TestAssetRegistry();
		var heightmapId = Guid.NewGuid();
		registry.Register(heightmapId, CreateHeightTexture("height-2x2", 2, 2, [0, 128, 128, 255]));

		var component = new TerrainComponent
		{
			TerrainAsset = new AssetRef<TerrainAsset> { NodeId = heightmapId },
			WorldSizeMeters = new Vector2(2.0f, 2.0f),
			HeightScaleMeters = 8.0f,
			ChunkSizeMeters = 2.0f,
			LodCount = 3,
			Lod0ResolutionInQuads = 4,
			LodDistancesMeters = [120.0f, 320.0f]
		};
		var runtime = new TerrainRuntimeData();
		Assert.That(runtime.EnsureBuilt(component), Is.True);

		var sampled = runtime.TrySampleSurface(Matrix4x4.Identity, new Vector3(0.0f, 100.0f, 0.0f), out var point, out var normal);

		Assert.That(sampled, Is.True);
		Assert.That(point.Y, Is.EqualTo((128.0f / 255.0f) * 8.0f).Within(0.05f));
		Assert.That(normal.Y, Is.GreaterThan(0.3f));
		Assert.That(normal.X, Is.LessThan(0.0f));
		Assert.That(normal.Z, Is.LessThan(0.0f));
	}

	[Test]
	public void TrySampleSurface_ReturnsFalseOutsideTerrainBounds()
	{
		using var registry = new TestAssetRegistry();
		var heightmapId = Guid.NewGuid();
		registry.Register(heightmapId, CreateHeightTexture("height-2x2", 2, 2, [0, 0, 0, 0]));

		var component = new TerrainComponent
		{
			TerrainAsset = new AssetRef<TerrainAsset> { NodeId = heightmapId },
			WorldSizeMeters = new Vector2(2.0f, 2.0f),
			HeightScaleMeters = 4.0f,
			ChunkSizeMeters = 2.0f,
			LodCount = 3,
			Lod0ResolutionInQuads = 4,
			LodDistancesMeters = [120.0f, 320.0f]
		};
		var runtime = new TerrainRuntimeData();
		Assert.That(runtime.EnsureBuilt(component), Is.True);

		var sampled = runtime.TrySampleSurface(Matrix4x4.Identity, new Vector3(2.1f, 0.0f, 0.0f), out _, out _);

		Assert.That(sampled, Is.False);
	}

	[Test]
	public void TrySampleSurface_RespectsWorldTransform()
	{
		using var registry = new TestAssetRegistry();
		var heightmapId = Guid.NewGuid();
		registry.Register(heightmapId, CreateHeightTexture("height-2x2", 2, 2, [0, 255, 0, 255]));

		var component = new TerrainComponent
		{
			TerrainAsset = new AssetRef<TerrainAsset> { NodeId = heightmapId },
			WorldSizeMeters = new Vector2(2.0f, 2.0f),
			HeightScaleMeters = 2.0f,
			ChunkSizeMeters = 2.0f,
			LodCount = 3,
			Lod0ResolutionInQuads = 4,
			LodDistancesMeters = [120.0f, 320.0f]
		};
		var runtime = new TerrainRuntimeData();
		Assert.That(runtime.EnsureBuilt(component), Is.True);

		var localToWorld = Matrix4x4.CreateScale(2.0f) *
		                   Matrix4x4.CreateFromQuaternion(Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI * 0.5f)) *
		                   Matrix4x4.CreateTranslation(new Vector3(10.0f, 3.0f, 20.0f));
		var localSurfacePoint = new Vector3(1.0f, 2.0f, -1.0f);
		var expectedWorldPoint = Vector3.Transform(localSurfacePoint, localToWorld);
		var queryPoint = new Vector3(expectedWorldPoint.X, 100.0f, expectedWorldPoint.Z);

		var sampled = runtime.TrySampleSurface(localToWorld, queryPoint, out var point, out var normal);

		Assert.That(sampled, Is.True);
		Assert.That(point.Y, Is.EqualTo(expectedWorldPoint.Y).Within(0.05f));
		Assert.That(normal.Y, Is.GreaterThan(0.4f));
	}

	private static Texture CreateHeightTexture(string name, int width, int height)
	{
		return new Texture(name, width, height, false, TextureFormat.Rgba8Unorm, CreateHeightMipLevels(width, height));
	}

	private static Texture CreateHeightTexture(string name, int width, int height, byte normalizedHeights)
	{
		return CreateHeightTexture(name, width, height, Enumerable.Repeat(normalizedHeights, width * height).ToArray());
	}

	private static Texture CreateHeightTexture(string name, int width, int height, byte[] normalizedHeights)
	{
		return new Texture(name, width, height, false, TextureFormat.Rgba8Unorm, [new TextureMipData(width, height, CreateRgbaHeightData(width, height, normalizedHeights))]);
	}

	private static TextureMipData[] CreateHeightMipLevels(int width, int height)
	{
		var data = new byte[width * height * 4];
		for (var y = 0; y < height; y++)
		{
			for (var x = 0; x < width; x++)
			{
				var sampleIndex = y * width + x;
				var offset = sampleIndex * 4;
				data[offset] = (byte)Math.Clamp((x + y) * 16, 0, 255);
				data[offset + 1] = 0;
				data[offset + 2] = 0;
				data[offset + 3] = 255;
			}
		}

		return [new TextureMipData(width, height, data)];
	}

	private static byte[] CreateRgbaHeightData(int width, int height, byte[] normalizedHeights)
	{
		if (normalizedHeights.Length != width * height)
		{
			throw new ArgumentException("Height sample count must match width * height.", nameof(normalizedHeights));
		}

		var data = new byte[width * height * 4];
		for (var i = 0; i < normalizedHeights.Length; i++)
		{
			var offset = i * 4;
			data[offset] = normalizedHeights[i];
			data[offset + 1] = 0;
			data[offset + 2] = 0;
			data[offset + 3] = 255;
		}

		return data;
	}

	private static TerrainAsset CreateTerrainAssetFromHeightTexture(Texture heightTexture)
	{
		var topMip = heightTexture.MipLevels[0];
		var heightData = new byte[topMip.Width * topMip.Height * 2];
		var bytesPerPixel = TextureFormatUtilities.GetBytesPerBlock(heightTexture.Format);
		for (var i = 0; i < topMip.Width * topMip.Height; i++)
		{
			var normalizedHeight = topMip.Data[i * bytesPerPixel];
			var height = (ushort)(normalizedHeight * 257);
			var offset = i * 2;
			heightData[offset] = (byte)(height & 0xFF);
			heightData[offset + 1] = (byte)(height >> 8);
		}

		var heightmap = new Texture(
			heightTexture.Name,
			topMip.Width,
			topMip.Height,
			isSrgb: false,
			TextureFormat.R16Unorm,
			[new TextureMipData(topMip.Width, topMip.Height, heightData)]);
		var indexData = new byte[topMip.Width * topMip.Height * 4];
		var weightData = new byte[topMip.Width * topMip.Height * 4];
		for (var i = 0; i < topMip.Width * topMip.Height; i++)
		{
			weightData[i * 4] = 255;
		}

		var layerMips = TerrainLayerMapUtility.GenerateLayerMipChain(
			new TextureMipData(topMip.Width, topMip.Height, indexData),
			new TextureMipData(topMip.Width, topMip.Height, weightData));
		var layerIndexMap = new Texture(
			$"{heightTexture.Name}_layers",
			topMip.Width,
			topMip.Height,
			isSrgb: false,
			TextureFormat.Rgba8Uint,
			layerMips.Indices);
		var layerWeightMap = new Texture(
			$"{heightTexture.Name}_weights",
			topMip.Width,
			topMip.Height,
			isSrgb: false,
			TextureFormat.Rgba8Unorm,
			layerMips.Weights);
		return new TerrainAsset(heightTexture.Name, heightmap, layerIndexMap, layerWeightMap);
	}

	private sealed class TestAssetRegistry : IAssetInstanceRegistry, IDisposable
	{
		private readonly Dictionary<Guid, object> _assets = new();
		private readonly Dictionary<Guid, TerrainAsset> _terrainAssets = new();

		public TestAssetRegistry()
		{
			AssetDatabase.SetInstanceRegistry(this);
		}

		public void Register(Guid assetId, object asset)
		{
			_assets[assetId] = asset;
			_terrainAssets.Remove(assetId);
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

			if (expectedType == typeof(TerrainAsset) && asset is Texture heightTexture)
			{
				if (_terrainAssets.TryGetValue(assetId, out var terrainAsset) == false)
				{
					terrainAsset = CreateTerrainAssetFromHeightTexture(heightTexture);
					_terrainAssets[assetId] = terrainAsset;
				}

				return terrainAsset;
			}

			return null;
		}

		public void RefreshProject(string projectRootPath, AssetDatabase database)
		{
		}

		public void InvalidateAssets(IEnumerable<Guid> assetIds)
		{
			foreach (var assetId in assetIds)
			{
				_assets.Remove(assetId);
				_terrainAssets.Remove(assetId);
			}
		}

		public void ClearCachedInstances()
		{
			_assets.Clear();
			_terrainAssets.Clear();
		}

		public void Clear()
		{
			_assets.Clear();
			_terrainAssets.Clear();
		}

		public void Dispose()
		{
			AssetDatabase.ClearInstanceRegistry();
		}
	}
}
