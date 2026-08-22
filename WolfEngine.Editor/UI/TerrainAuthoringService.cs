using System.Numerics;
using WolfEngine.Editor.Projects;
using WolfEngine.ECS;
using WolfEngine.Rendering;

namespace WolfEngine.Editor.UI;

public enum TerrainAuthoringSurfaceTarget
{
	Heightmap,
	LayerMaps
}

public enum TerrainBrushOperation
{
	RaiseLower,
	Flatten,
	Smooth,
	PaintLayer
}

public readonly record struct TerrainBrushSettings(
	float RadiusMeters,
	float Strength,
	float Falloff,
	int LayerIndex,
	float? FlattenHeightNormalized);

public readonly record struct TerrainBrushModifierState(bool Invert);

public readonly record struct TerrainBrushStrokeRequest(
	TerrainAuthoringSurfaceTarget SurfaceTarget,
	TerrainBrushOperation Operation,
	TerrainBrushSettings Settings);

public interface ITerrainAuthoringService
{
	bool HasActiveStroke { get; }
	bool BeginStroke(EditorScene scene, Entity terrainEntity, TerrainBrushStrokeRequest request);
	void AppendStamp(Vector3 localPosition, float pressure, TerrainBrushModifierState modifiers);
	bool EndStroke();
	void CancelStroke();
}

public sealed class TerrainAuthoringService : ITerrainAuthoringService
{
	private readonly IEditorUndoRedoService _undoRedoService;
	private readonly IEditorInteractionState _interactionState;
	private readonly ITerrainAssetPersistenceService _terrainAssetPersistenceService;
	private readonly ITerrainTexturePreviewRegistry _terrainTexturePreviewRegistry;
	private readonly ITerrainBrushGpuExecutor _terrainBrushGpuExecutor;
	private StrokeState? _activeStroke;

	public TerrainAuthoringService(
		IEditorUndoRedoService undoRedoService,
		IEditorInteractionState interactionState,
		ITerrainAssetPersistenceService terrainAssetPersistenceService,
		ITerrainTexturePreviewRegistry terrainTexturePreviewRegistry,
		ITerrainBrushGpuExecutor terrainBrushGpuExecutor)
	{
		_undoRedoService = undoRedoService ?? throw new ArgumentNullException(nameof(undoRedoService));
		_interactionState = interactionState ?? throw new ArgumentNullException(nameof(interactionState));
		_terrainAssetPersistenceService = terrainAssetPersistenceService ?? throw new ArgumentNullException(nameof(terrainAssetPersistenceService));
		_terrainTexturePreviewRegistry = terrainTexturePreviewRegistry ?? throw new ArgumentNullException(nameof(terrainTexturePreviewRegistry));
		_terrainBrushGpuExecutor = terrainBrushGpuExecutor ?? throw new ArgumentNullException(nameof(terrainBrushGpuExecutor));
	}

	public bool HasActiveStroke => _activeStroke is not null;

	public bool BeginStroke(EditorScene scene, Entity terrainEntity, TerrainBrushStrokeRequest request)
	{
		ArgumentNullException.ThrowIfNull(scene);
		CancelStroke();

		var world = scene.World;
		if (world.IsAlive(terrainEntity) == false || world.HasComponent<TerrainComponent>(terrainEntity) == false)
		{
			return false;
		}

		ref var terrain = ref world.GetComponent<TerrainComponent>(terrainEntity);
		var terrainAsset = terrain.TerrainAsset.Asset;
		var terrainAssetId = terrain.TerrainAsset.NodeId;
		if (terrainAsset is null || terrainAssetId == Guid.Empty)
		{
			return false;
		}

		request = NormalizeStrokeRequest(in terrain, request);
		var beforeSnapshot = terrainAsset.CaptureSnapshot(terrainAssetId);
		if (request.SurfaceTarget == TerrainAuthoringSurfaceTarget.Heightmap)
		{
			var previewSet = _terrainBrushGpuExecutor.CreateStrokeResources(terrain.AuthoringPreviewHeightmap ?? terrainAsset.Heightmap, request.SurfaceTarget);
			if (terrain.AuthoringPreviewHeightmap is not null)
			{
				_terrainTexturePreviewRegistry.UnregisterPreview(terrainAssetId, request.SurfaceTarget, terrain.AuthoringPreviewHeightmap);
			}

			terrain.AuthoringPreviewHeightmap = previewSet.CurrentPreviewTexture;
			_terrainTexturePreviewRegistry.RegisterPreview(terrainAssetId, request.SurfaceTarget, previewSet.CurrentPreviewTexture);
			_activeStroke = StrokeState.ForHeight(scene, terrainEntity, request, terrainAsset, terrainAssetId, beforeSnapshot, previewSet.CurrentPreviewTexture, previewSet.ScratchPreviewTexture);
			return true;
		}

		if (request.Operation != TerrainBrushOperation.PaintLayer)
		{
			return false;
		}

		terrain.AuthoringPreviewLayerIndexMap = null;
		terrain.AuthoringPreviewLayerWeightMap = null;
		_activeStroke = StrokeState.ForLayerMaps(scene, terrainEntity, request, terrainAsset, terrainAssetId, beforeSnapshot);
		return true;
	}

