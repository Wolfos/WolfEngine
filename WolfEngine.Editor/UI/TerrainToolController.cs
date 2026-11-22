using System.Numerics;
using ImGuiNET;
using WolfEngine.ECS;
using WolfEngine.Physics;
using WolfEngine.Rendering;
using WolfEngine.Rendering.UI;

namespace WolfEngine.Editor.UI;

public sealed class TerrainToolSettings
{
	public float RadiusMeters = 12.0f;
	public float Strength = 0.35f;
	public float Falloff = 1.5f;
	public int LayerIndex;
}

public sealed class TerrainToolController
{
	private readonly EditorViewportStateBus _viewportStateBus;
	private readonly EditorCameraContext _cameraContext;
	private readonly ITerrainAuthoringService _terrainAuthoringService;
	private readonly FoliageAuthoringService _foliageAuthoringService;
	private readonly RigidbodySystem _rigidbodySystem;
	private readonly TerrainBrushPreviewDecalController _terrainBrushPreviewDecalController;
	private EditorScene? _previewScene;
	private Entity _previewTerrainEntity;
	private Vector2? _lastFoliageStamp;
	private bool _lastFoliageErase;

	public TerrainToolController(
		EditorViewportStateBus viewportStateBus,
		EditorCameraContext cameraContext,
		ITerrainAuthoringService terrainAuthoringService,
		FoliageAuthoringService foliageAuthoringService,
		RigidbodySystem rigidbodySystem,
		TerrainBrushPreviewDecalController terrainBrushPreviewDecalController)
	{
		_viewportStateBus = viewportStateBus ?? throw new ArgumentNullException(nameof(viewportStateBus));
		_cameraContext = cameraContext ?? throw new ArgumentNullException(nameof(cameraContext));
		_terrainAuthoringService = terrainAuthoringService ?? throw new ArgumentNullException(nameof(terrainAuthoringService));
		_foliageAuthoringService = foliageAuthoringService ?? throw new ArgumentNullException(nameof(foliageAuthoringService));
		_rigidbodySystem = rigidbodySystem ?? throw new ArgumentNullException(nameof(rigidbodySystem));
		_terrainBrushPreviewDecalController = terrainBrushPreviewDecalController ?? throw new ArgumentNullException(nameof(terrainBrushPreviewDecalController));
	}

	public TerrainToolSettings Settings { get; } = new();
	internal FoliageBrushSettings FoliageSettings { get; } = new();
	internal List<FoliagePaletteEntry> FoliagePalette { get; } = [];
	internal string? FoliageError { get; private set; }

	internal void DrawAndHandle(EditorScene scene, TerrainTool terrainTool)
	{
		ArgumentNullException.ThrowIfNull(scene);
		if (terrainTool != TerrainTool.Foliage)
		{
			FinishFoliageStroke();
		}

		var viewportState = _viewportStateBus.GetUiState(RenderViewId.Primary);
		if (viewportState.Visible == false ||
		    EditorGui.HasSelectedEntity == false ||
		    scene.World.IsAlive(EditorGui.SelectedEntity) == false ||
		    scene.World.HasComponent<TerrainComponent>(EditorGui.SelectedEntity) == false ||
		    scene.World.HasComponent<WorldTransform>(EditorGui.SelectedEntity) == false ||
		    _cameraContext.TryGet(out var camera, out var cameraWorldTransform) == false)
		{
			ClearPreview();
			HandleRelease();
			return;
		}

		if (terrainTool is TerrainTool.Eyedropper or TerrainTool.Pen)
		{
			ClearPreview();
			HandleRelease();
			return;
		}

		if (viewportState.PointerAvailable == false && viewportState.PointerCaptured == false)
		{
			ClearPreview();
			HandleRelease();
			return;
		}

		var io = ImGui.GetIO();
		var renderState = _viewportStateBus.GetRenderState(RenderViewId.Primary);
		if (SceneViewportRayUtility.TryBuildWorldRay(viewportState, io.MousePos, renderState.ViewProjection, out var sceneRay) == false)
		{
			ClearPreview();
			HandleRelease();
			return;
		}

		ref var transform = ref scene.World.GetComponent<WorldTransform>(EditorGui.SelectedEntity);
		if (_rigidbodySystem.TryRaycast(
			    scene.World,
			    sceneRay.Origin,
			    sceneRay.Direction * MathF.Max(camera.FarPlane, 1000.0f),
			    out var hit) == false ||
		    hit.Entity != EditorGui.SelectedEntity ||
		    Matrix4x4.Invert(transform.LocalToWorld, out var worldToLocal) == false)
		{
			ClearPreview();
			HandleRelease();
			return;
		}

		var leftDown = viewportState.PointerCaptured && ImGui.IsMouseDown(ImGuiMouseButton.Left);
		var localHit = Vector3.Transform(hit.Point, worldToLocal);
		ref var terrain = ref scene.World.GetComponent<TerrainComponent>(EditorGui.SelectedEntity);
		var isFoliage = terrainTool == TerrainTool.Foliage;
		var radius = isFoliage ? FoliageSettings.RadiusMeters : Settings.RadiusMeters;
		var falloff = isFoliage ? 4.0f : Settings.Falloff;
		UpdatePreview(scene, EditorGui.SelectedEntity, ref terrain, localHit, radius, falloff);
		if (leftDown)
		{
			var modifiers = new TerrainBrushModifierState(
				ImGui.IsKeyDown(ImGuiKey.LeftShift) || ImGui.IsKeyDown(ImGuiKey.RightShift));
			if (isFoliage)
			{
				_terrainAuthoringService.EndStroke();
				PaintFoliage(scene, EditorGui.SelectedEntity, new Vector2(localHit.X, localHit.Z), modifiers);
				return;
			}

			if (_terrainAuthoringService.HasActiveStroke == false)
			{
				_terrainAuthoringService.BeginStroke(scene, EditorGui.SelectedEntity, BuildStrokeRequest(terrainTool));
			}

			_terrainAuthoringService.AppendStamp(
				localHit,
				pressure: 1.0f,
				modifiers);
		}
		else
		{
			HandleRelease();
		}
	}

