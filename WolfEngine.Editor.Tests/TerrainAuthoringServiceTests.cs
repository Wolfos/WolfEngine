#nullable enable

using System;
using System.Collections.Generic;
using System.Numerics;
using NSubstitute;
using WolfEngine.AssetPipeline;
using WolfEngine.Editor.Projects;
using WolfEngine.Editor.UI;
using WolfEngine.ECS;
using WolfEngine.Rendering;

namespace WolfEngine.Editor.Tests;

[TestFixture]
public sealed class TerrainAuthoringServiceTests
{
	[TearDown]
	public void TearDown()
	{
		AssetDatabase.ClearInstanceRegistry();
	}

	[Test]
	public void CancelStroke_KeepsTerrainAssetHeightmapUnchanged()
	{
		using var registry = new TestAssetRegistry();
		var terrainAssetId = Guid.NewGuid();
		var terrainAsset = CreateTerrainAsset("terrain", 5, 5, 10);
		registry.Register(terrainAssetId, terrainAsset);
		var scene = CreateScene(terrainAssetId);
		var service = CreateService(out _, out _);

		Assert.That(service.BeginStroke(
			scene,
			GetTerrainEntity(scene),
			new TerrainBrushStrokeRequest(
				TerrainAuthoringSurfaceTarget.Heightmap,
				TerrainBrushOperation.RaiseLower,
				new TerrainBrushSettings(8.0f, 0.8f, 1.5f, 0, null))), Is.True);

		service.AppendStamp(Vector3.Zero, 1.0f, new TerrainBrushModifierState(false));
		service.CancelStroke();

		Assert.That(ReadR16(terrainAsset.Heightmap.MipLevels[0].Data, 0), Is.EqualTo(10));
		ref var terrain = ref scene.World.GetComponent<TerrainComponent>(GetTerrainEntity(scene));
		Assert.That(terrain.AuthoringPreviewHeightmap, Is.Null);
	}

	[Test]
	public void EndStroke_CommitsR16HeightmapAndMarksSceneDirty()
	{
		using var registry = new TestAssetRegistry();
		var terrainAssetId = Guid.NewGuid();
		var terrainAsset = CreateTerrainAsset("terrain", 5, 5, 10);
		registry.Register(terrainAssetId, terrainAsset);
		var scene = CreateScene(terrainAssetId);
		var service = CreateService(out var undoRedo, out var interactionState);

		service.BeginStroke(
			scene,
			GetTerrainEntity(scene),
			new TerrainBrushStrokeRequest(
				TerrainAuthoringSurfaceTarget.Heightmap,
				TerrainBrushOperation.RaiseLower,
				new TerrainBrushSettings(8.0f, 1.0f, 1.0f, 0, null)));
		service.AppendStamp(Vector3.Zero, 1.0f, new TerrainBrushModifierState(false));

		Assert.That(service.EndStroke(), Is.True);

		var centerPixel = (2 * 5) + 2;
		Assert.That(ReadR16(terrainAsset.Heightmap.MipLevels[0].Data, centerPixel), Is.GreaterThan(10));
		Assert.That(interactionState.IsSceneDirty, Is.True);
		ref var terrain = ref scene.World.GetComponent<TerrainComponent>(GetTerrainEntity(scene));
		Assert.That(terrain.AuthoringPreviewHeightmap, Is.Not.Null);
		undoRedo.Received(1).CommitCapture(Arg.Any<IEditorUndoRedoEntry>());
	}