	private static TerrainBrushStrokeRequest NormalizeStrokeRequest(in TerrainComponent terrain, TerrainBrushStrokeRequest request)
	{
		if (request.Operation != TerrainBrushOperation.PaintLayer)
		{
			return request;
		}

		var layerCount = terrain.LayerSetAsset.Asset?.ResolvedLayerCount ?? 4;
		var maxLayerIndex = Math.Clamp(layerCount, 0, TerrainLayerSet.MaxLayerCount);
		var layerIndex = Math.Clamp(request.Settings.LayerIndex, 0, maxLayerIndex);
		return request with
		{
			Settings = request.Settings with
			{
				LayerIndex = layerIndex
			}
		};
	}

	public void AppendStamp(Vector3 localPosition, float pressure, TerrainBrushModifierState modifiers)
	{
		var stroke = _activeStroke;
		if (stroke is null)
		{
			return;
		}

		var world = stroke.Scene.World;
		if (world.IsAlive(stroke.TerrainEntity) == false || world.HasComponent<TerrainComponent>(stroke.TerrainEntity) == false)
		{
			CancelStroke();
			return;
		}

		ref var terrain = ref world.GetComponent<TerrainComponent>(stroke.TerrainEntity);
		var targetTexture = stroke.Request.SurfaceTarget == TerrainAuthoringSurfaceTarget.Heightmap
			? stroke.CurrentPreviewTexture
			: stroke.TerrainAsset.LayerWeightMap;
		if (targetTexture is null)
		{
			return;
		}

		var radius = MathF.Max(stroke.Request.Settings.RadiusMeters, 0.1f);
		var strength = Math.Clamp(stroke.Request.Settings.Strength, 0.0f, 1.0f) * Math.Clamp(pressure <= 0.0f ? 1.0f : pressure, 0.0f, 1.0f);
		if (stroke.Request.Operation == TerrainBrushOperation.RaiseLower)
		{
			strength *= 0.1f;
		}

		if (strength <= 0.0f ||
		    TryBuildBrushPlacement(in terrain, targetTexture.Width, targetTexture.Height, localPosition, radius, out var placement) == false)
		{
			return;
		}

		if (stroke.Request.Operation == TerrainBrushOperation.PaintLayer)
		{
			ApplyLayerStamp(stroke, placement, strength, modifiers);
			return;
		}

		if (stroke.Request.Operation == TerrainBrushOperation.Flatten && stroke.FlattenHeightNormalized.HasValue == false)
		{
			stroke.FlattenHeightNormalized = SampleHeight(stroke.TerrainAsset.Heightmap, (int)MathF.Round(placement.CenterPixels.X), (int)MathF.Round(placement.CenterPixels.Y));
		}

		stroke.AccumulateHeightDirtyRegion(CreateDirtyRegion(placement, targetTexture.Width, targetTexture.Height));
		_terrainBrushGpuExecutor.ApplyStamp(new TerrainGpuBrushDispatch(
			stroke.Request,
			modifiers,
			stroke.CurrentPreviewTexture!,
			stroke.ScratchPreviewTexture!,
			strength,
			placement.CenterPixels,
			placement.RadiusPixels,
			stroke.FlattenHeightNormalized));
		_terrainTexturePreviewRegistry.UnregisterPreview(stroke.TerrainAssetId, stroke.Request.SurfaceTarget, stroke.CurrentPreviewTexture!);
		stroke.SwapHeightPreviewTextures();
		terrain.AuthoringPreviewHeightmap = stroke.CurrentPreviewTexture;
		_terrainTexturePreviewRegistry.RegisterPreview(stroke.TerrainAssetId, stroke.Request.SurfaceTarget, stroke.CurrentPreviewTexture!);
	}

