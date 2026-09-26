using System.ComponentModel;
using ModelContextProtocol.Server;

namespace WolfEngine.Editor.Automation;

[McpServerToolType]
public sealed class EditorAutomationTools
{
	private readonly EditorProcessController _controller;

	public EditorAutomationTools(EditorProcessController controller) => _controller = controller;

	[McpServerTool(Name = "start_editor"), Description("Start WolfEngine Editor for a project and wait until it is ready for automation.")]
	public async Task<string> StartEditor(
		[Description("Absolute or relative path to a WolfEngine project folder.")] string projectPath,
		CancellationToken cancellationToken)
	{
		await _controller.StartAsync(projectPath, cancellationToken).ConfigureAwait(false);
		return "WolfEngine Editor is ready.";
	}

	[McpServerTool(Name = "set_editor_camera"), Description("Move the primary editor camera to a position looking at a target, in memory only. Wait for render frames after loading a scene before calling this.")]
	public async Task<string> SetEditorCamera(float x, float y, float z, float targetX, float targetY, float targetZ, CancellationToken cancellationToken)
	{
		await _controller.SetEditorCameraAsync(new(x, y, z), new(targetX, targetY, targetZ), cancellationToken);
		return "Editor camera updated in memory.";
	}

	[McpServerTool(Name = "create_entity"), Description("Create an entity in the running editor's authoring scene without saving the scene.")]
	public async Task<string> CreateEntity(
		[Description("Optional entity name.")] string? name = null,
		CancellationToken cancellationToken = default)
	{
		var result = await _controller.CreateEntityAsync(name, cancellationToken).ConfigureAwait(false);
		return $"Created entity '{result.Name}' with id {result.EntityId:D}.";
	}

	[McpServerTool(Name = "delete_entity"), Description("Delete an entity by the persistent GUID returned by create_entity.")]
	public async Task<string> DeleteEntity(
		[Description("Persistent entity GUID.")] string entityId,
		CancellationToken cancellationToken)
	{
		await _controller.DeleteEntityAsync(entityId, cancellationToken).ConfigureAwait(false);
		return $"Deleted entity {entityId}.";
	}

	[McpServerTool(Name = "instantiate_model"), Description("Instantiate an imported 3D model into the authoring scene by asset name, through the same path the Assets window uses. Reports how many skinned mesh renderers and animators the model brought with it.")]
	public Task<InstantiatedModelResult> InstantiateModel(
		[Description("Substring of the 3D model asset name, such as 'Macarena'.")] string assetName,
		[Description("Optional spawn X in world units.")] float? x = null,
		[Description("Optional spawn Y in world units.")] float? y = null,
		[Description("Optional spawn Z in world units.")] float? z = null,
		[Description("Uniform scale applied to the instantiated root. Useful for sources authored in centimetres.")] float uniformScale = 1.0f,
		CancellationToken cancellationToken = default) =>
		_controller.InstantiateModelAsync(assetName, x, y, z, uniformScale, cancellationToken);

	[McpServerTool(Name = "get_animation_state"), Description("Report every animator in the authoring scene: bound clip and skeleton, bone and track counts, how many clip tracks resolved against the skeleton, playback time, and how far the current pose has moved from the bind pose.")]
	public Task<AnimationStateResult> GetAnimationState(CancellationToken cancellationToken) =>
		_controller.GetAnimationStateAsync(cancellationToken);

	[McpServerTool(Name = "select_entities"), Description("Set the editor's entity selection by persistent GUID, through the same selection path as clicking in the viewport or the entity hierarchy. Pass no ids to clear the selection.")]
	public async Task<string> SelectEntities(
		[Description("Persistent entity GUIDs to select. Empty clears the selection.")] string[]? entityIds = null,
		CancellationToken cancellationToken = default)
	{
		var selected = await _controller
			.SelectEntitiesAsync(entityIds ?? Array.Empty<string>(), cancellationToken)
			.ConfigureAwait(false);
		return selected == 0 ? "Cleared the entity selection." : $"Selected {selected} entities.";
	}

	[McpServerTool(Name = "load_scene"), Description("Load a scene from the open project's asset database through the editor's normal scene-replacement path. The persistent editor and renderer remain running until the scene load has completed.")]
	public Task<SceneLoadResult> LoadScene(
		[Description("Absolute or project-relative path of a .scene.json asset.")] string scenePath,
		CancellationToken cancellationToken) =>
		_controller.LoadSceneAsync(scenePath, cancellationToken);