	[Test]
	public void EndStroke_MarksHeightmapDirtyForTerrainRayTracingRuntime()
	{
		using var registry = new TestAssetRegistry();
		var terrainAssetId = Guid.NewGuid();
		var terrainAsset = CreateTerrainAsset("terrain", 5, 5, 10);
		registry.Register(terrainAssetId, terrainAsset);
		var scene = CreateScene(terrainAssetId);
		var terrainEntity = GetTerrainEntity(scene);
		var service = CreateService(out _, out _);

		service.BeginStroke(
			scene,
			terrainEntity,
			new TerrainBrushStrokeRequest(
				TerrainAuthoringSurfaceTarget.Heightmap,
				TerrainBrushOperation.RaiseLower,
				new TerrainBrushSettings(8.0f, 1.0f, 1.0f, 0, null)));
		service.AppendStamp(Vector3.Zero, 1.0f, new TerrainBrushModifierState(false));
		service.EndStroke();

		var runtime = TerrainRuntimeRegistry.GetOrCreateRuntime(scene.World, terrainEntity);
		ref var terrain = ref scene.World.GetComponent<TerrainComponent>(terrainEntity);
		Assert.That(runtime.EnsureBuilt(terrain), Is.True);
		Assert.That(runtime.RayTracingChunks, Has.Count.EqualTo(1));
		Assert.That(runtime.RayTracingChunks[0].GeometryRevision, Is.EqualTo(2));
	}

	[Test]
	public void CancelStroke_DoesNotMarkHeightmapDirtyForTerrainRayTracingRuntime()
	{
		using var registry = new TestAssetRegistry();
		var terrainAssetId = Guid.NewGuid();
		var terrainAsset = CreateTerrainAsset("terrain", 5, 5, 10);
		registry.Register(terrainAssetId, terrainAsset);
		var scene = CreateScene(terrainAssetId);
		var terrainEntity = GetTerrainEntity(scene);
		var service = CreateService(out _, out _);

		service.BeginStroke(
			scene,
			terrainEntity,
			new TerrainBrushStrokeRequest(
				TerrainAuthoringSurfaceTarget.Heightmap,
				TerrainBrushOperation.RaiseLower,
				new TerrainBrushSettings(8.0f, 1.0f, 1.0f, 0, null)));
		service.AppendStamp(Vector3.Zero, 1.0f, new TerrainBrushModifierState(false));
		service.CancelStroke();

		var runtime = TerrainRuntimeRegistry.GetOrCreateRuntime(scene.World, terrainEntity);
		ref var terrain = ref scene.World.GetComponent<TerrainComponent>(terrainEntity);
		Assert.That(runtime.EnsureBuilt(terrain), Is.True);
		Assert.That(runtime.RayTracingChunks, Has.Count.EqualTo(1));
		Assert.That(runtime.RayTracingChunks[0].GeometryRevision, Is.EqualTo(1));
	}

	[Test]
	public void EndStroke_PaintsRequestedLayerIntoIndexAndWeightMaps()
	{
		using var registry = new TestAssetRegistry();
		var terrainAssetId = Guid.NewGuid();
		var terrainAsset = CreateTerrainAsset("terrain", 5, 5, 0);
		registry.Register(terrainAssetId, terrainAsset);
		var scene = CreateScene(terrainAssetId);
		var service = CreateService(out _, out _);

		service.BeginStroke(
			scene,
			GetTerrainEntity(scene),
			new TerrainBrushStrokeRequest(
				TerrainAuthoringSurfaceTarget.LayerMaps,
				TerrainBrushOperation.PaintLayer,
				new TerrainBrushSettings(8.0f, 1.0f, 1.0f, 2, null)));
		service.AppendStamp(Vector3.Zero, 1.0f, new TerrainBrushModifierState(false));
		service.EndStroke();

		var centerOffset = ((2 * 5) + 2) * 4;
		var paintedSlot = Array.IndexOf(terrainAsset.LayerIndexMap.MipLevels[0].Data, (byte)2, centerOffset, 4);
		Assert.That(paintedSlot, Is.GreaterThanOrEqualTo(centerOffset));
		Assert.That(terrainAsset.LayerWeightMap.MipLevels[0].Data[paintedSlot], Is.EqualTo(255));
		var weightSum =
			terrainAsset.LayerWeightMap.MipLevels[0].Data[centerOffset] +
			terrainAsset.LayerWeightMap.MipLevels[0].Data[centerOffset + 1] +
			terrainAsset.LayerWeightMap.MipLevels[0].Data[centerOffset + 2] +
			terrainAsset.LayerWeightMap.MipLevels[0].Data[centerOffset + 3];
		Assert.That(weightSum, Is.EqualTo(255));
		for (var i = 0; i < 4; i++)
		{
			if (centerOffset + i != paintedSlot)
			{
				Assert.That(terrainAsset.LayerWeightMap.MipLevels[0].Data[centerOffset + i], Is.EqualTo(0));
			}
		}

		Assert.That(terrainAsset.LayerWeightMap.MipLevels.Length, Is.GreaterThan(1));
	}