	public bool EndStroke()
	{
		var stroke = _activeStroke;
		if (stroke is null)
		{
			return false;
		}

		var world = stroke.Scene.World;
		if (world.IsAlive(stroke.TerrainEntity) == false || world.HasComponent<TerrainComponent>(stroke.TerrainEntity) == false)
		{
			_activeStroke = null;
			return false;
		}

		ref var terrain = ref world.GetComponent<TerrainComponent>(stroke.TerrainEntity);
		if (stroke.Request.SurfaceTarget == TerrainAuthoringSurfaceTarget.Heightmap)
		{
			var previewTexture = stroke.CurrentPreviewTexture!;
			var topMip = new TextureMipData(
				previewTexture.Width,
				previewTexture.Height,
				ConvertHeightReadbackToR16(_terrainBrushGpuExecutor.ReadTopMip(previewTexture), previewTexture.Format, previewTexture.Width, previewTexture.Height));
			stroke.TerrainAsset.Heightmap.ApplyTextureData(previewTexture.Width, previewTexture.Height, false, TextureFormat.R16Unorm, [topMip]);
			_terrainBrushGpuExecutor.SynchronizePreviewTexture(previewTexture, stroke.TerrainAsset.Heightmap, stroke.Request.SurfaceTarget);
			terrain.AuthoringPreviewHeightmap = previewTexture;
			if (stroke.HeightDirtyRegion.HasValue)
			{
				TerrainRuntimeRegistry.MarkHeightmapEdited(world, stroke.TerrainEntity, stroke.HeightDirtyRegion.Value);
			}
		}
		else
		{
			var baseIndices = stroke.TerrainAsset.LayerIndexMap.MipLevels[0];
			var baseWeights = stroke.TerrainAsset.LayerWeightMap.MipLevels[0];
			var mips = TerrainLayerMapUtility.GenerateLayerMipChain(baseIndices, baseWeights);
			stroke.TerrainAsset.LayerIndexMap.ApplyTextureData(baseIndices.Width, baseIndices.Height, false, TextureFormat.Rgba8Uint, mips.Indices);
			stroke.TerrainAsset.LayerWeightMap.ApplyTextureData(baseWeights.Width, baseWeights.Height, false, TextureFormat.Rgba8Unorm, mips.Weights);
			terrain.AuthoringPreviewLayerIndexMap = null;
			terrain.AuthoringPreviewLayerWeightMap = null;
		}

		var afterSnapshot = stroke.TerrainAsset.CaptureSnapshot(stroke.TerrainAssetId);
		var snapshots = new[] { afterSnapshot };
		_terrainAssetPersistenceService.RecordPendingTerrainAssetState(snapshots);
		_undoRedoService.BeginCapture("Terrain Stroke");
		_undoRedoService.CommitCapture(new TerrainAssetEditUndoRedoEntry("Terrain Stroke", [stroke.BeforeSnapshot], snapshots));
		_interactionState.MarkSceneDirty();
		_activeStroke = null;
		return true;
	}

	public void CancelStroke()
	{
		var stroke = _activeStroke;
		if (stroke is null)
		{
			return;
		}

		var world = stroke.Scene.World;
		if (world.IsAlive(stroke.TerrainEntity) && world.HasComponent<TerrainComponent>(stroke.TerrainEntity))
		{
			ref var terrain = ref world.GetComponent<TerrainComponent>(stroke.TerrainEntity);
			if (stroke.Request.SurfaceTarget == TerrainAuthoringSurfaceTarget.Heightmap)
			{
				terrain.AuthoringPreviewHeightmap = null;
			}
			else
			{
				stroke.TerrainAsset.ApplyMaps(
					stroke.BeforeSnapshot.Heightmap,
					stroke.BeforeSnapshot.LayerIndexMap,
					stroke.BeforeSnapshot.LayerWeightMap);
				terrain.AuthoringPreviewLayerIndexMap = null;
				terrain.AuthoringPreviewLayerWeightMap = null;
			}
		}

		_activeStroke = null;
	}