	[McpServerTool(Name = "enter_play_mode"), Description("Enter Play mode from the current authoring scene. Play mode creates an isolated runtime scene without saving or mutating the authoring scene.")]
	public Task<PlayModeStateResult> EnterPlayMode(CancellationToken cancellationToken) =>
		_controller.EnterPlayModeAsync(cancellationToken);

	[McpServerTool(Name = "pause_play_mode"), Description("Pause the currently running Play-mode scene.")]
	public Task<PlayModeStateResult> PausePlayMode(CancellationToken cancellationToken) =>
		_controller.PausePlayModeAsync(cancellationToken);

	[McpServerTool(Name = "stop_play_mode"), Description("Stop Play mode and discard the isolated runtime scene, returning to the authoring scene.")]
	public Task<PlayModeStateResult> StopPlayMode(CancellationToken cancellationToken) =>
		_controller.StopPlayModeAsync(cancellationToken);

	[McpServerTool(Name = "set_input_button"), Description("Press or release one named input binding in a running Play-mode scene, through the same input system used by gameplay.")]
	public async Task<string> SetInputButton(
		[Description("InputActionBinding name, such as KeyW or GamepadFaceSouth.")] string binding,
		[Description("True to press the binding; false to release it.")] bool pressed,
		CancellationToken cancellationToken)
	{
		await _controller.SetInputButtonAsync(binding, pressed, cancellationToken).ConfigureAwait(false);
		return $"Input binding '{binding}' is now {(pressed ? "pressed" : "released")}.";
	}

	[McpServerTool(Name = "set_input_axis_2d"), Description("Set one named two-dimensional input binding in a running Play-mode scene, through the same input system used by gameplay.")]
	public async Task<string> SetInputAxis2D(
		[Description("InputActionBinding name, such as MouseDelta or GamepadLeftStick.")] string binding,
		[Description("Horizontal axis value.")] float x,
		[Description("Vertical axis value.")] float y,
		CancellationToken cancellationToken)
	{
		await _controller.SetInputAxis2DAsync(binding, new System.Numerics.Vector2(x, y), cancellationToken)
			.ConfigureAwait(false);
		return $"Input axis '{binding}' is now ({x}, {y}).";
	}

	[McpServerTool(Name = "wait_for_render_frames"), Description("Wait for completed render-graph frames, rather than editor update ticks, and return the editor and render sequence numbers.")]
	public Task<RenderFrameWaitResult> WaitForRenderFrames(
		[Description("Positive number of completed render frames to wait for.")] int frameCount,
		CancellationToken cancellationToken) =>
		_controller.WaitForRenderFramesAsync(frameCount, cancellationToken);

	[McpServerTool(Name = "get_workspace_state"), Description("Return all editor workspaces, their stable ids, open windows, active workspace, and saved docking-layout size.")]
	public Task<EditorWorkspaceStateResult> GetWorkspaceState(CancellationToken cancellationToken) =>
		_controller.GetWorkspaceStateAsync(cancellationToken);

	[McpServerTool(Name = "get_editor_ui_state"), Description("Report every editor panel as of the last drawn frame: whether it is open, which dock node it sits in, whether it is that node's selected tab, whether it is focused or hovered, and its position and size in ImGui points. Dock nodes are also grouped, so tab selection can be asserted without reading a screenshot. Wait for render frames after a change before reading this.")]
	public Task<EditorUiStateResult> GetEditorUiState(CancellationToken cancellationToken) =>
		_controller.GetEditorUiStateAsync(cancellationToken);

	[McpServerTool(Name = "create_workspace"), Description("Create and activate an empty editor workspace.")]
	public Task<EditorWorkspaceStateResult> CreateWorkspace(string name, CancellationToken cancellationToken) =>
		_controller.CreateWorkspaceAsync(name, cancellationToken);

	[McpServerTool(Name = "rename_workspace"), Description("Rename an editor workspace without changing its stable layout identity.")]
	public Task<EditorWorkspaceStateResult> RenameWorkspace(string workspaceId, string name, CancellationToken cancellationToken) =>
		_controller.RenameWorkspaceAsync(workspaceId, name, cancellationToken);