	internal void SnapFoliageToTerrain(EditorScene scene)
	{
		ArgumentNullException.ThrowIfNull(scene);

		FinishFoliageStroke();
		if (EditorGui.HasSelectedEntity == false)
		{
			return;
		}

		try
		{
			FoliageError = null;
			_foliageAuthoringService.SnapToTerrain(scene, EditorGui.SelectedEntity);
		}
		catch (Exception exception)
		{
			FoliageError = exception.Message;
		}
	}

	internal void FinishFoliageStroke()
	{
		_foliageAuthoringService.EndStroke();
		_lastFoliageStamp = null;
	}

	private void PaintFoliage(EditorScene scene, Entity entity, Vector2 localPosition, TerrainBrushModifierState modifiers)
	{
		if (FoliageError is not null && ImGui.IsMouseClicked(ImGuiMouseButton.Left) == false) return;

		try
		{
			FoliageError = null;
			if (_foliageAuthoringService.IsStrokeOn(scene, entity) == false)
			{
				_lastFoliageStamp = null;
				if (_foliageAuthoringService.BeginStroke(scene, entity, FoliageSettings, FoliagePalette) == false)
				{
					FoliageError = "This terrain needs a valid editable foliage asset and heightmap.";
					return;
				}
			}

			if (_lastFoliageStamp is not { } last || _lastFoliageErase != modifiers.Invert)
			{
				_foliageAuthoringService.AppendStamp(localPosition, modifiers);
				_lastFoliageStamp = localPosition;
				_lastFoliageErase = modifiers.Invert;
				return;
			}

			var distance = Vector2.Distance(last, localPosition);
			var step = FoliageSettings.RadiusMeters * 0.35f;
			if (distance < step) return;

			var count = (int)MathF.Min(MathF.Floor(distance / step), 64.0f);
			step = MathF.Max(step, distance / 64.0f);
			for (var i = 1; i <= count; i++)
			{
				var position = Vector2.Lerp(last, localPosition, i * step / distance);
				_foliageAuthoringService.AppendStamp(position, modifiers);
				_lastFoliageStamp = position;
			}
		}
		catch (Exception exception)
		{
			FinishFoliageStroke();
			FoliageError = exception.Message;
		}
	}

	private void UpdatePreview(
		EditorScene scene,
		Entity terrainEntity,
		ref TerrainComponent terrain,
		Vector3 localHit,
		float radius,
		float falloff)
	{
		if (!ReferenceEquals(_previewScene, scene) || _previewTerrainEntity != terrainEntity)
		{
			ClearPreview();
			_previewScene = scene;
			_previewTerrainEntity = terrainEntity;
		}

		_terrainBrushPreviewDecalController.ApplyPreview(ref terrain, localHit, radius, falloff);
	}

	internal void ClearPreview()
	{
		if (_previewScene is null ||
		    _previewScene.World.IsAlive(_previewTerrainEntity) == false ||
		    _previewScene.World.HasComponent<TerrainComponent>(_previewTerrainEntity) == false)
		{
			_previewScene = null;
			_previewTerrainEntity = default;
			return;
		}

		ref var terrain = ref _previewScene.World.GetComponent<TerrainComponent>(_previewTerrainEntity);
		_terrainBrushPreviewDecalController.ClearPreview(ref terrain);
		_previewScene = null;
		_previewTerrainEntity = default;
	}

	private void HandleRelease()
	{
		if (ImGui.IsMouseDown(ImGuiMouseButton.Left) == false)
		{
			FinishFoliageStroke();
		}

		if (ImGui.IsMouseReleased(ImGuiMouseButton.Left))
		{
			_terrainAuthoringService.EndStroke();
		}
		else if (ImGui.IsMouseDown(ImGuiMouseButton.Left) == false && _terrainAuthoringService.HasActiveStroke)
		{
			_terrainAuthoringService.EndStroke();
		}
	}

	private TerrainBrushStrokeRequest BuildStrokeRequest(TerrainTool tool)
	{
		var settings = new TerrainBrushSettings(
			MathF.Max(Settings.RadiusMeters, 0.1f),
			Math.Clamp(Settings.Strength, 0.0f, 1.0f),
			MathF.Max(Settings.Falloff, 0.1f),
			Math.Clamp(Settings.LayerIndex, 0, TerrainLayerSet.MaxLayerCount),
			FlattenHeightNormalized: null);
		return tool switch
		{
			TerrainTool.RaiseLower => new TerrainBrushStrokeRequest(TerrainAuthoringSurfaceTarget.Heightmap, TerrainBrushOperation.RaiseLower, settings),
			TerrainTool.Flatten => new TerrainBrushStrokeRequest(TerrainAuthoringSurfaceTarget.Heightmap, TerrainBrushOperation.Flatten, settings),
			TerrainTool.Smooth => new TerrainBrushStrokeRequest(TerrainAuthoringSurfaceTarget.Heightmap, TerrainBrushOperation.Smooth, settings),
			_ => new TerrainBrushStrokeRequest(TerrainAuthoringSurfaceTarget.LayerMaps, TerrainBrushOperation.PaintLayer, settings)
		};
	}
}
