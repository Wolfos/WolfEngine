#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Reflection;
using System.Runtime.Serialization;
using WolfEngine.AssetPipeline;
using WolfEngine.Editor.Projects;
using WolfEngine.Rendering;

namespace WolfEngine.Editor.Tests;

[TestFixture]
public sealed class TerrainPrototypeTests
{
	[TearDown]
	public void TearDown()
	{
		AssetDatabase.ClearInstanceRegistry();
	}

	[Test]
	public void DataAssetStore_RoundTripsTerrainLayerSetTextureRefs()
	{
		var store = new DataAssetStore();
		var assetPath = Path.Combine(Path.GetTempPath(), "WolfEngineTerrainTests", Guid.NewGuid().ToString("N"), $"TerrainLayerSet{DataAssetFile.FileExtension}");
		var albedoId = Guid.NewGuid();
		var normalId = Guid.NewGuid();
		var heightId = Guid.NewGuid();
		var layerSet = new TerrainLayerSet
		{
			ActiveLayerCount = 2,
			HeightBlendSharpness = 6.5f,
			Layer0 = new TerrainLayerDefinition
			{
				Scale = 12.0f,
				Albedo = new AssetRef<Texture> { NodeId = albedoId },
				Normal = new AssetRef<Texture> { NodeId = normalId }
			},
			Layer1 = new TerrainLayerDefinition
			{
				Scale = 24.0f,
				Height = new AssetRef<Texture> { NodeId = heightId }
			}
		};

		store.SaveAsset(assetPath, typeof(TerrainLayerSet), layerSet);
		var loadResult = store.LoadAsset(assetPath);
		var loaded = (TerrainLayerSet)loadResult.Asset;

		Assert.That(loadResult.DataAssetType, Is.EqualTo(typeof(TerrainLayerSet)));
		Assert.That(loaded.ActiveLayerCount, Is.EqualTo(2));
		Assert.That(loaded.HeightBlendSharpness, Is.EqualTo(6.5f).Within(0.0001f));
		Assert.That(loaded.Layer0.Scale, Is.EqualTo(12.0f).Within(0.0001f));
		Assert.That(loaded.Layer1.Scale, Is.EqualTo(24.0f).Within(0.0001f));
		Assert.That(loaded.Layer0.Albedo.NodeId, Is.EqualTo(albedoId));
		Assert.That(loaded.Layer0.Normal.NodeId, Is.EqualTo(normalId));
		Assert.That(loaded.Layer1.Height.NodeId, Is.EqualTo(heightId));
	}

	[Test]
	public void TerrainLayerSet_SupportsMoreThanFourLayers()
	{
		var layerSet = new TerrainLayerSet
		{
			ActiveLayerCount = 6
		};
		layerSet.EnsureLayerCapacity(6);
		layerSet.Layers[5].Scale = 48.0f;

		Assert.That(layerSet.ResolvedLayerCount, Is.EqualTo(6));
		Assert.That(layerSet.GetLayer(5).Scale, Is.EqualTo(48.0f).Within(0.0001f));
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
			HeightmapAsset = new AssetRef<Texture> { NodeId = heightmapId },
			WorldSizeMeters = new Vector2(64.0f, 64.0f),
			HeightScaleMeters = 16.0f,
			ChunkSizeInQuads = 4
		};
		var runtime = new TerrainRuntimeData();

		Assert.That(runtime.EnsureBuilt(component), Is.True);
		Assert.That(runtime.HeightSampleWidth, Is.EqualTo(5));
		Assert.That(runtime.HeightSampleHeight, Is.EqualTo(5));
		Assert.That(runtime.Chunks, Has.Count.EqualTo(1));