	[McpServerTool(Name = "activate_workspace"), Description("Activate an editor workspace by stable id.")]
	public Task<EditorWorkspaceStateResult> ActivateWorkspace(string workspaceId, CancellationToken cancellationToken) =>
		_controller.ActivateWorkspaceAsync(workspaceId, cancellationToken);

	[McpServerTool(Name = "delete_workspace"), Description("Delete an editor workspace. The only remaining workspace cannot be deleted.")]
	public Task<EditorWorkspaceStateResult> DeleteWorkspace(string workspaceId, CancellationToken cancellationToken) =>
		_controller.DeleteWorkspaceAsync(workspaceId, cancellationToken);

	[McpServerTool(Name = "set_workspace_window_open"), Description("Open or close one registered editor window in the active workspace.")]
	public Task<EditorWorkspaceStateResult> SetWorkspaceWindowOpen(string windowId, bool open, CancellationToken cancellationToken) =>
		_controller.SetWorkspaceWindowOpenAsync(windowId, open, cancellationToken);

	[McpServerTool(Name = "paint_terrain_layer"), Description("Apply one terrain layer-paint stamp through the editor's real authoring and undo path. The edit remains in memory unless the scene is explicitly saved.")]
	public Task<TerrainLayerPaintResult> PaintTerrainLayer(
		[Description("Persistent terrain entity GUID. May be omitted when the authoring scene contains exactly one terrain entity.")] string? terrainEntityId = null,
		[Description("Terrain-local X coordinate in meters.")] float localX = 0.0f,
		[Description("Terrain-local Z coordinate in meters.")] float localZ = 0.0f,
		[Description("Zero-based terrain layer index to paint.")] int layerIndex = 1,
		[Description("Brush radius in meters.")] float radiusMeters = 8.0f,
		[Description("Brush strength from 0 through 1.")] float strength = 1.0f,
		[Description("Positive brush falloff exponent.")] float falloff = 1.0f,
		[Description("Remove the selected layer instead of adding it.")] bool invert = false,
		CancellationToken cancellationToken = default) =>
		_controller.PaintTerrainLayerAsync(
			terrainEntityId,
			localX,
			localZ,
			layerIndex,
			radiusMeters,
			strength,
			falloff,
			invert,
			cancellationToken);

	[McpServerTool(Name = "undo"), Description("Undo the most recent editor authoring action, including a terrain stroke.")]
	public Task<EditorUndoResult> Undo(CancellationToken cancellationToken) =>
		_controller.UndoAsync(cancellationToken);

	[McpServerTool(Name = "get_ray_tracing_scene_state"), Description("Return the latest renderer-side TLAS, BLAS, terrain, retirement, and GPU-submission diagnostics without synchronizing or restarting the renderer.")]
	public Task<RayTracingSceneStateResult> GetRayTracingSceneState(CancellationToken cancellationToken) =>
		_controller.GetRayTracingSceneStateAsync(cancellationToken);

	[McpServerTool(Name = "profile_gpu_frames"), Description("Enable the existing GPU profiler and aggregate timing statistics over multiple completed GPU frames. Results include median, p95, and maximum per render pass and nested scope.")]
	public Task<GpuFrameProfileResult> ProfileGpuFrames(
		[Description("Positive number of completed GPU-profile frames to aggregate.")] int frameCount,
		CancellationToken cancellationToken) =>
		_controller.ProfileGpuFramesAsync(frameCount, cancellationToken);

	[McpServerTool(Name = "get_cpu_frame_profile"), Description("Return the latest completed CPU profiler tree for every profiled thread, including duration and managed allocation per scope.")]
	public Task<CpuFrameProfileResult> GetCpuFrameProfile(CancellationToken cancellationToken) =>
		_controller.GetCpuFrameProfileAsync(cancellationToken);

	[McpServerTool(Name = "set_anti_aliasing"), Description("Switch the authoring scene's anti-aliasing method in memory without saving its render config asset. Use with frame waits, GPU profiling, and captures to compare TAA/CAS and FSR3.")]
	public Task<string> SetAntiAliasing(
		[Description("Taa or Fsr3.")] string mode,
		[Description("Whether temporal anti-aliasing is enabled.")] bool enabled = true,
		[Description("Whether to sharpen TAA with CAS after tonemapping. Does not affect FSR3 RCAS.")] bool casSharpening = true,
		CancellationToken cancellationToken = default) =>
		_controller.SetAntiAliasingAsync(mode, enabled, casSharpening, cancellationToken);

