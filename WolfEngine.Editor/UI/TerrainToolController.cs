using System;
using System.Numerics;
using ImGuiNET;
using WolfEngine.ECS;
using WolfEngine.Physics;
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
	private readonly RigidbodySystem _rigidbodySystem;
	private readonly TerrainToolSettingsOverlay _terrainToolSettingsOverlay;
	private readonly TerrainBrushPreviewDecalController _terrainBrushPreviewDecalController;
	private EditorScene? _previewScene;
	private Entity _previewTerrainEntity;

	public TerrainToolController(
		EditorViewportStateBus viewportStateBus,
		EditorCameraContext cameraContext,
		ITerrainAuthoringService terrainAuthoringService,
		RigidbodySystem rigidbodySystem,
		TerrainToolSettingsOverlay terrainToolSettingsOverlay,
		TerrainBrushPreviewDecalController terrainBrushPreviewDecalController)
	{
		_viewportStateBus = viewportStateBus ?? throw new ArgumentNullException(nameof(viewportStateBus));
		_cameraContext = cameraContext ?? throw new ArgumentNullException(nameof(cameraContext));
		_terrainAuthoringService = terrainAuthoringService ?? throw new ArgumentNullException(nameof(terrainAuthoringService));
		_rigidbodySystem = rigidbodySystem ?? throw new ArgumentNullException(nameof(rigidbodySystem));
		_terrainToolSettingsOverlay = terrainToolSettingsOverlay ?? throw new ArgumentNullException(nameof(terrainToolSettingsOverlay));
		_terrainBrushPreviewDecalController = terrainBrushPreviewDecalController ?? throw new ArgumentNullException(nameof(terrainBrushPreviewDecalController));
	}

	public TerrainToolSettings Settings { get; } = new();

	internal void DrawAndHandle(EditorScene scene, TerrainTool terrainTool)
	{
		ArgumentNullException.ThrowIfNull(scene);

		var viewportState = _viewportStateBus.GetUiState();
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

		if (_terrainToolSettingsOverlay.BlocksPainting)
		{
			ClearPreview();
			HandleRelease();
			return;
		}

		var io = ImGui.GetIO();
		if (SceneViewportRayUtility.TryBuildInverseViewProjection(camera, cameraWorldTransform, out var inverseViewProjection) == false ||
		    SceneViewportRayUtility.TryBuildWorldRay(viewportState, io.MousePos, inverseViewProjection, out var sceneRay) == false)
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

		var leftDown = ImGui.IsMouseDown(ImGuiMouseButton.Left);
		var localHit = Vector3.Transform(hit.Point, worldToLocal);
		ref var terrain = ref scene.World.GetComponent<TerrainComponent>(EditorGui.SelectedEntity);
		UpdatePreview(scene, EditorGui.SelectedEntity, ref terrain, localHit);
		if (leftDown)
		{
			if (_terrainAuthoringService.HasActiveStroke == false)
			{
				_terrainAuthoringService.BeginStroke(scene, EditorGui.SelectedEntity, BuildStrokeRequest(terrainTool));
			}

			_terrainAuthoringService.AppendStamp(
				localHit,
				pressure: 1.0f,
				new TerrainBrushModifierState(
					ImGui.IsKeyDown(ImGuiKey.LeftShift) || ImGui.IsKeyDown(ImGuiKey.RightShift)));
		}
		else
		{
			HandleRelease();
		}
	}

	private void UpdatePreview(EditorScene scene, Entity terrainEntity, ref TerrainComponent terrain, Vector3 localHit)
	{
		if (!ReferenceEquals(_previewScene, scene) || _previewTerrainEntity != terrainEntity)
		{
			ClearPreview();
			_previewScene = scene;
			_previewTerrainEntity = terrainEntity;
		}

		_terrainBrushPreviewDecalController.ApplyPreview(ref terrain, localHit, Settings.RadiusMeters, Settings.Falloff);
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
			Math.Clamp(Settings.LayerIndex, 0, 3),
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