		var chunk = runtime.Chunks[0];
		Assert.That(chunk.LodMeshes[0].Vertices, Has.Length.EqualTo(45));
		Assert.That(chunk.LodMeshes[0].Indices, Has.Length.EqualTo(192));
		Assert.That(chunk.LodMeshes[1].Vertices, Has.Length.EqualTo(21));
		Assert.That(chunk.LodMeshes[1].Indices, Has.Length.EqualTo(72));
		Assert.That(chunk.LodMeshes[2].Vertices, Has.Length.EqualTo(12));
		Assert.That(chunk.LodMeshes[2].Indices, Has.Length.EqualTo(30));
	}

	[Test]
	public void CollectChunkDrawRecords_SelectsLodsByDistanceWithoutCpuFrustumCulling()
	{
		using var registry = new TestAssetRegistry();
		var heightmapId = Guid.NewGuid();
		registry.Register(heightmapId, CreateHeightTexture("height-5x5", 5, 5));

		var component = new TerrainComponent
		{
			HeightmapAsset = new AssetRef<Texture> { NodeId = heightmapId },
			WorldSizeMeters = new Vector2(64.0f, 64.0f),
			HeightScaleMeters = 8.0f,
			ChunkSizeInQuads = 4
		};
		var runtime = new TerrainRuntimeData();
		Assert.That(runtime.EnsureBuilt(component), Is.True);

		var renderGraph = CreateTestRenderGraph();
		var material = new Material("__terrain__");
		var nearCamera = new Vector3(0.0f, 40.0f, -60.0f);
		var records = new List<TerrainChunkDrawRecord>();

		runtime.CollectChunkDrawRecords(renderGraph, material, nearCamera, Matrix4x4.Identity, records);

		Assert.That(records, Has.Count.EqualTo(1));
		Assert.That(records[0].Mesh, Is.SameAs(runtime.Chunks[0].LodMeshes[0]));
		Assert.That(records[0].Material, Is.SameAs(material));
		Assert.That(records[0].ChunkIndex, Is.EqualTo(0));
		Assert.That(records[0].Surface.LayerCount, Is.EqualTo(1));

		records.Clear();
		var farCamera = new Vector3(0.0f, 50.0f, -400.0f);
		runtime.CollectChunkDrawRecords(renderGraph, material, farCamera, Matrix4x4.Identity, records);

		Assert.That(records, Has.Count.EqualTo(1));
		Assert.That(records[0].Mesh, Is.SameAs(runtime.Chunks[0].LodMeshes[2]));
		Assert.That(records[0].Material, Is.SameAs(material));

		records.Clear();
		runtime.CollectChunkDrawRecords(
			renderGraph,
			material,
			nearCamera,
			Matrix4x4.CreateTranslation(5000.0f, 0.0f, 0.0f),
			records);

		Assert.That(records, Has.Count.EqualTo(1));
		Assert.That(records[0].Mesh, Is.SameAs(runtime.Chunks[0].LodMeshes[2]));
		Assert.That(records[0].Material, Is.SameAs(material));
	}

	[Test]
	public void CollectChunkDrawRecords_ReusesSharedMaterialAcrossCalls()
	{
		using var registry = new TestAssetRegistry();
		var heightmapId = Guid.NewGuid();
		registry.Register(heightmapId, CreateHeightTexture("height-5x5", 5, 5));

		var component = new TerrainComponent
		{
			HeightmapAsset = new AssetRef<Texture> { NodeId = heightmapId },
			WorldSizeMeters = new Vector2(64.0f, 64.0f),
			HeightScaleMeters = 8.0f,
			ChunkSizeInQuads = 4
		};
		var runtime = new TerrainRuntimeData();
		Assert.That(runtime.EnsureBuilt(component), Is.True);

		var renderGraph = CreateTestRenderGraph();
		var material = new Material("__terrain__");
		var records = new List<TerrainChunkDrawRecord>();
		var camera = new Vector3(0.0f, 40.0f, -60.0f);

		runtime.CollectChunkDrawRecords(renderGraph, material, camera, Matrix4x4.Identity, records);
		Assert.That(records, Has.Count.EqualTo(1));
		var firstMaterial = records[0].Material;

		records.Clear();
		runtime.CollectChunkDrawRecords(renderGraph, material, camera, Matrix4x4.Identity, records);
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
			HeightmapAsset = new AssetRef<Texture> { NodeId = heightmapId },
			WorldSizeMeters = new Vector2(64.0f, 64.0f),
			HeightScaleMeters = 16.0f,
			ChunkSizeInQuads = 4
		};
		var runtime = new TerrainRuntimeData();

		Assert.That(runtime.EnsureBuilt(component), Is.True);
		var initialChunkCount = runtime.Chunks.Count;

		texture.ApplyTextureData(9, 9, false, TextureFormat.Rgba8Unorm, CreateHeightMipLevels(9, 9));

		Assert.That(runtime.EnsureBuilt(component), Is.True);
		Assert.That(runtime.HeightSampleWidth, Is.EqualTo(9));
		Assert.That(runtime.HeightSampleHeight, Is.EqualTo(9));
		Assert.That(runtime.Chunks.Count, Is.Not.EqualTo(initialChunkCount));
		Assert.That(runtime.Chunks.Count, Is.EqualTo(4));
	}

	[Test]
	public void TrySampleSurface_ReturnsExpectedHeightAndNormal()
	{
		using var registry = new TestAssetRegistry();
		var heightmapId = Guid.NewGuid();
		registry.Register(heightmapId, CreateHeightTexture("height-2x2", 2, 2, [0, 128, 128, 255]));

		var component = new TerrainComponent
		{
			HeightmapAsset = new AssetRef<Texture> { NodeId = heightmapId },
			WorldSizeMeters = new Vector2(2.0f, 2.0f),
			HeightScaleMeters = 8.0f,
			ChunkSizeInQuads = 4
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
			HeightmapAsset = new AssetRef<Texture> { NodeId = heightmapId },
			WorldSizeMeters = new Vector2(2.0f, 2.0f),
			HeightScaleMeters = 4.0f,
			ChunkSizeInQuads = 4
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
			HeightmapAsset = new AssetRef<Texture> { NodeId = heightmapId },
			WorldSizeMeters = new Vector2(2.0f, 2.0f),
			HeightScaleMeters = 2.0f,
			ChunkSizeInQuads = 4
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

	private static RenderGraph CreateTestRenderGraph()
	{
		var renderGraph = (RenderGraph)FormatterServices.GetUninitializedObject(typeof(RenderGraph));
		SetField(renderGraph, "_resourceSync", new object());
		SetField(renderGraph, "_pendingTextures", new HashSet<Texture>());
		SetField(renderGraph, "_ensureMeshQueue", new ConcurrentQueue<Mesh>());
		return renderGraph;
	}

	private static void SetField(object instance, string fieldName, object value)
	{
		var field = instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
			?? throw new AssertionException($"Field '{fieldName}' was not found.");
		field.SetValue(instance, value);
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
