using System;
using System.Collections.Generic;
using System.Numerics;
using WolfEngine.AssetPipeline;
using WolfEngine.Editor.Projects;
using WolfEngine.ECS;
using WolfEngine.Rendering;

namespace WolfEngine.Editor.UI;

public enum TerrainAuthoringSurfaceTarget
{
	Heightmap,
	ControlMap
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
	private readonly ITerrainTexturePersistenceService _terrainTexturePersistenceService;
	private readonly ITerrainBrushGpuExecutor _terrainBrushGpuExecutor;
	private StrokeState? _activeStroke;

	public TerrainAuthoringService(
		IEditorUndoRedoService undoRedoService,
		IEditorInteractionState interactionState,
		ITerrainTexturePersistenceService terrainTexturePersistenceService,
		ITerrainBrushGpuExecutor terrainBrushGpuExecutor)
	{
		_undoRedoService = undoRedoService ?? throw new ArgumentNullException(nameof(undoRedoService));
		_interactionState = interactionState ?? throw new ArgumentNullException(nameof(interactionState));
		_terrainTexturePersistenceService = terrainTexturePersistenceService ?? throw new ArgumentNullException(nameof(terrainTexturePersistenceService));
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
		if (TryResolveStrokeTexture(ref terrain, request.SurfaceTarget, out var sourceTexture, out var sourceAssetId) == false ||
		    IsEditableTexture(sourceTexture) == false)
		{
			return false;
		}

		try
		{
			var previewSet = _terrainBrushGpuExecutor.CreateStrokeResources(sourceTexture, request.SurfaceTarget);
			AssignPreviewTexture(ref terrain, request.SurfaceTarget, previewSet.CurrentPreviewTexture);
			_activeStroke = new StrokeState(
				scene,
				terrainEntity,
				request,
				sourceTexture,
				previewSet.CurrentPreviewTexture,
				previewSet.ScratchPreviewTexture,
				sourceAssetId,
				CaptureTextureSnapshot(sourceAssetId, sourceTexture));
			return true;
		}
		catch
		{
			ClearPreviewTexture(ref terrain, request.SurfaceTarget);
			_activeStroke = null;
			throw;
		}
	}

	public void AppendStamp(Vector3 localPosition, float pressure, TerrainBrushModifierState modifiers)
	{
		var stroke = _activeStroke;
		if (stroke is null)
		{
			return;
		}

		if (pressure <= 0.0f)
		{
			pressure = 1.0f;
		}

		var world = stroke.Scene.World;
		if (world.IsAlive(stroke.TerrainEntity) == false || world.HasComponent<TerrainComponent>(stroke.TerrainEntity) == false)
		{
			CancelStroke();
			return;
		}

		ref var terrain = ref world.GetComponent<TerrainComponent>(stroke.TerrainEntity);
		if (ReferenceEquals(GetPreviewTexture(ref terrain, stroke.Request.SurfaceTarget), stroke.CurrentPreviewTexture) == false)
		{
			CancelStroke();
			return;
		}

		var radius = MathF.Max(stroke.Request.Settings.RadiusMeters, 0.1f);
		var strength = Math.Clamp(stroke.Request.Settings.Strength, 0.0f, 1.0f) * Math.Clamp(pressure, 0.0f, 1.0f);
		if (strength <= 0.0f)
		{
			return;
		}

		if (TryBuildBrushPlacement(ref terrain, stroke.CurrentPreviewTexture.Width, stroke.CurrentPreviewTexture.Height, localPosition, radius, out var placement) == false)
		{
			return;
		}

		if (stroke.Request.Operation == TerrainBrushOperation.Flatten &&
		    stroke.FlattenHeightNormalized.HasValue == false)
		{
			stroke.FlattenHeightNormalized = SampleHeightChannel(
				stroke.SourceTexture.MipLevels[0].Data,
				stroke.SourceTexture.MipLevels[0].Width,
				stroke.SourceTexture.MipLevels[0].Height,
				(int)MathF.Round(placement.CenterPixels.X),
				(int)MathF.Round(placement.CenterPixels.Y));
		}

		_terrainBrushGpuExecutor.ApplyStamp(new TerrainGpuBrushDispatch(
			stroke.Request,
			modifiers,
			stroke.CurrentPreviewTexture,
			stroke.ScratchPreviewTexture,
			strength,
			placement.CenterPixels,
			placement.RadiusPixels,
			stroke.FlattenHeightNormalized));
		stroke.SwapPreviewTextures();
		AssignPreviewTexture(ref terrain, stroke.Request.SurfaceTarget, stroke.CurrentPreviewTexture);
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
		var previewTexture = stroke.CurrentPreviewTexture;
		if (ReferenceEquals(GetPreviewTexture(ref terrain, stroke.Request.SurfaceTarget), previewTexture) == false)
		{
			_activeStroke = null;
			return false;
		}

		var topMip = new TextureMipData(
			previewTexture.Width,
			previewTexture.Height,
			_terrainBrushGpuExecutor.ReadTopMip(previewTexture));
		stroke.SourceTexture.ApplyTextureData(
			previewTexture.Width,
			previewTexture.Height,
			stroke.SourceTexture.IsSrgb,
			stroke.SourceTexture.Format,
			TextureMipGenerator.GenerateRgba32MipChain(topMip));
		ClearPreviewTexture(ref terrain, stroke.Request.SurfaceTarget);

		var afterSnapshot = CaptureTextureSnapshot(stroke.SourceAssetId, stroke.SourceTexture);
		var snapshots = new[] { afterSnapshot };
		_terrainTexturePersistenceService.RecordPendingTextureState(snapshots);
		_undoRedoService.BeginCapture("Terrain Stroke");
		_undoRedoService.CommitCapture(new TerrainTextureEditUndoRedoEntry(
			"Terrain Stroke",
			[stroke.BeforeSnapshot],
			snapshots));
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
			ClearPreviewTexture(ref terrain, stroke.Request.SurfaceTarget);
		}