	[Test]
	public void EndStroke_PartialLayerPaintStealsWeightFromExistingLayers()
	{
		using var registry = new TestAssetRegistry();
		var terrainAssetId = Guid.NewGuid();
		var terrainAsset = CreateTerrainAsset("terrain", 5, 5, 0);
		registry.Register(terrainAssetId, terrainAsset);
		var scene = CreateScene(terrainAssetId);
		var service = CreateService(out _, out _);

		service.BeginStroke(
			scene,
			GetTerrainEntity(scene),
			new TerrainBrushStrokeRequest(
				TerrainAuthoringSurfaceTarget.LayerMaps,
				TerrainBrushOperation.PaintLayer,
				new TerrainBrushSettings(8.0f, 0.5f, 1.0f, 1, null)));
		service.AppendStamp(Vector3.Zero, 1.0f, new TerrainBrushModifierState(false));
		service.EndStroke();

		var centerOffset = ((2 * 5) + 2) * 4;
		var paintedSlot = Array.IndexOf(terrainAsset.LayerIndexMap.MipLevels[0].Data, (byte)1, centerOffset, 4);
		Assert.That(paintedSlot, Is.GreaterThanOrEqualTo(centerOffset));
		Assert.That(terrainAsset.LayerWeightMap.MipLevels[0].Data[paintedSlot], Is.EqualTo(128));
		Assert.That(terrainAsset.LayerWeightMap.MipLevels[0].Data[centerOffset], Is.EqualTo(127));
	}

	[Test]
	public void AppendStamp_PaintsLayerDirectlyIntoTerrainAssetWithoutPreviewMaps()
	{
		using var registry = new TestAssetRegistry();
		var terrainAssetId = Guid.NewGuid();
		var terrainAsset = CreateTerrainAsset("terrain", 5, 5, 0);
		registry.Register(terrainAssetId, terrainAsset);
		var scene = CreateScene(terrainAssetId);
		var service = CreateService(out _, out _);

		service.BeginStroke(
			scene,
			GetTerrainEntity(scene),
			new TerrainBrushStrokeRequest(
				TerrainAuthoringSurfaceTarget.LayerMaps,
				TerrainBrushOperation.PaintLayer,
				new TerrainBrushSettings(8.0f, 1.0f, 1.0f, 1, null)));
		service.AppendStamp(Vector3.Zero, 1.0f, new TerrainBrushModifierState(false));

		var centerOffset = ((2 * 5) + 2) * 4;
		var paintedSlot = Array.IndexOf(terrainAsset.LayerIndexMap.MipLevels[0].Data, (byte)1, centerOffset, 4);
		Assert.That(paintedSlot, Is.GreaterThanOrEqualTo(centerOffset));
		ref var terrain = ref scene.World.GetComponent<TerrainComponent>(GetTerrainEntity(scene));
		Assert.That(terrain.AuthoringPreviewLayerIndexMap, Is.Null);
		Assert.That(terrain.AuthoringPreviewLayerWeightMap, Is.Null);
	}

	[Test]
	public void CancelStroke_RestoresDirectLayerPaint()
	{
		using var registry = new TestAssetRegistry();
		var terrainAssetId = Guid.NewGuid();
		var terrainAsset = CreateTerrainAsset("terrain", 5, 5, 0);
		registry.Register(terrainAssetId, terrainAsset);
		var scene = CreateScene(terrainAssetId);
		var service = CreateService(out _, out _);

		service.BeginStroke(
			scene,
			GetTerrainEntity(scene),
			new TerrainBrushStrokeRequest(
				TerrainAuthoringSurfaceTarget.LayerMaps,
				TerrainBrushOperation.PaintLayer,
				new TerrainBrushSettings(8.0f, 1.0f, 1.0f, 1, null)));
		service.AppendStamp(Vector3.Zero, 1.0f, new TerrainBrushModifierState(false));
		service.CancelStroke();

		var centerOffset = ((2 * 5) + 2) * 4;
		Assert.That(Array.IndexOf(terrainAsset.LayerIndexMap.MipLevels[0].Data, (byte)1, centerOffset, 4), Is.EqualTo(-1));
		Assert.That(terrainAsset.LayerIndexMap.MipLevels[0].Data[centerOffset], Is.EqualTo(0));
		Assert.That(terrainAsset.LayerWeightMap.MipLevels[0].Data[centerOffset], Is.EqualTo(255));
	}