	[McpServerTool(Name = "set_scene_debug_view"), Description("Sets the debug view captured from the scene viewport; pass an empty string to release it.")]
	public async Task<string> SetSceneDebugView(
		[Description("Debug view id, or empty to release.")] string debugViewId,
		CancellationToken cancellationToken)
	{
		await _controller.SetSceneDebugViewAsync(debugViewId, cancellationToken).ConfigureAwait(false);
		return string.IsNullOrWhiteSpace(debugViewId)
			? "Scene debug view override released."
			: $"Scene viewport pinned to debug view '{debugViewId}'.";
	}

	[McpServerTool(Name = "set_ddgi_relocation"), Description("Enable or disable DDGI probe relocation in the active scene's render config in memory, without saving the asset. Works in authoring and Play mode.")]
	public Task<string> SetDdgiRelocation(bool enabled, CancellationToken cancellationToken) =>
		_controller.SetDdgiRelocationAsync(enabled, cancellationToken);

	[McpServerTool(Name = "set_entity_rotation"), Description("Set an entity's local Euler rotation in degrees in the active authoring or Play-mode scene, without saving. Uses the same transform setter as the inspector.")]
	public async Task<string> SetEntityRotation(string entityId, float pitch, float yaw, float roll, CancellationToken cancellationToken)
	{
		await _controller.SetEntityRotationAsync(Guid.Parse(entityId), new System.Numerics.Vector3(pitch, yaw, roll), cancellationToken);
		return $"Entity {entityId}: rotation ({pitch}, {yaw}, {roll}) degrees. Changed in memory only.";
	}

	[McpServerTool(Name = "capture_frame"), Description("Capture a PNG from the currently running editor on its next rendered frame. This never launches a separate editor process.")]
	public Task<FrameCaptureResult> CaptureFrame(
		[Description("Absolute or project-relative PNG output path.")] string outputPath,
		CancellationToken cancellationToken) =>
		_controller.CaptureFrameAsync(outputPath, cancellationToken);

	[McpServerTool(Name = "select_asset"), Description("Select an asset by its persistent GUID through the editor's normal asset-selection path and focus the Asset Editor. The selection is in memory only.")]
	public Task<string> SelectAsset(string assetId, CancellationToken cancellationToken) =>
		_controller.SelectAssetAsync(Guid.Parse(assetId), cancellationToken);

	[McpServerTool(Name = "capture_editor_window"), Description("Capture a PNG of the whole editor window, including the ImGui panels, dock tabs and menus. Use this instead of capture_frame whenever the editor UI itself is what needs to be verified.")]
	public Task<FrameCaptureResult> CaptureEditorWindow(
		[Description("Absolute or project-relative PNG output path.")] string outputPath,
		CancellationToken cancellationToken) =>
		_controller.CaptureEditorWindowAsync(outputPath, cancellationToken);

	[McpServerTool(Name = "capture_gameplay_frame"), Description("Enter or resume Play mode, verify that an enabled gameplay Camera is driving the viewport, wait for gameplay startup frames, and capture a PNG from that camera. Fails instead of silently falling back to the authoring camera.")]
	public Task<GameplayFrameCaptureResult> CaptureGameplayFrame(
		[Description("Absolute or project-relative PNG output path.")] string outputPath,
		[Description("Positive number of rendered Play-mode frames to wait before validating the gameplay camera and capturing.")] int settleFrameCount = 4,
		CancellationToken cancellationToken = default) =>
		_controller.CaptureGameplayFrameAsync(outputPath, settleFrameCount, cancellationToken);

	[McpServerTool(Name = "shutdown_editor"), Description("Gracefully shut down the running WolfEngine Editor while leaving this MCP server available.")]
	public async Task<string> ShutdownEditor(CancellationToken cancellationToken)
	{
		await _controller.ShutdownAsync(cancellationToken).ConfigureAwait(false);
		return "WolfEngine Editor has shut down.";
	}
}