	private static void ApplyLayerStamp(StrokeState stroke, BrushPlacement placement, float strength, TerrainBrushModifierState modifiers)
	{
		var indexMap = stroke.TerrainAsset.LayerIndexMap;
		var weightMap = stroke.TerrainAsset.LayerWeightMap;
		var indexMip = indexMap.MipLevels[0];
		var weightMip = weightMap.MipLevels[0];
		var targetLayer = (byte)Math.Clamp(stroke.Request.Settings.LayerIndex, 0, 255);
		var minX = Math.Clamp((int)MathF.Floor(placement.CenterPixels.X - placement.RadiusPixels.X), 0, indexMip.Width - 1);
		var maxX = Math.Clamp((int)MathF.Ceiling(placement.CenterPixels.X + placement.RadiusPixels.X), 0, indexMip.Width - 1);
		var minY = Math.Clamp((int)MathF.Floor(placement.CenterPixels.Y - placement.RadiusPixels.Y), 0, indexMip.Height - 1);
		var maxY = Math.Clamp((int)MathF.Ceiling(placement.CenterPixels.Y + placement.RadiusPixels.Y), 0, indexMip.Height - 1);

		for (var y = minY; y <= maxY; y++)
		{
			for (var x = minX; x <= maxX; x++)
			{
				var normalizedOffset = new Vector2(x, y) - placement.CenterPixels;
				normalizedOffset.X /= MathF.Max(placement.RadiusPixels.X, 0.001f);
				normalizedOffset.Y /= MathF.Max(placement.RadiusPixels.Y, 0.001f);
				var distance = normalizedOffset.Length();
				if (distance >= 1.0f)
				{
					continue;
				}

				var brushWeight = MathF.Pow(1.0f - distance, MathF.Max(stroke.Request.Settings.Falloff, 0.1f));
				var delta = (int)MathF.Round(strength * brushWeight * 255.0f) * (modifiers.Invert ? -1 : 1);
				if (delta == 0)
				{
					continue;
				}

				var pixelIndex = (y * indexMip.Width) + x;
				ApplyLayerDelta(indexMip.Data, weightMip.Data, pixelIndex, targetLayer, delta);
			}
		}

		indexMap.ApplyTextureData(indexMap.Width, indexMap.Height, indexMap.IsSrgb, indexMap.Format, indexMap.MipLevels);
		weightMap.ApplyTextureData(weightMap.Width, weightMap.Height, weightMap.IsSrgb, weightMap.Format, weightMap.MipLevels);
	}

	private static void ApplyLayerDelta(byte[] indices, byte[] weights, int pixelIndex, byte targetLayer, int delta)
	{
		var offset = pixelIndex * 4;
		var slot = -1;
		var lowestSlot = 0;
		for (var i = 0; i < 4; i++)
		{
			if (indices[offset + i] == targetLayer)
			{
				if (slot < 0)
				{
					slot = i;
				}
				else
				{
					weights[offset + i] = 0;
				}
			}

			if (weights[offset + i] < weights[offset + lowestSlot])
			{
				lowestSlot = i;
			}
		}

		if (slot < 0)
		{
			if (delta <= 0)
			{
				return;
			}

			slot = lowestSlot;
			indices[offset + slot] = targetLayer;
			weights[offset + slot] = 0;
		}

		var targetWeight = Math.Clamp(weights[offset + slot] + delta, 0, 255);
		ApplyTargetLayerWeight(indices, weights, pixelIndex, slot, targetLayer, targetWeight);
	}