	[Test]
	public void EndStroke_LeavesUnpaintedLayerPixelsOnLayerZero()
	{
		using var registry = new TestAssetRegistry();
		var terrainAssetId = Guid.NewGuid();
		var terrainAsset = CreateTerrainAsset("terrain", 5, 5, 0);
		registry.Register(terrainAssetId, terrainAsset);
		var scene = CreateScene(terrainAssetId);
		var service = CreateService(out _, out _);

		service.BeginStroke(
			scene,
			GetTerrainEntity(scene),
			new TerrainBrushStrokeRequest(
				TerrainAuthoringSurfaceTarget.LayerMaps,
				TerrainBrushOperation.PaintLayer,
				new TerrainBrushSettings(8.0f, 1.0f, 1.0f, 1, null)));
		service.AppendStamp(Vector3.Zero, 1.0f, new TerrainBrushModifierState(false));
		service.EndStroke();

		var cornerOffset = 0;
		Assert.That(terrainAsset.LayerIndexMap.MipLevels[0].Data[cornerOffset], Is.EqualTo(0));
		Assert.That(terrainAsset.LayerWeightMap.MipLevels[0].Data[cornerOffset], Is.EqualTo(255));
		Assert.That(terrainAsset.LayerWeightMap.MipLevels[0].Data[cornerOffset + 1], Is.EqualTo(0));
	}

	[Test]
	public void EndStroke_ClampsPaintLayerToActiveLayerSet()
	{
		using var registry = new TestAssetRegistry();
		var terrainAssetId = Guid.NewGuid();
		var layerSetId = Guid.NewGuid();
		var terrainAsset = CreateTerrainAsset("terrain", 5, 5, 0);
		registry.Register(terrainAssetId, terrainAsset);
		registry.Register(layerSetId, new TerrainLayerSet { ActiveLayerCount = 2 });
		var scene = CreateScene(terrainAssetId, layerSetId);
		var service = CreateService(out _, out _);

		service.BeginStroke(
			scene,
			GetTerrainEntity(scene),
			new TerrainBrushStrokeRequest(
				TerrainAuthoringSurfaceTarget.LayerMaps,
				TerrainBrushOperation.PaintLayer,
				new TerrainBrushSettings(8.0f, 1.0f, 1.0f, 3, null)));
		service.AppendStamp(Vector3.Zero, 1.0f, new TerrainBrushModifierState(false));
		service.EndStroke();

		var centerOffset = ((2 * 5) + 2) * 4;
		var clampedSlot = Array.IndexOf(terrainAsset.LayerIndexMap.MipLevels[0].Data, (byte)2, centerOffset, 4);
		Assert.That(clampedSlot, Is.GreaterThanOrEqualTo(centerOffset));
		Assert.That(terrainAsset.LayerWeightMap.MipLevels[0].Data[clampedSlot], Is.GreaterThan(0));
		Assert.That(Array.IndexOf(terrainAsset.LayerIndexMap.MipLevels[0].Data, (byte)3, centerOffset, 4), Is.EqualTo(-1));
	}

	private static ITerrainAuthoringService CreateService(
		out IEditorUndoRedoService undoRedo,
		out IEditorInteractionState interactionState)
	{
		undoRedo = Substitute.For<IEditorUndoRedoService>();
		interactionState = new EditorInteractionState();
		return new TerrainAuthoringService(
			undoRedo,
			interactionState,
			new TestTerrainAssetPersistenceService(),
			new TerrainTexturePreviewRegistry(),
			new TestTerrainBrushGpuExecutor());
	}

