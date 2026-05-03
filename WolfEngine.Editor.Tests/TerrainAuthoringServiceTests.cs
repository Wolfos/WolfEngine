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
	public void CancelStroke_KeepsSourceTextureUnchanged()
	{
		using var registry = new TestAssetRegistry();
		var heightmapId = Guid.NewGuid();
		var source = CreateTexture("height", 5, 5, 10);
		registry.Register(heightmapId, source);
		var scene = CreateScene(heightmapId, Guid.Empty);
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

		Assert.That(source.MipLevels[0].Data[0], Is.EqualTo(10));
		ref var terrain = ref scene.World.GetComponent<TerrainComponent>(GetTerrainEntity(scene));
		Assert.That(terrain.AuthoringPreviewHeightmap, Is.Null);
	}

	[Test]
	public void EndStroke_CommitsHeightmapOnceAndMarksSceneDirty()
	{
		using var registry = new TestAssetRegistry();
		var heightmapId = Guid.NewGuid();
		var source = CreateTexture("height", 5, 5, 10);
		registry.Register(heightmapId, source);
		var scene = CreateScene(heightmapId, Guid.Empty);
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

		var centerOffset = ((2 * 5) + 2) * 4;
		Assert.That(source.MipLevels[0].Data[centerOffset], Is.GreaterThan(10));
		Assert.That(interactionState.IsSceneDirty, Is.True);
		undoRedo.Received(1).CommitCapture(Arg.Any<IEditorUndoRedoEntry>());
	}

	[Test]
	public void EndStroke_PaintsRequestedControlChannel()
	{
		using var registry = new TestAssetRegistry();
		var controlMapId = Guid.NewGuid();
		var heightmapId = Guid.NewGuid();
		registry.Register(heightmapId, CreateTexture("height", 5, 5, 0));
		var source = CreateColorTexture("control", 5, 5, 0, 0, 0, 0);
		registry.Register(controlMapId, source);
		var scene = CreateScene(heightmapId, controlMapId);
		var service = CreateService(out _, out _);

		service.BeginStroke(
			scene,
			GetTerrainEntity(scene),
			new TerrainBrushStrokeRequest(
				TerrainAuthoringSurfaceTarget.ControlMap,
				TerrainBrushOperation.PaintLayer,
				new TerrainBrushSettings(8.0f, 1.0f, 1.0f, 2, null)));
		service.AppendStamp(Vector3.Zero, 1.0f, new TerrainBrushModifierState(false));
		service.EndStroke();

		var centerOffset = ((2 * 5) + 2) * 4;
		Assert.That(source.MipLevels[0].Data[centerOffset + 2], Is.GreaterThan(0));
		Assert.That(source.MipLevels[0].Data[centerOffset], Is.EqualTo(0));
		Assert.That(source.MipLevels[0].Data[centerOffset + 1], Is.EqualTo(0));
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
			new TestTerrainTexturePersistenceService(),
			new TestTerrainBrushGpuExecutor());
	}

	private static EditorScene CreateScene(Guid heightmapId, Guid controlMapId)
	{
		var world = new World(WorldTag.Authoring);
		var terrainEntity = world.CreateEntity("Terrain");
		world.AddTransform(terrainEntity, Matrix4x4.Identity);
		world.AddComponent(terrainEntity, new TerrainComponent
		{
			HeightmapAsset = new AssetRef<Texture> { NodeId = heightmapId },
			ControlMapAsset = new AssetRef<Texture> { NodeId = controlMapId },
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

	private static Texture CreateTexture(string name, int width, int height, byte heightValue)
	{
		var data = new byte[width * height * 4];
		for (var i = 0; i < width * height; i++)
		{
			data[i * 4] = heightValue;
			data[(i * 4) + 3] = 255;
		}

		return new Texture(name, width, height, false, TextureFormat.Rgba8Unorm, [new TextureMipData(width, height, data)]);
	}

	private static Texture CreateColorTexture(string name, int width, int height, byte r, byte g, byte b, byte a)
	{
		var data = new byte[width * height * 4];
		for (var i = 0; i < width * height; i++)
		{
			var offset = i * 4;
			data[offset] = r;
			data[offset + 1] = g;
			data[offset + 2] = b;
			data[offset + 3] = a;
		}

		return new Texture(name, width, height, false, TextureFormat.Rgba8Unorm, [new TextureMipData(width, height, data)]);
	}

	private sealed class TestTerrainTexturePersistenceService : ITerrainTexturePersistenceService
	{
		public readonly Dictionary<Guid, TerrainTextureStateSnapshot> States = new();

		public void RecordPendingTextureState(IReadOnlyList<TerrainTextureStateSnapshot> snapshots)
		{
			for (var i = 0; i < snapshots.Count; i++)
			{
				States[snapshots[i].AssetId] = snapshots[i];
			}
		}

		public void ApplyTextureStates(IReadOnlyList<TerrainTextureStateSnapshot> snapshots)
		{
			for (var i = 0; i < snapshots.Count; i++)
			{
				var snapshot = snapshots[i];
				var texture = AssetDatabase.GetInstance<Texture>(snapshot.AssetId);
				texture?.ApplyTextureData(snapshot.Width, snapshot.Height, snapshot.IsSrgb, snapshot.Format, snapshot.MipLevels);
				States[snapshot.AssetId] = snapshot;
			}
		}

		public void SaveDirtyTextures()
		{
		}
	}

	private sealed class TestTerrainBrushGpuExecutor : ITerrainBrushGpuExecutor
	{
		public TerrainGpuStrokePreviewSet CreateStrokeResources(Texture sourceTexture, TerrainAuthoringSurfaceTarget surfaceTarget)
		{
			return new TerrainGpuStrokePreviewSet(
				CloneTexture($"{sourceTexture.Name}__test_preview_current", sourceTexture),
				CloneTexture($"{sourceTexture.Name}__test_preview_scratch", sourceTexture));
		}

		public void ApplyStamp(in TerrainGpuBrushDispatch dispatch)
		{
			var sourceData = dispatch.InputTexture.MipLevels[0].Data;
			var outputData = sourceData.ToArray();
			var centerX = Math.Clamp((int)MathF.Round(dispatch.BrushCenterPixels.X), 0, dispatch.InputTexture.Width - 1);
			var centerY = Math.Clamp((int)MathF.Round(dispatch.BrushCenterPixels.Y), 0, dispatch.InputTexture.Height - 1);
			var index = ((centerY * dispatch.InputTexture.Width) + centerX) * 4;

			switch (dispatch.Request.Operation)
			{
				case TerrainBrushOperation.RaiseLower:
				{
					var current = outputData[index] / 255.0f;
					var delta = dispatch.Strength * (dispatch.Modifiers.Invert ? -0.25f : 0.25f);
					outputData[index] = EncodeNormalized(current + delta);
					break;
				}
				case TerrainBrushOperation.Flatten:
				{
					outputData[index] = EncodeNormalized(dispatch.FlattenHeightNormalized ?? 0.0f);
					break;
				}
				case TerrainBrushOperation.Smooth:
				{
					outputData[index] = sourceData[index];
					break;
				}
				case TerrainBrushOperation.PaintLayer:
				{
					var channel = Math.Clamp(dispatch.Request.Settings.LayerIndex, 0, 3);
					var channelIndex = index + channel;
					var current = outputData[channelIndex] / 255.0f;
					var delta = dispatch.Strength * (dispatch.Modifiers.Invert ? -1.0f : 1.0f);
					outputData[channelIndex] = EncodeNormalized(current + delta);
					break;
				}
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

		private static Texture CloneTexture(string name, Texture source)
		{
			var mipLevels = new TextureMipData[source.MipLevels.Length];
			for (var i = 0; i < source.MipLevels.Length; i++)
			{
				var mip = source.MipLevels[i];
				mipLevels[i] = new TextureMipData(mip.Width, mip.Height, mip.Data.ToArray());
			}

			return new Texture(name, source.Width, source.Height, source.IsSrgb, source.Format, mipLevels);
		}

		private static byte EncodeNormalized(float value)
		{
			return (byte)Math.Clamp((int)MathF.Round(Math.Clamp(value, 0.0f, 1.0f) * 255.0f), 0, 255);
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