	private static void ApplyTargetLayerWeight(byte[] indices, byte[] weights, int pixelIndex, int targetSlot, byte targetLayer, int targetWeight)
	{
		var offset = pixelIndex * 4;
		indices[offset + targetSlot] = targetLayer;
		weights[offset + targetSlot] = (byte)targetWeight;

		var remaining = 255 - targetWeight;
		if (remaining <= 0)
		{
			for (var i = 0; i < 4; i++)
			{
				if (i != targetSlot)
				{
					weights[offset + i] = 0;
				}
			}

			return;
		}

		var otherWeightSum = 0;
		for (var i = 0; i < 4; i++)
		{
			if (i != targetSlot)
			{
				otherWeightSum += weights[offset + i];
			}
		}

		if (otherWeightSum <= 0)
		{
			var fallbackSlot = targetSlot == 0 ? 1 : 0;
			indices[offset + fallbackSlot] = 0;
			weights[offset + fallbackSlot] = (byte)remaining;
			for (var i = 0; i < 4; i++)
			{
				if (i != targetSlot && i != fallbackSlot)
				{
					weights[offset + i] = 0;
				}
			}

			return;
		}

		var distributed = 0;
		var lastWeightedSlot = -1;
		for (var i = 0; i < 4; i++)
		{
			if (i == targetSlot)
			{
				continue;
			}

			if (weights[offset + i] > 0)
			{
				lastWeightedSlot = i;
			}
		}

		for (var i = 0; i < 4; i++)
		{
			if (i == targetSlot)
			{
				continue;
			}

			var value = weights[offset + i] <= 0
				? 0
				: i == lastWeightedSlot
					? remaining - distributed
					: Math.Clamp((int)MathF.Round(weights[offset + i] / (float)otherWeightSum * remaining), 0, remaining - distributed);
			weights[offset + i] = (byte)value;
			distributed += value;
		}

		TerrainLayerMapUtility.NormalizePixel(indices, weights, pixelIndex);
	}

	private static bool TryBuildBrushPlacement(
		in TerrainComponent terrain,
		int textureWidth,
		int textureHeight,
		Vector3 localPosition,
		float radiusMeters,
		out BrushPlacement placement)
	{
		var worldSize = terrain.GetResolvedWorldSize();
		var halfWidth = worldSize.X * 0.5f;
		var halfDepth = worldSize.Y * 0.5f;
		var u = (localPosition.X + halfWidth) / Math.Max(worldSize.X, 0.001f);
		var v = (localPosition.Z + halfDepth) / Math.Max(worldSize.Y, 0.001f);
		if (u < 0.0f || u > 1.0f || v < 0.0f || v > 1.0f)
		{
			placement = default;
			return false;
		}

		placement = new BrushPlacement(
			new Vector2(u * Math.Max(textureWidth - 1, 1), v * Math.Max(textureHeight - 1, 1)),
			new Vector2(
				MathF.Max(radiusMeters / Math.Max(worldSize.X, 0.001f) * Math.Max(textureWidth - 1, 1), 0.001f),
				MathF.Max(radiusMeters / Math.Max(worldSize.Y, 0.001f) * Math.Max(textureHeight - 1, 1), 0.001f)));
		return true;
	}

	private static float SampleHeight(Texture texture, int x, int y)
	{
		var mip = texture.MipLevels[0];
		x = Math.Clamp(x, 0, mip.Width - 1);
		y = Math.Clamp(y, 0, mip.Height - 1);
		var offset = ((y * mip.Width) + x) * 2;
		return (mip.Data[offset] | (mip.Data[offset + 1] << 8)) / 65535.0f;
	}

	private static TerrainHeightmapDirtyRegion CreateDirtyRegion(BrushPlacement placement, int textureWidth, int textureHeight)
	{
		var minX = (int)MathF.Floor(placement.CenterPixels.X - placement.RadiusPixels.X) - 1;
		var minY = (int)MathF.Floor(placement.CenterPixels.Y - placement.RadiusPixels.Y) - 1;
		var maxX = (int)MathF.Ceiling(placement.CenterPixels.X + placement.RadiusPixels.X) + 1;
		var maxY = (int)MathF.Ceiling(placement.CenterPixels.Y + placement.RadiusPixels.Y) + 1;
		return new TerrainHeightmapDirtyRegion(minX, minY, maxX - minX + 1, maxY - minY + 1, textureWidth, textureHeight);
	}