	private static EditorScene CreateScene(Guid terrainAssetId, Guid layerSetId = default)
	{
		var world = new World(WorldTag.Authoring);
		var terrainEntity = world.CreateEntity("Terrain");
		world.AddTransform(terrainEntity, Matrix4x4.Identity);
		world.AddComponent(terrainEntity, new TerrainComponent
		{
			TerrainAsset = new AssetRef<TerrainAsset> { NodeId = terrainAssetId },
			LayerSetAsset = new AssetRef<TerrainLayerSet> { NodeId = layerSetId },
			WorldSizeMeters = new Vector2(64.0f, 64.0f),
			HeightScaleMeters = 8.0f,
			ChunkSizeMeters = 64.0f,
			LodCount = 2,
			Lod0ResolutionInQuads = 4,
			LodDistancesMeters = [120.0f]
		});
		return new EditorScene { World = world };
	}

	private static Entity GetTerrainEntity(EditorScene scene)
	{
		var entities = new List<Entity>();
		scene.World.GetAllEntities(entities);
		return entities[0];
	}

	private static TerrainAsset CreateTerrainAsset(string name, int width, int height, ushort heightValue)
	{
		var heightData = new byte[width * height * 2];
		for (var i = 0; i < width * height; i++)
		{
			WriteR16(heightData, i, heightValue);
		}

		var indices = new byte[width * height * 4];
		var weights = new byte[width * height * 4];
		for (var i = 0; i < width * height; i++)
		{
			weights[i * 4] = 255;
		}

		var layerMips = TerrainLayerMapUtility.GenerateLayerMipChain(
			new TextureMipData(width, height, indices),
			new TextureMipData(width, height, weights));

		return new TerrainAsset(
			name,
			new Texture($"{name}:height", width, height, false, TextureFormat.R16Unorm, [new TextureMipData(width, height, heightData)]),
			new Texture($"{name}:indices", width, height, false, TextureFormat.Rgba8Uint, layerMips.Indices),
			new Texture($"{name}:weights", width, height, false, TextureFormat.Rgba8Unorm, layerMips.Weights));
	}

	private static ushort ReadR16(byte[] data, int pixelIndex)
	{
		var offset = pixelIndex * 2;
		return (ushort)(data[offset] | (data[offset + 1] << 8));
	}

	private static void WriteR16(byte[] data, int pixelIndex, ushort value)
	{
		var offset = pixelIndex * 2;
		data[offset] = (byte)(value & 0xff);
		data[offset + 1] = (byte)(value >> 8);
	}

	private sealed class TestTerrainAssetPersistenceService : ITerrainAssetPersistenceService
	{
		public readonly Dictionary<Guid, TerrainAssetSnapshot> States = new();

		public void RecordPendingTerrainAssetState(IReadOnlyList<TerrainAssetSnapshot> snapshots)
		{
			for (var i = 0; i < snapshots.Count; i++)
			{
				States[snapshots[i].AssetId] = snapshots[i];
			}
		}

		public void ApplyTerrainAssetStates(IReadOnlyList<TerrainAssetSnapshot> snapshots)
		{
			for (var i = 0; i < snapshots.Count; i++)
			{
				var snapshot = snapshots[i];
				var terrainAsset = AssetDatabase.GetInstance<TerrainAsset>(snapshot.AssetId);
				terrainAsset?.ApplyMaps(snapshot.Heightmap, snapshot.LayerIndexMap, snapshot.LayerWeightMap);
				States[snapshot.AssetId] = snapshot;
			}
		}

		public void SaveDirtyTerrainAssets()
		{
		}
	}

	private sealed class TestTerrainBrushGpuExecutor : ITerrainBrushGpuExecutor
	{
		public TerrainGpuStrokePreviewSet CreateStrokeResources(Texture sourceTexture, TerrainAuthoringSurfaceTarget surfaceTarget)
		{
			var current = CreateHeightPreview($"{sourceTexture.Name}__test_preview_current", sourceTexture);
			var scratch = CreateHeightPreview($"{sourceTexture.Name}__test_preview_scratch", sourceTexture);
			return new TerrainGpuStrokePreviewSet(current, scratch);
		}

