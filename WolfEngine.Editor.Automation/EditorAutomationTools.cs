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
		CancellationToken cancellationToken,
		[Description("Optional initial renderer window width; specify together with height.")] int width = 0,
		[Description("Optional initial renderer window height; specify together with width.")] int height = 0)
	{
		await _controller.StartAsync(projectPath, cancellationToken, width, height).ConfigureAwait(false);
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

	[McpServerTool(Name = "build_game"), Description("Build the open project into a runnable game in the output folder, through the same path as the editor's Build menu: compile gameplay, cook content and publish the runtime. Returns the output path and cooked asset count.")]
	public Task<global::WolfEngine.Build.GameBuildResult> BuildGame(
		[Description("Folder to write the game to. Write builds below Artifacts/ and do not commit them.")] string outputPath,
		CancellationToken cancellationToken,
		[Description("Keep debug symbols and build gameplay unoptimized.")] bool debug = false,
		[Description("Runtime identifier to build for, such as ios-arm64 for the content an iOS app bundles. Omit to build for this machine.")] string? target = null) =>
		_controller.BuildGameAsync(outputPath, debug, target, cancellationToken);

	[McpServerTool(Name = "load_play_scene"), Description("Replace the active Play-mode scene through the gameplay scene-load service. Leaves the authoring scene unchanged and waits for lifecycle completion.")]
	public Task<SceneLoadResult> LoadPlayScene(string scenePath, CancellationToken cancellationToken) =>
		_controller.LoadPlaySceneAsync(scenePath, cancellationToken);

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

	[McpServerTool(Name = "set_scene_pointer"), Description("Move the mouse in Scene viewport logical points and optionally press/release its primary button. Delivers independent input to gameplay and editor ImGui. Works in authoring, running and paused modes to validate routing.")]
	public async Task<string> SetScenePointer(float x, float y, CancellationToken cancellationToken, bool? pressed = null,
		[Description("Optional synthetic native focus state, for deterministic background-window and focus-loss tests.")] bool? focused = null)
	{
		await _controller.SetScenePointerAsync(new(x, y), pressed, cancellationToken, focused).ConfigureAwait(false);
		return $"Scene pointer at ({x}, {y}); primary button: {pressed?.ToString() ?? "unchanged"}.";
	}

	[McpServerTool(Name = "get_scene_pointer_state"), Description("Read the Scene viewport pointer availability, capture, focus, logical origin and size after completed render frames.")]
	public Task<string> GetScenePointerState(CancellationToken cancellationToken) => _controller.GetScenePointerStateAsync(cancellationToken);

	[McpServerTool(Name = "set_editor_pointer"), Description("Move the editor UI pointer in window-relative logical points and optionally change a button or send wheel input. Works when the Scene viewport is hidden, including the Animation Graph workspace. Does not inject gameplay input.")]
	public async Task<string> SetEditorPointer(
		float x,
		float y,
		CancellationToken cancellationToken,
		bool? pressed = null,
		int button = 0,
		float wheel = 0.0f)
	{
		await _controller.SetEditorPointerAsync(new(x, y), pressed, button, wheel, cancellationToken).ConfigureAwait(false);
		return $"Editor pointer at ({x}, {y}).";
	}

	[McpServerTool(Name = "get_animation_editor_state"), Description("Read the open graph's last-drawn canvas, node and port bounds, zoom, selected node, toolbar bounds, and unsaved undo/redo state. Wait for render frames after an interaction before inspecting these coordinates.")]
	public Task<UI.AnimationGraphEditorState> GetAnimationEditorState(CancellationToken cancellationToken) =>
		_controller.GetAnimationEditorStateAsync(cancellationToken);

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

	[McpServerTool(Name = "set_render_effects"), Description("Set AO, reflections, diffuse GI and volumetric fog enabled states in memory without saving the render config. Disable temporal effects to isolate geometry and direct shadows for deterministic image comparisons.")]
	public Task<string> SetRenderEffects(bool ambientOcclusion, bool reflections, bool diffuseGlobalIllumination,
		bool volumetricFog, CancellationToken cancellationToken) =>
		_controller.SetRenderEffectsAsync(ambientOcclusion, reflections, diffuseGlobalIllumination, volumetricFog, cancellationToken);

	[McpServerTool(Name = "set_shadow_caster_culling"), Description("Enable or disable tighter shadow-caster culling in memory without saving the render config. Use for identical-scene capture and GPU timing comparisons.")]
	public Task<string> SetShadowCasterCulling(bool enabled, CancellationToken cancellationToken) =>
		_controller.SetShadowCasterCullingAsync(enabled, cancellationToken);

	[McpServerTool(Name = "set_shadow_vertex_welding"), Description("Enable or disable lossless shadow vertex welding in memory without saving the render config. Use for identical-scene capture and GPU timing comparisons.")]
	public Task<string> SetShadowVertexWelding(bool enabled, CancellationToken cancellationToken) =>
		_controller.SetShadowVertexWeldingAsync(enabled, cancellationToken);

	[McpServerTool(Name = "set_ddgi_relocation"), Description("Enable or disable DDGI probe relocation in the active scene's render config in memory, without saving the asset. Works in authoring and Play mode.")]
	public Task<string> SetDdgiRelocation(bool enabled, CancellationToken cancellationToken) =>
		_controller.SetDdgiRelocationAsync(enabled, cancellationToken);

	[McpServerTool(Name = "set_ddgi_final_gather"), Description("Enable or disable the DDGI final gather and set its trace resolution in the active scene's render config in memory, without saving the asset. Works in authoring and Play mode.")]
	public Task<string> SetDdgiFinalGather(
		bool enabled,
		[Description("Trace resolution: Full, Half or Quarter.")] string resolution,
		CancellationToken cancellationToken) =>
		_controller.SetDdgiFinalGatherAsync(enabled, resolution, cancellationToken);

	[McpServerTool(Name = "set_indirect_light_minimum"), Description("Set the render config's indirect light minimum in memory, without saving the asset: the uniform ambient radiance that diffuse and specular indirect lighting never fall below. Zero disables it. Works in authoring and Play mode.")]
	public Task<string> SetIndirectLightMinimum(
		[Description("Minimum indirect radiance; zero disables the floor.")] float minimum,
		CancellationToken cancellationToken) =>
		_controller.SetIndirectLightMinimumAsync(minimum, cancellationToken);

	[McpServerTool(Name = "set_entity_scale"), Description("Set an entity's local scale in the active authoring or Play-mode scene without saving. Supports per-axis negative scales for mirrored-geometry validation.")]
	public async Task<string> SetEntityScale(string entityId, float x, float y, float z, CancellationToken cancellationToken)
	{
		await _controller.SetEntityScaleAsync(Guid.Parse(entityId), new System.Numerics.Vector3(x, y, z), cancellationToken);
		return $"Entity {entityId}: scale ({x}, {y}, {z}). Changed in memory only.";
	}

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
    [McpServerTool(Name = "configure_animation_preview"), Description("Open a graph or authored clip in the Animation workspace using an isolated model or prefab preview.")]
    public Task<AnimationGraphInspectionResult> ConfigureAnimationPreview(string assetId, string modelId, string? clipSetId = null, CancellationToken cancellationToken = default) =>
        _controller.ConfigureAnimationPreviewAsync(Guid.Parse(assetId), Guid.Parse(modelId), clipSetId is null ? Guid.Empty : Guid.Parse(clipSetId), cancellationToken);
    [McpServerTool(Name = "animation_preview_transport"), Description("Play, pause, scrub or step the isolated animation preview. Seeking suppresses markers.")]
    public Task<AnimationGraphInspectionResult> AnimationPreviewTransport(bool playing, float? seekSeconds = null, float? stepSeconds = null, CancellationToken cancellationToken = default) =>
        _controller.AnimationPreviewTransportAsync(playing, seekSeconds, stepSeconds, cancellationToken);
    [McpServerTool(Name = "get_animation_graph"), Description("Inspect contributing clips, states, curves and markers. Omit entityId to inspect the isolated preview.")]
    public Task<AnimationGraphInspectionResult> GetAnimationGraph(string? entityId = null, CancellationToken cancellationToken = default) =>
        _controller.GetAnimationGraphAsync(entityId is null ? null : Guid.Parse(entityId), cancellationToken);
    [McpServerTool(Name = "set_animation_parameter"), Description("Set a typed graph parameter; omit entityId to modify the isolated preview. Bool values are 0 or 1.")]
    public Task<AnimationGraphInspectionResult> SetAnimationParameter(string name, float value, string? entityId = null, CancellationToken cancellationToken = default) =>
        _controller.SetAnimationParameterAsync(entityId is null ? null : Guid.Parse(entityId), name, value, cancellationToken);
    [McpServerTool(Name = "set_skinning_force_updates"), Description("Force skinning dispatches and BLAS updates for reference-image comparisons, or restore pose-generation scheduling.")]
    public Task<bool> SetSkinningForceUpdates(bool forced, CancellationToken cancellationToken = default) => _controller.SetSkinningForceUpdatesAsync(forced, cancellationToken);
    [McpServerTool(Name = "edit_animation_graph"), Description("Edit an input connection in the open animation document, or undo/redo it. Edits are unsaved; invalid edits retain the last valid preview.")]
    public Task<AnimationGraphInspectionResult> EditAnimationGraph(string operation, string? nodeId = null, int inputIndex = 0, string? inputId = null, CancellationToken cancellationToken = default) =>
        _controller.EditAnimationGraphAsync(operation, nodeId is null ? Guid.Empty : Guid.Parse(nodeId), inputIndex, inputId is null ? Guid.Empty : Guid.Parse(inputId), cancellationToken);
    [McpServerTool(Name = "capture_animation_preview"), Description("Capture the isolated animation preview render view without editor UI. Keep the Animation window visible.")]
    public Task<FrameCaptureResult> CaptureAnimationPreview(string outputPath, CancellationToken cancellationToken) => _controller.CaptureAnimationPreviewAsync(outputPath, cancellationToken);

	[McpServerTool(Name = "open_clip_editor"), Description("Open an animation clip asset in the Animate workspace. Authored .anim.json clips are editable; imported clips open read-only. If the open clip has unsaved edits, a prompt is shown unless discardUnsavedChanges is true.")]
	public Task<AnimationClipEditorStateResult> OpenClipEditor(string assetId, bool discardUnsavedChanges = false, CancellationToken cancellationToken = default) =>
		_controller.OpenClipEditorAsync(Guid.Parse(assetId), discardUnsavedChanges, cancellationToken);

	[McpServerTool(Name = "create_clip"), Description("Create an authored .anim.json clip in a project folder, bound to a skeleton asset, and open it. Unsaved edits to the previously open clip are discarded.")]
	public Task<AnimationClipEditorStateResult> CreateClip(string folder, string name, string skeletonId, CancellationToken cancellationToken = default) =>
		_controller.CreateClipAsync(folder, name, Guid.Parse(skeletonId), cancellationToken);

	[McpServerTool(Name = "duplicate_clip"), Description("Copy an imported clip into a new editable .anim.json next to its source and open it. Keys off frame boundaries or with tangents are resampled to one key per frame.")]
	public Task<AnimationClipEditorStateResult> DuplicateClip(string assetId, CancellationToken cancellationToken = default) =>
		_controller.DuplicateClipAsync(Guid.Parse(assetId), cancellationToken);

	[McpServerTool(Name = "select_bone"), Description("Select a bone in the clip editor by name, or clear the selection when bone is omitted.")]
	public Task<AnimationClipEditorStateResult> SelectBone(string? bone = null, CancellationToken cancellationToken = default) =>
		_controller.SelectClipBoneAsync(bone, cancellationToken);

	[McpServerTool(Name = "set_bone_local_transform"), Description("Set a bone's local transform at the playhead like the inspector does: an unkeyed edit, or a key when Auto Key is on. Pass three numbers for any of position, rotationDegrees (Euler) and scale; omitted parts keep their value.")]
	public Task<AnimationClipEditorStateResult> SetBoneLocalTransform(
		string bone,
		float[]? position = null,
		float[]? rotationDegrees = null,
		float[]? scale = null,
		CancellationToken cancellationToken = default) =>
		_controller.SetClipBoneLocalTransformAsync(bone, ToVector(position), ToVector(rotationDegrees), ToVector(scale), cancellationToken);

	[McpServerTool(Name = "set_playhead"), Description("Move the clip editor playhead to a frame and stop playback. Moving to a different frame discards unkeyed edits.")]
	public Task<AnimationClipEditorStateResult> SetPlayhead(int frame, CancellationToken cancellationToken = default) =>
		_controller.SetClipPlayheadAsync(frame, cancellationToken);

	[McpServerTool(Name = "set_auto_key"), Description("Turn the clip editor's Auto Key on or off.")]
	public Task<AnimationClipEditorStateResult> SetAutoKey(bool enabled, CancellationToken cancellationToken = default) =>
		_controller.SetClipAutoKeyAsync(enabled, cancellationToken);

	[McpServerTool(Name = "set_key"), Description("Key a bone's current local transform, including unkeyed edits, at the playhead. Omit bone to key the selected bone.")]
	public Task<AnimationClipEditorStateResult> SetKey(string? bone = null, CancellationToken cancellationToken = default) =>
		_controller.SetClipKeyAsync(bone, cancellationToken);

	[McpServerTool(Name = "edit_clip_keys"), Description("Select dopesheet keys given as bone@frame, then apply operation: select, move, duplicate or delete. Move and duplicate shift by frameDelta and fail if a key would leave the clip.")]
	public Task<AnimationClipEditorStateResult> EditClipKeys(string operation, string[] keys, int frameDelta = 0, CancellationToken cancellationToken = default) =>
		_controller.EditClipKeysAsync(operation, keys, frameDelta, cancellationToken);

	[McpServerTool(Name = "set_clip_metadata"), Description("Replace the open clip's named curves and timed markers with a JSON object containing Curves and Markers arrays. Works on authored clips and imported takes; changes are undoable and unsaved until save_clip.")]
	public Task<AnimationClipEditorStateResult> SetClipMetadata(string metadataJson, CancellationToken cancellationToken = default) =>
		_controller.SetClipMetadataAsync(metadataJson, cancellationToken);

	[McpServerTool(Name = "clip_history"), Description("Undo or redo in the open clip's own history, separate from scene undo. operation is undo or redo.")]
	public Task<AnimationClipEditorStateResult> ClipHistory(string operation, CancellationToken cancellationToken = default) =>
		_controller.ClipHistoryAsync(operation, cancellationToken);

	[McpServerTool(Name = "save_clip"), Description("Save the open clip or imported take annotations to their source and reimport.")]
	public Task<AnimationClipEditorStateResult> SaveClip(CancellationToken cancellationToken = default) =>
		_controller.SaveClipAsync(cancellationToken);

	[McpServerTool(Name = "get_clip_editor_state"), Description("Inspect the clip editor: open clip, dirty state, playhead, selection, keyed frames per bone, unkeyed bones, the selected bone's local transform and a hash of the preview pose.")]
	public Task<AnimationClipEditorStateResult> GetClipEditorState(CancellationToken cancellationToken = default) =>
		_controller.GetClipEditorStateAsync(cancellationToken);

	[McpServerTool(Name = "capture_clip_preview"), Description("Capture the clip editor's preview render view without editor UI. Keep the Clip Viewport visible and wait for render frames after changes.")]
	public Task<FrameCaptureResult> CaptureClipPreview(string outputPath, CancellationToken cancellationToken = default) =>
		_controller.CaptureClipPreviewAsync(outputPath, cancellationToken);

	private static System.Numerics.Vector3? ToVector(float[]? values)
	{
		if (values is null)
		{
			return null;
		}

		if (values.Length != 3)
		{
			throw new ArgumentException("Vectors take exactly three numbers.");
		}

		return new System.Numerics.Vector3(values[0], values[1], values[2]);
	}
}