		_activeStroke = null;
	}

	private static bool IsEditableTexture(Texture texture)
	{
		return texture is not null && texture.Format == TextureFormat.Rgba8Unorm && texture.MipLevels.Length > 0;
	}

	private static bool TryResolveStrokeTexture(
		ref TerrainComponent terrain,
		TerrainAuthoringSurfaceTarget target,
		out Texture sourceTexture,
		out Guid sourceAssetId)
	{
		switch (target)
		{
			case TerrainAuthoringSurfaceTarget.Heightmap:
				sourceTexture = terrain.HeightmapAsset.Asset!;
				sourceAssetId = terrain.HeightmapAsset.NodeId;
				return sourceTexture is not null && sourceAssetId != Guid.Empty;
			case TerrainAuthoringSurfaceTarget.ControlMap:
				sourceTexture = terrain.ControlMapAsset.Asset!;
				sourceAssetId = terrain.ControlMapAsset.NodeId;
				return sourceTexture is not null && sourceAssetId != Guid.Empty;
			default:
				sourceTexture = null!;
				sourceAssetId = Guid.Empty;
				return false;
		}
	}

	private static void AssignPreviewTexture(ref TerrainComponent terrain, TerrainAuthoringSurfaceTarget target, Texture previewTexture)
	{
		if (target == TerrainAuthoringSurfaceTarget.Heightmap)
		{
			terrain.AuthoringPreviewHeightmap = previewTexture;
		}
		else
		{
			terrain.AuthoringPreviewControlMap = previewTexture;
		}
	}

	private static Texture? GetPreviewTexture(ref TerrainComponent terrain, TerrainAuthoringSurfaceTarget target)
	{
		return target == TerrainAuthoringSurfaceTarget.Heightmap
			? terrain.AuthoringPreviewHeightmap
			: terrain.AuthoringPreviewControlMap;
	}

	private static void ClearPreviewTexture(ref TerrainComponent terrain, TerrainAuthoringSurfaceTarget target)
	{
		if (target == TerrainAuthoringSurfaceTarget.Heightmap)
		{
			terrain.AuthoringPreviewHeightmap = null;
		}
		else
		{
			terrain.AuthoringPreviewControlMap = null;
		}
	}

	private static TerrainTextureStateSnapshot CaptureTextureSnapshot(Guid assetId, Texture texture)
	{
		return new TerrainTextureStateSnapshot(
			assetId,
			texture.Width,
			texture.Height,
			texture.IsSrgb,
			texture.Format,
			CloneMipLevels(texture.MipLevels));
	}

	private static TextureMipData[] CloneMipLevels(TextureMipData[] mipLevels)
	{
		var clone = new TextureMipData[mipLevels.Length];
		for (var i = 0; i < mipLevels.Length; i++)
		{
			var mip = mipLevels[i];
			clone[i] = new TextureMipData(mip.Width, mip.Height, mip.Data.ToArray());
		}

		return clone;
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

		var centerX = u * Math.Max(textureWidth - 1, 1);
		var centerY = v * Math.Max(textureHeight - 1, 1);
		var radiusX = radiusMeters / Math.Max(worldSize.X, 0.001f) * Math.Max(textureWidth - 1, 1);
		var radiusY = radiusMeters / Math.Max(worldSize.Y, 0.001f) * Math.Max(textureHeight - 1, 1);
		placement = new BrushPlacement(
			new Vector2(centerX, centerY),
			new Vector2(MathF.Max(radiusX, 0.001f), MathF.Max(radiusY, 0.001f)));
		return true;
	}

	private static float SampleHeightChannel(byte[] data, int width, int height, int x, int y)
	{
		x = Math.Clamp(x, 0, width - 1);
		y = Math.Clamp(y, 0, height - 1);
		return data[((y * width) + x) * 4] / 255.0f;
	}

	private sealed class StrokeState
	{
		public StrokeState(
			EditorScene scene,
			Entity terrainEntity,
			TerrainBrushStrokeRequest request,
			Texture sourceTexture,
			Texture currentPreviewTexture,
			Texture scratchPreviewTexture,
			Guid sourceAssetId,
			TerrainTextureStateSnapshot beforeSnapshot)
		{
			Scene = scene;
			TerrainEntity = terrainEntity;
			Request = request;
			SourceTexture = sourceTexture;
			CurrentPreviewTexture = currentPreviewTexture;
			ScratchPreviewTexture = scratchPreviewTexture;
			SourceAssetId = sourceAssetId;
			BeforeSnapshot = beforeSnapshot;
			FlattenHeightNormalized = request.Settings.FlattenHeightNormalized;
		}

		public EditorScene Scene { get; }
		public Entity TerrainEntity { get; }
		public TerrainBrushStrokeRequest Request { get; }
		public Texture SourceTexture { get; }
		public Texture CurrentPreviewTexture { get; private set; }
		public Texture ScratchPreviewTexture { get; private set; }
		public Guid SourceAssetId { get; }
		public TerrainTextureStateSnapshot BeforeSnapshot { get; }
		public float? FlattenHeightNormalized { get; set; }

		public void SwapPreviewTextures()
		{
			(CurrentPreviewTexture, ScratchPreviewTexture) = (ScratchPreviewTexture, CurrentPreviewTexture);
		}
	}

	private readonly record struct BrushPlacement(
		Vector2 CenterPixels,
		Vector2 RadiusPixels);
}