		public void ApplyStamp(in TerrainGpuBrushDispatch dispatch)
		{
			var sourceData = dispatch.InputTexture.MipLevels[0].Data;
			var outputData = sourceData.ToArray();
			var centerX = Math.Clamp((int)MathF.Round(dispatch.BrushCenterPixels.X), 0, dispatch.InputTexture.Width - 1);
			var centerY = Math.Clamp((int)MathF.Round(dispatch.BrushCenterPixels.Y), 0, dispatch.InputTexture.Height - 1);
			var pixelIndex = (centerY * dispatch.InputTexture.Width) + centerX;

			if (dispatch.Request.Operation == TerrainBrushOperation.RaiseLower)
			{
				var current = ReadHalf(outputData, pixelIndex * 8);
				var delta = dispatch.Strength * (dispatch.Modifiers.Invert ? -0.25f : 0.25f);
				WriteHalf(outputData, pixelIndex * 8, Math.Clamp(current + delta, 0.0f, 1.0f));
			}

			var sourceTopMip = dispatch.InputTexture.MipLevels[0];
			dispatch.OutputTexture.ApplyTextureData(
				dispatch.OutputTexture.Width,
				dispatch.OutputTexture.Height,
				dispatch.OutputTexture.IsSrgb,
				dispatch.OutputTexture.Format,
				[new TextureMipData(sourceTopMip.Width, sourceTopMip.Height, outputData)]);
		}

		public byte[] ReadTopMip(Texture texture)
		{
			return texture.MipLevels[0].Data.ToArray();
		}

		public void RefreshTextureResources(Texture texture)
		{
		}

		public void SynchronizePreviewTexture(Texture previewTexture, Texture sourceTexture, TerrainAuthoringSurfaceTarget surfaceTarget)
		{
			var synchronized = CreateHeightPreview(previewTexture.Name, sourceTexture);
			previewTexture.ApplyTextureData(
				synchronized.Width,
				synchronized.Height,
				synchronized.IsSrgb,
				synchronized.Format,
				synchronized.MipLevels);
		}

		private static Texture CreateHeightPreview(string name, Texture source)
		{
			var previewData = new byte[source.Width * source.Height * 8];
			for (var i = 0; i < source.Width * source.Height; i++)
			{
				var normalized = ReadR16(source.MipLevels[0].Data, i) / 65535.0f;
				WriteHalf(previewData, i * 8, normalized);
				WriteHalf(previewData, i * 8 + 2, normalized);
				WriteHalf(previewData, i * 8 + 4, normalized);
				WriteHalf(previewData, i * 8 + 6, 1.0f);
			}

			return new Texture(name, source.Width, source.Height, false, TextureFormat.Rgba16Float, [new TextureMipData(source.Width, source.Height, previewData)]);
		}

		private static float ReadHalf(byte[] data, int offset)
		{
			return (float)BitConverter.UInt16BitsToHalf((ushort)(data[offset] | (data[offset + 1] << 8)));
		}

		private static void WriteHalf(byte[] data, int offset, float value)
		{
			var half = (ushort)BitConverter.HalfToUInt16Bits((Half)value);
			data[offset] = (byte)(half & 0xff);
			data[offset + 1] = (byte)(half >> 8);
		}
	}

	private sealed class TestAssetRegistry : IAssetInstanceRegistry, IDisposable
	{
		private readonly Dictionary<Guid, object> _instances = new();

		public TestAssetRegistry()
		{
			AssetDatabase.SetInstanceRegistry(this);
		}

		public void Register(Guid assetId, object instance)
		{
			_instances[assetId] = instance;
		}

		public object? GetInstance(Guid assetId, Type expectedType)
		{
			return _instances.TryGetValue(assetId, out var instance) && expectedType.IsInstanceOfType(instance)
				? instance
				: null;
		}

		public void RefreshProject(string projectRootPath, AssetDatabase database)
		{
		}

		public void InvalidateAssets(IEnumerable<Guid> assetIds)
		{
		}

		public void Clear()
		{
			_instances.Clear();
		}

		public void ClearCachedInstances()
		{
		}

		public void Dispose()
		{
			AssetDatabase.ClearInstanceRegistry();
		}
	}
}