	private static byte[] ConvertHeightReadbackToR16(byte[] source, TextureFormat sourceFormat, int width, int height)
	{
		var expectedByteCount = TextureFormatUtilities.GetMipDataSize(sourceFormat, width, height);
		if (source.Length < expectedByteCount)
		{
			throw new InvalidOperationException(
				$"Terrain height readback for {sourceFormat} expected at least {expectedByteCount} bytes for {width}x{height}, but got {source.Length}.");
		}

		if (sourceFormat == TextureFormat.R16Unorm)
		{
			return source.ToArray();
		}

		var result = new byte[width * height * 2];
		for (var pixelIndex = 0; pixelIndex < width * height; pixelIndex++)
		{
			float normalized = sourceFormat == TextureFormat.Rgba16Float
				? (float)BitConverter.UInt16BitsToHalf((ushort)(source[pixelIndex * 8] | (source[pixelIndex * 8 + 1] << 8)))
				: (sourceFormat == TextureFormat.Bgra8Unorm
					? source[pixelIndex * 4 + 2]
					: source[pixelIndex * 4]) / 255.0f;
			var encoded = (ushort)Math.Clamp((int)MathF.Round(Math.Clamp(normalized, 0.0f, 1.0f) * 65535.0f), 0, 65535);
			result[pixelIndex * 2] = (byte)(encoded & 0xff);
			result[pixelIndex * 2 + 1] = (byte)(encoded >> 8);
		}

		return result;
	}

	private sealed class StrokeState
	{
		private StrokeState(EditorScene scene, Entity terrainEntity, TerrainBrushStrokeRequest request, TerrainAsset terrainAsset, Guid terrainAssetId, TerrainAssetSnapshot beforeSnapshot)
		{
			Scene = scene;
			TerrainEntity = terrainEntity;
			Request = request;
			TerrainAsset = terrainAsset;
			TerrainAssetId = terrainAssetId;
			BeforeSnapshot = beforeSnapshot;
			FlattenHeightNormalized = request.Settings.FlattenHeightNormalized;
		}

		public EditorScene Scene { get; }
		public Entity TerrainEntity { get; }
		public TerrainBrushStrokeRequest Request { get; }
		public TerrainAsset TerrainAsset { get; }
		public Guid TerrainAssetId { get; }
		public TerrainAssetSnapshot BeforeSnapshot { get; }
		public Texture? CurrentPreviewTexture { get; private set; }
		public Texture? ScratchPreviewTexture { get; private set; }
		public float? FlattenHeightNormalized { get; set; }
		public TerrainHeightmapDirtyRegion? HeightDirtyRegion { get; private set; }

		public static StrokeState ForHeight(EditorScene scene, Entity terrainEntity, TerrainBrushStrokeRequest request, TerrainAsset terrainAsset, Guid terrainAssetId, TerrainAssetSnapshot beforeSnapshot, Texture currentPreview, Texture scratchPreview)
		{
			return new StrokeState(scene, terrainEntity, request, terrainAsset, terrainAssetId, beforeSnapshot)
			{
				CurrentPreviewTexture = currentPreview,
				ScratchPreviewTexture = scratchPreview
			};
		}

		public static StrokeState ForLayerMaps(EditorScene scene, Entity terrainEntity, TerrainBrushStrokeRequest request, TerrainAsset terrainAsset, Guid terrainAssetId, TerrainAssetSnapshot beforeSnapshot)
		{
			return new StrokeState(scene, terrainEntity, request, terrainAsset, terrainAssetId, beforeSnapshot);
		}

		public void SwapHeightPreviewTextures()
		{
			(CurrentPreviewTexture, ScratchPreviewTexture) = (ScratchPreviewTexture, CurrentPreviewTexture);
		}

		public void AccumulateHeightDirtyRegion(in TerrainHeightmapDirtyRegion dirtyRegion)
		{
			if (dirtyRegion.IsEmpty)
			{
				return;
			}

			HeightDirtyRegion = HeightDirtyRegion.HasValue
				? TerrainHeightmapDirtyRegion.Union(HeightDirtyRegion.Value, dirtyRegion)
				: dirtyRegion;
		}
	}

	private readonly record struct BrushPlacement(Vector2 CenterPixels, Vector2 RadiusPixels);
}
