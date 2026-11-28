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
	public async Task<string> SetEditorCamera(
		float x,
		float y,
		float z,
		float targetX,
		float targetY,
		float targetZ,
		CancellationToken cancellationToken,
		[Description("Optional vertical field of view in degrees, for example the gameplay camera's, so an Edit-mode capture frames the same view as capture_gameplay_frame.")] float? fov = null,
		[Description("Optional near clip plane distance. Shadow cascades and depth follow the clip range, so match the gameplay camera's for like-for-like comparisons.")] float? nearPlane = null,
		[Description("Optional far clip plane distance.")] float? farPlane = null)
	{
		await _controller.SetEditorCameraAsync(
			new(x, y, z),
			new(targetX, targetY, targetZ),
			new EditorCameraLens(fov, nearPlane, farPlane),
			cancellationToken);
		return "Editor camera updated in memory.";
	}

	[McpServerTool(Name = "create_entity"), Description("Create an empty entity through scene authoring, the way the hierarchy's Create menu does, without saving the scene. In the authoring scene it is one undo step; during Play mode it is created in the runtime scene and not recorded.")]
	public async Task<string> CreateEntity(
		[Description("Optional entity name.")] string? name = null,
		CancellationToken cancellationToken = default)
	{
		var result = await _controller.CreateEntityAsync(name, cancellationToken).ConfigureAwait(false);
		return $"Created entity '{result.Name}' with id {result.EntityId:D}.";
	}

	[McpServerTool(Name = "delete_entity"), Description("Delete an entity and its children by persistent GUID through scene authoring, the way the hierarchy's Delete does. In the authoring scene it is one undo step; during Play mode it acts on the runtime scene and is not recorded. Refused for entities inside a prefab instance: delete the prefab root instead.")]
	public async Task<string> DeleteEntity(
		[Description("Persistent entity GUID.")] string entityId,
		CancellationToken cancellationToken)
	{
		await _controller.DeleteEntityAsync(entityId, cancellationToken).ConfigureAwait(false);
		return $"Deleted entity {entityId}.";
	}

	[McpServerTool(Name = "instantiate_model"), Description("Instantiate an imported 3D model by asset name, searching the project's asset catalog including mounted engine assets, through scene authoring the way the Assets window's Add to Scene does. In the authoring scene it is one undo step, plus one more when a uniform scale is applied; during Play mode it goes into the runtime scene unrecorded. Reports how many mesh renderers, skinned mesh renderers and animators the model brought with it, and each mesh renderer's level of detail count.")]
	public Task<InstantiatedModelResult> InstantiateModel(
		[Description("Substring of the 3D model asset name, such as 'Macarena'.")] string assetName,
		[Description("Optional spawn X in world units.")] float? x = null,
		[Description("Optional spawn Y in world units.")] float? y = null,
		[Description("Optional spawn Z in world units.")] float? z = null,
		[Description("Uniform scale applied to the instantiated root. Useful for sources authored in centimetres.")] float uniformScale = 1.0f,
		CancellationToken cancellationToken = default) =>
		_controller.InstantiateModelAsync(assetName, x, y, z, uniformScale, cancellationToken);

	[McpServerTool(Name = "save_entity_as_prefab"), Description("Save an authoring-scene entity and its children as a prefab asset, through the same path as the hierarchy's Save as Prefab. The prefab is written to disk immediately; the authoring scene itself is not saved.")]
	public Task<SavedPrefabResult> SaveEntityAsPrefab(
		[Description("Persistent entity GUID, such as the rootEntityId from instantiate_model.")] string entityId,
		[Description("Project-relative folder for the prefab, such as 'Assets/Prefabs'. The file is named after the entity.")] string folderPath,
		CancellationToken cancellationToken = default) =>
		_controller.SaveEntityAsPrefabAsync(entityId, folderPath, cancellationToken);

	[McpServerTool(Name = "play_vfx"), Description("Play a VFX asset in the running Play-mode scene, at a world position or attached to an entity with that position as its offset. Effects advance with the gameplay world, so enter Play mode first.")]
	public Task<VfxPlayResult> PlayVfx(
		[Description("Project-relative path of the VFX data asset, such as 'Assets/VFX/SwordImpact.data.json'.")] string assetPath,
		[Description("World X, or offset X when attached.")] float x = 0.0f,
		[Description("World Y, or offset Y when attached.")] float y = 0.0f,
		[Description("World Z, or offset Z when attached.")] float z = 0.0f,
		[Description("Optional persistent entity GUID to attach the effect to.")] string? attachToEntityId = null,
		CancellationToken cancellationToken = default) =>
		_controller.PlayVfxAsync(assetPath, new System.Numerics.Vector3(x, y, z), attachToEntityId, cancellationToken);

	[McpServerTool(Name = "sample_texture"), Description("Load an imported texture's runtime artifact for the running backend onto the GPU and sample it in a compute shader through the bindless GetTexture or GetTextureCube accessor. Returns RGBA per coordinate as the GPU decodes it, which checks block compression, cube face layout and the bindless view for the texture's dimension. Reports the texture's dimension, format and size.")]
	public Task<TextureSampleResult> SampleTexture(
		[Description("Project-relative path of the texture's source file, such as 'Assets/Skies/Sunset.hdr'.")] string assetPath,
		[Description("Coordinates to sample: a direction [x, y, z] for a cube texture, or a UV [u, v] for a 2D texture. At most 4096.")] float[][] coordinates,
		[Description("Mip level to sample.")] float mipLevel = 0.0f,
		[Description("Sample with bilinear filtering instead of point sampling.")] bool linearFilter = false,
		CancellationToken cancellationToken = default) =>
		_controller.SampleTextureAsync(assetPath, coordinates, mipLevel, linearFilter, cancellationToken);

	[McpServerTool(Name = "get_vfx_state"), Description("Report the Play-mode world's playing effects and how much of its particle pool they reserve.")]
	public Task<VfxStateResult> GetVfxState(CancellationToken cancellationToken) =>
		_controller.GetVfxStateAsync(cancellationToken);

	[McpServerTool(Name = "stop_all_vfx"), Description("Stop every effect in the Play-mode world. Without immediate, particles that are alive finish their lives.")]
	public Task<VfxStateResult> StopAllVfx(bool immediate = false, CancellationToken cancellationToken = default) =>
		_controller.StopAllVfxAsync(immediate, cancellationToken);

	[McpServerTool(Name = "configure_vfx_preview"), Description("Configure the VFX editor's preview of the selected VFX asset. Select the asset with select_asset and wait for render frames first. Pause and advance to step the effect deterministically before capture_vfx_preview.")]
	public Task<VfxPreviewStateResult> ConfigureVfxPreview(
		[Description("Optional motion: Still, Circle or Swing.")] string? motion = null,
		[Description("Optional: play or pause the preview clock.")] bool? playing = null,
		[Description("Optional seconds to advance the preview, in 1/60 s steps. Works while paused.")] float? advanceSeconds = null,
		[Description("Optional camera yaw in radians; give pitch and distance too.")] float? yaw = null,
		[Description("Optional camera pitch in radians.")] float? pitch = null,
		[Description("Optional camera distance in metres.")] float? distance = null,
		[Description("Optional model or prefab asset GUID to preview with; empty GUID removes it.")] string? modelId = null,
		[Description("Optional name of the model entity to attach the effect to, such as 'Sword'.")] string? attachTo = null,
		[Description("Restart the effect from time zero.")] bool restart = false,
		CancellationToken cancellationToken = default) =>
		_controller.ConfigureVfxPreviewAsync(
			motion,
			playing,
			advanceSeconds,
			yaw.HasValue && pitch.HasValue && distance.HasValue ? new System.Numerics.Vector3(yaw.Value, pitch.Value, distance.Value) : null,
			modelId is null ? null : Guid.Parse(modelId),
			attachTo,
			restart,
			cancellationToken);

	[McpServerTool(Name = "capture_vfx_preview"), Description("Write a PNG of the VFX editor's preview view, rendered by the real renderer.")]
	public Task<FrameCaptureResult> CaptureVfxPreview(
		[Description("Absolute or project-relative PNG output path.")] string outputPath,
		CancellationToken cancellationToken) =>
		_controller.CaptureVfxPreviewAsync(outputPath, cancellationToken);

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

	[McpServerTool(Name = "create_project"), Description("Create a new project and switch the running editor to it, through the same path as File > New Project: initialise Assets and Library, scaffold the gameplay project and open it. Start the editor on any existing project first. Fails if the project folder already exists.")]
	public async Task<string> CreateProject(
		[Description("Existing folder the project folder is created in.")] string parentFolder,
		[Description("Project name, which is also the new folder's name.")] string projectName,
		CancellationToken cancellationToken)
	{
		var projectRoot = await _controller.CreateProjectAsync(parentFolder, projectName, cancellationToken).ConfigureAwait(false);
		return $"Created and opened project '{projectRoot}'.";
	}

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

	[McpServerTool(Name = "enter_play_mode"), Description("Enter Play mode from the current authoring scene, or resume it when paused. Play mode creates an isolated runtime scene without saving or mutating the authoring scene.")]
	public Task<PlayModeStateResult> EnterPlayMode(CancellationToken cancellationToken) =>
		_controller.EnterPlayModeAsync(cancellationToken);

	[McpServerTool(Name = "pause_play_mode"), Description("Pause the currently running Play-mode scene.")]
	public Task<PlayModeStateResult> PausePlayMode(CancellationToken cancellationToken) =>
		_controller.PausePlayModeAsync(cancellationToken);

	[McpServerTool(Name = "set_game_pause"), Description("Hold or release one game pause reason on the running Play-mode world, the way gameplay code pauses for a menu with World.Pause and World.Resume. The world is paused while any reason is held: WorldTag.Game systems stop and WorldTag.GamePaused/AnyGame systems run. This is the game's own pause, independent of pause_play_mode. Results report the held reasons as the gamePauseReasons bit mask.")]
	public Task<PlayModeStateResult> SetGamePause(
		[Description("The reason's bit index, 0 to 31, as declared with new PauseReason(index) in gameplay code.")] int reasonIndex,
		[Description("True to hold the reason; false to release it.")] bool paused,
		CancellationToken cancellationToken) =>
		_controller.SetGamePauseAsync(reasonIndex, paused, cancellationToken);

	[McpServerTool(Name = "quit_game"), Description("Quit the running Play-mode game the way a gameplay Quit button does, through IApplicationQuitService. In the editor this stops Play mode rather than closing the editor; a built game would exit. Returns the Play state after the editor has handled the request.")]
	public Task<PlayModeStateResult> QuitGame(CancellationToken cancellationToken) =>
		_controller.QuitGameAsync(cancellationToken);

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
		float wheel = 0.0f,
		float wheelX = 0.0f,
		[Description("Pinch magnification delta, e.g. 0.2 for a 20% increase or -0.2 for a 20% decrease.")] float magnification = 0.0f,
		[Description("Optional Control key state for modifier-scroll zoom.")] bool? control = null,
		[Description("Send trackpad scrolling instead of a mouse wheel; each unit pans 35 logical points.")] bool preciseScroll = false)
	{
		await _controller.SetEditorPointerAsync(
			new(x, y),
			pressed,
			button,
			wheel,
			cancellationToken,
			wheelX,
			magnification,
			control,
			preciseScroll).ConfigureAwait(false);
		return $"Editor pointer at ({x}, {y}).";
	}

	[McpServerTool(Name = "get_animation_editor_state"), Description("Read the open graph's last-drawn canvas, node and port bounds, zoom, selected node, toolbar bounds, and unsaved undo/redo state. Wait for render frames after an interaction before inspecting these coordinates.")]
	public Task<UI.AnimationGraphEditorState> GetAnimationEditorState(CancellationToken cancellationToken) =>
		_controller.GetAnimationEditorStateAsync(cancellationToken);

	[McpServerTool(Name = "get_asset_browser_state"), Description("Read the asset browser location, searches, selection, sub-asset filter, visible card and row bounds, and control bounds after a drawn frame.")]
	public Task<UI.AssetBrowserState> GetAssetBrowserState(CancellationToken cancellationToken) =>
		_controller.GetAssetBrowserStateAsync(cancellationToken);

	[McpServerTool(Name = "configure_asset_browser"), Description("Navigate the asset browser and optionally search or open a source's sub-assets. Changes UI state only; does not edit or import assets.")]
	public async Task<string> ConfigureAssetBrowser(
		string folderPath,
		CancellationToken cancellationToken,
		string search = "",
		string? expandedSourceId = null,
		string subAssetSearch = "")
	{
		await _controller.ConfigureAssetBrowserAsync(
			folderPath,
			search,
			expandedSourceId is null ? null : Guid.Parse(expandedSourceId),
			subAssetSearch,
			cancellationToken).ConfigureAwait(false);

		return $"Asset browser: {folderPath}.";
	}

	[McpServerTool(Name = "get_entities_window_state"), Description("Read the Entities window's search text and the rows it drew in the last frame, in tree order, with whether each row matches the search. Ancestors shown only to place a match report false.")]
	public Task<UI.EntitiesWindowState> GetEntitiesWindowState(CancellationToken cancellationToken) =>
		_controller.GetEntitiesWindowStateAsync(cancellationToken);

	[McpServerTool(Name = "set_entities_search"), Description("Set the Entities window's search text, as typing into its search bar does. An empty string clears the search. Changes UI state only.")]
	public async Task<string> SetEntitiesSearch(string search, CancellationToken cancellationToken)
	{
		await _controller.SetEntitiesSearchAsync(search, cancellationToken).ConfigureAwait(false);

		return $"Entities search: '{search}'.";
	}

	[McpServerTool(Name = "set_editor_key"), Description("Press or release an ImGui editor key such as LeftShift, Space, Escape or Delete. Does not inject gameplay input.")]
	public async Task<string> SetEditorKey(string key, bool pressed, CancellationToken cancellationToken)
	{
		await _controller.SetEditorKeyAsync(key, pressed, cancellationToken).ConfigureAwait(false);

		return $"Editor key {key}: {(pressed ? "pressed" : "released")}.";
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

	[McpServerTool(Name = "wait_for_render_frames"), Description("Wait for completed render-graph frames, rather than editor update ticks or frames that only redrew the UI, and return the editor, presented-frame and scene-frame sequence numbers.")]
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

	[McpServerTool(Name = "configure_workspace"), Description("Rename an editor workspace without changing its stable layout identity, and optionally change the document it owns (None, Scene, AnimationGraph or AnimationClip). Save, Undo and Redo act on the active workspace's owned document; omit owned_document to keep it.")]
	public Task<EditorWorkspaceStateResult> ConfigureWorkspace(
		string workspaceId,
		string name,
		string? ownedDocument = null,
		CancellationToken cancellationToken = default) =>
		_controller.ConfigureWorkspaceAsync(workspaceId, name, ownedDocument, cancellationToken);

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

	[McpServerTool(Name = "sculpt_terrain"), Description("Apply one raise or lower heightmap stamp through the editor's real authoring and undo path. Placed foliage under the brush moves with the ground in the same undo step.")]
	public Task<TerrainSculptResult> SculptTerrain(
		[Description("Persistent terrain entity GUID. Omit when the scene contains exactly one terrain.")] string? terrainEntityId = null,
		[Description("Terrain-local X coordinate in metres.")] float localX = 0.0f,
		[Description("Terrain-local Z coordinate in metres.")] float localZ = 0.0f,
		[Description("Brush radius in metres.")] float radiusMeters = 8.0f,
		[Description("Brush strength from 0 through 1.")] float strength = 1.0f,
		[Description("Positive brush falloff exponent.")] float falloff = 1.0f,
		[Description("Lower the terrain instead of raising it.")] bool invert = false,
		CancellationToken cancellationToken = default) =>
		_controller.SculptTerrainAsync(
			terrainEntityId,
			localX,
			localZ,
			radiusMeters,
			strength,
			falloff,
			invert,
			cancellationToken);

	[McpServerTool(Name = "snap_foliage_to_terrain"), Description("Put every placed-foliage instance of a terrain on its surface, as one undoable edit. Returns how many instances moved and the total.")]
	public Task<FoliageSnapResult> SnapFoliageToTerrain(
		[Description("Persistent terrain entity GUID. Omit when the scene contains exactly one terrain.")] string? terrainEntityId = null,
		CancellationToken cancellationToken = default) =>
		_controller.SnapFoliageToTerrainAsync(terrainEntityId, cancellationToken);

	[McpServerTool(Name = "import_unity_scene"), Description("Import a scene exported from Unity with WolfEngine.UnityExporter as a new scene in the open project, then open it. Creates the terrain, its layer set and textures, its trees as placed foliage, its prefab instances, the directional lights and the main camera, with prefabs, meshes and materials converted from the export. Returns what was imported, skipped and guessed.")]
	public Task<UnitySceneImportResult> ImportUnityScene(
		[Description("Folder the Unity exporter wrote, holding scene.json.")] string exportFolder,
		[Description("The Unity project the export came from; its textures are read from there.")] string unityProjectPath,
		[Description("Project folder under Assets for the imported assets, for example Assets/Imported/ForestDemo.")] string targetFolder,
		[Description("Name of the new scene, saved as Assets/Scenes/Name/Name.scene.json. Must not exist yet.")] string sceneName,
		[Description("Optional project path of a RenderConfig for the scene's world settings.")] string? renderConfigPath = null,
		CancellationToken cancellationToken = default) =>
		_controller.ImportUnitySceneAsync(
			exportFolder,
			unityProjectPath,
			targetFolder,
			sceneName,
			renderConfigPath,
			cancellationToken);

	[McpServerTool(Name = "paint_foliage"), Description("Apply one undoable placed-foliage stamp. Returns added, removed and total instances, plus the foliage asset GUID when assigned.")]
	public Task<FoliagePaintResult> PaintFoliage(
		[Description("Persistent terrain entity GUID. Omit when the scene contains exactly one terrain.")] string? terrainEntityId = null,
		[Description("Terrain-local X coordinate in metres.")] float localX = 0.0f,
		[Description("Terrain-local Z coordinate in metres.")] float localZ = 0.0f,
		[Description("Brush radius in metres.")] float radiusMeters = 12.0f,
		[Description("Target instances per 100 square metres in the brush circle.")] float density = 1.0f,
		[Description("Project-relative FoliageType paths. In erase mode, an empty palette targets all types.")] string[]? foliageTypePaths = null,
		[Description("Erase matching instances within the brush.")] bool erase = false,
		[Description("Optional deterministic seed.")] int? seed = null,
		CancellationToken cancellationToken = default) =>
		_controller.PaintFoliageAsync(
			terrainEntityId,
			localX,
			localZ,
			radiusMeters,
			density,
			foliageTypePaths ?? [],
			erase,
			seed,
			cancellationToken);

	[McpServerTool(Name = "reload_engine_shaders"), Description("Reload the engine shaders the way the editor's Reload Engine Shaders command does, and return how many shader artifacts were replaced, any compile failures, and the new shader revision. The renderer switches to the new shaders at the start of its next frame, so wait for render frames before capturing.")]
	public Task<EngineShaderReloadResult> ReloadEngineShaders(CancellationToken cancellationToken) =>
		_controller.ReloadEngineShadersAsync(cancellationToken);

	[McpServerTool(Name = "undo"), Description("Undo the most recent editor authoring action, including entity create, delete, instantiate and transform edits and terrain strokes.")]
	public Task<EditorUndoResult> Undo(CancellationToken cancellationToken) =>
		_controller.UndoAsync(cancellationToken);

	[McpServerTool(Name = "redo"), Description("Redo the most recently undone editor authoring action.")]
	public Task<EditorUndoResult> Redo(CancellationToken cancellationToken) =>
		_controller.RedoAsync(cancellationToken);

	[McpServerTool(Name = "get_gameplay_state"), Description("Return the loaded gameplay assembly's generation and path, whether the gameplay source changed since that build (rescanned now), whether a build is running, and whether the last build failed for the current source.")]
	public Task<GameplayStateResult> GetGameplayState(CancellationToken cancellationToken) =>
		_controller.GetGameplayStateAsync(cancellationToken);

	[McpServerTool(Name = "get_ray_tracing_scene_state"), Description("Return the latest renderer-side TLAS, BLAS, terrain, retirement, and GPU-submission diagnostics without synchronizing or restarting the renderer.")]
	public Task<RayTracingSceneStateResult> GetRayTracingSceneState(CancellationToken cancellationToken) =>
		_controller.GetRayTracingSceneStateAsync(cancellationToken);

	[McpServerTool(Name = "get_foliage_state"), Description("Return the placed foliage in the last frame's draw tables: terrain foliage sets, instances, the renderer instances they could expand into, batch reservations and reserved visible slots, the foliage expansion region's capacity, and the TLAS instance count.")]
	public Task<FoliageStateResult> GetFoliageState(CancellationToken cancellationToken) =>
		_controller.GetFoliageStateAsync(cancellationToken);

	[McpServerTool(Name = "profile_gpu_frames"), Description("Enable the existing GPU profiler and aggregate timing statistics over multiple completed GPU frames. Results include median, p95, and maximum per render pass and nested scope.")]
	public Task<GpuFrameProfileResult> ProfileGpuFrames(
		[Description("Positive number of completed GPU-profile frames to aggregate.")] int frameCount,
		CancellationToken cancellationToken) =>
		_controller.ProfileGpuFramesAsync(frameCount, cancellationToken);

	[McpServerTool(Name = "get_cpu_frame_profile"), Description("Return the latest completed CPU profiler tree for every profiled thread, including duration and managed allocation per scope.")]
	public Task<CpuFrameProfileResult> GetCpuFrameProfile(CancellationToken cancellationToken) =>
		_controller.GetCpuFrameProfileAsync(cancellationToken);

	[McpServerTool(Name = "set_viewport_frame_limit"), Description("Limit how often the 3D views are drawn while the editor UI keeps running every frame, as the Limit viewport FPS preference does. In memory only. Compare wait_for_render_frames' scene-frame and presented-frame sequences to measure it.")]
	public Task<string> SetViewportFrameLimit(
		[Description("Maximum scene frames per second; 0 turns the limit off. Values below 20 are raised to 20.")] int maxFramesPerSecond,
		CancellationToken cancellationToken) =>
		_controller.SetViewportFrameLimitAsync(maxFramesPerSecond, cancellationToken);

	[McpServerTool(Name = "set_anti_aliasing"), Description("Switch the authoring scene's anti-aliasing method in memory without saving its render config asset. Use with frame waits, GPU profiling, and captures to compare TAA/CAS and FSR3.")]
	public Task<string> SetAntiAliasing(
		[Description("Taa or Fsr3.")] string mode,
		[Description("Whether temporal anti-aliasing is enabled.")] bool enabled = true,
		[Description("Whether to sharpen TAA with CAS after tonemapping. Does not affect FSR3 RCAS.")] bool casSharpening = true,
		CancellationToken cancellationToken = default) =>
		_controller.SetAntiAliasingAsync(mode, enabled, casSharpening, cancellationToken);

	[McpServerTool(Name = "set_sky"), Description("Switch the active scene's sky mode and its settings in memory without saving its render config asset. Settings left out keep their current values.")]
	public Task<string> SetSky(
		[Description("Procedural, Hdri or Gradient.")] string mode,
		[Description("Project-relative source path of an imported HDR texture to use in HDRI mode, such as 'Assets/Skies/Sunset.hdr'.")] string? hdriAssetPath = null,
		[Description("Clear the HDRI texture, which draws a black sky in HDRI mode.")] bool clearHdri = false,
		[Description("HDRI rotation about the vertical axis, in degrees.")] float? hdriRotationDegrees = null,
		[Description("HDRI radiance multiplier.")] float? hdriIntensity = null,
		[Description("Gradient sky color multiplier.")] float? gradientIntensity = null,
		[Description("Procedural sky: Mie scattering at sea level, 1/km.")] float? mieScatteringScale = null,
		[Description("Procedural sky: Mie absorption at sea level, 1/km.")] float? mieAbsorptionScale = null,
		[Description("Procedural sky: Mie anisotropy, 0 to just under 1.")] float? mieAnisotropy = null,
		[Description("Procedural sky: Rayleigh scattering of the strongest channel at sea level, 1/km.")] float? rayleighScatteringScale = null,
		[Description("Procedural sky: grey ground albedo, 0 to 1.")] float? groundAlbedo = null,
		[Description("Sky lighting quality, High or Low. Left out keeps the current one.")] string? lightingQuality = null,
		CancellationToken cancellationToken = default) =>
		_controller.SetSkyAsync(
			mode,
			hdriAssetPath,
			clearHdri,
			hdriRotationDegrees,
			hdriIntensity,
			gradientIntensity,
			new ProceduralSkyOverrides(mieScatteringScale, mieAbsorptionScale, mieAnisotropy, rayleighScatteringScale, groundAlbedo),
			lightingQuality,
			cancellationToken);

	[McpServerTool(Name = "set_clouds"), Description("Change the active scene's volumetric clouds in memory without saving its render config asset. Clouds only render with the Procedural sky. Settings left out keep their current values.")]
	public Task<string> SetClouds(
		[Description("Draw the cloud layer.")] bool? enabled = null,
		[Description("Fraction of the sky covered, 0 to 1.")] float? coverage = null,
		[Description("Density multiplier, 0 to 4.")] float? density = null,
		[Description("Cloud base altitude above the ground, in metres.")] float? cloudBaseAltitude = null,
		[Description("Layer thickness, in metres.")] float? cloudThickness = null,
		[Description("Cloud size multiplier, 0.1 to 10.")] float? shapeScale = null,
		[Description("Edge erosion strength, 0 to 1.")] float? detailStrength = null,
		[Description("Compass direction the wind blows toward, in degrees: 0 toward +Z, 90 toward +X.")] float? windDirection = null,
		[Description("Wind speed, in metres per second.")] float? windSpeed = null,
		[Description("Skylight multiplier, 0 to 4.")] float? ambientStrength = null,
		[Description("Multiple-scattering strength, 0 to 1.")] float? multipleScattering = null,
		[Description("Low, Medium or High.")] string? quality = null,
		[Description("Whether clouds shadow the sun.")] bool? castShadows = null,
		CancellationToken cancellationToken = default) =>
		_controller.SetCloudsAsync(
			new CloudOverrides(
				enabled,
				coverage,
				density,
				cloudBaseAltitude,
				cloudThickness,
				shapeScale,
				detailStrength,
				windDirection,
				windSpeed,
				ambientStrength,
				multipleScattering,
				quality,
				castShadows),
			cancellationToken);

	[McpServerTool(Name = "set_sun"), Description("Point the active scene's first directional light at a sun elevation and azimuth in memory, without saving the scene. Azimuth 0 puts the sun toward +Z and 90 toward +X.")]
	public Task<string> SetSun(
		[Description("Sun elevation above the horizon, in degrees. Negative is below the horizon.")] float elevationDegrees,
		[Description("Sun azimuth in degrees: 0 toward +Z, 90 toward +X.")] float azimuthDegrees = 0.0f,
		[Description("Light intensity. Left out keeps the light's own.")] float? intensity = null,
		CancellationToken cancellationToken = default) =>
		_controller.SetSunAsync(elevationDegrees, azimuthDegrees, intensity, cancellationToken);

	[McpServerTool(Name = "set_tonemapping_exposure"), Description("Set the active scene's tonemapping exposure in memory without saving its render config.")]
	public Task<string> SetTonemappingExposure(float exposure, CancellationToken cancellationToken) =>
		_controller.SetTonemappingExposureAsync(exposure, cancellationToken);

	[McpServerTool(Name = "set_fsr3_settings"), Description("Set the active scene's FSR3 settings in memory without saving its render config: alpha-test reactivity, the transparency and composition mask scale, and RCAS sharpening.")]
	public Task<string> SetFsr3Settings(
		[Description("Reactivity for surviving alpha-tested samples, 0 to 1.")] float alphaTestReactiveScale,
		[Description("Scale of the transparent-forward composition mask, 0 to 1.")] float transparencyAndCompositionMaskScale,
		[Description("Whether RCAS sharpening runs.")] bool sharpening,
		[Description("RCAS sharpness, 0 to 1.")] float sharpness,
		CancellationToken cancellationToken = default) =>
		_controller.SetFsr3SettingsAsync(
			alphaTestReactiveScale,
			transparencyAndCompositionMaskScale,
			sharpening,
			sharpness,
			cancellationToken);

	[McpServerTool(Name = "set_render_scale"), Description("Set the active scene's render scale in memory without saving its render config. The scene renders at this per-axis fraction of the output; FSR3 upscales temporally, TAA is filtered up by tonemapping. Clamped to 0.25..1.")]
	public Task<string> SetRenderScale(
		[Description("Per-axis fraction of the output resolution, 0.25 to 1.")] float renderScale,
		CancellationToken cancellationToken = default) =>
		_controller.SetRenderScaleAsync(renderScale, cancellationToken);

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
		bool volumetricFog, CancellationToken cancellationToken,
		[Description("Optional volumetric fog shadow dimming, from 0 to 1.")] float? fogShadowDimming = null) =>
		_controller.SetRenderEffectsAsync(
			ambientOcclusion, reflections, diffuseGlobalIllumination, volumetricFog, fogShadowDimming, cancellationToken);

	[McpServerTool(Name = "set_shadow_caster_culling"), Description("Enable or disable tighter shadow-caster culling in memory without saving the render config. Use for identical-scene capture and GPU timing comparisons.")]
	public Task<string> SetShadowCasterCulling(bool enabled, CancellationToken cancellationToken) =>
		_controller.SetShadowCasterCullingAsync(enabled, cancellationToken);

	[McpServerTool(Name = "set_mesh_lod"), Description("Change mesh level of detail selection in the active scene's render config in memory, without saving the asset. Disabling it draws every mesh at level 0, for identical-scene capture and GPU timing comparisons.")]
	public Task<string> SetMeshLod(
		CancellationToken cancellationToken,
		[Description("Optional: select levels (true) or draw every mesh at level 0 (false).")] bool? enabled = null,
		[Description("Optional screen size multiplier. Above one keeps detail longer.")] float? bias = null,
		[Description("Optional multiplier on the bias for shadow casters. Below one makes shadows coarser.")] float? shadowBias = null) =>
		_controller.SetMeshLodAsync(enabled, bias, shadowBias, cancellationToken);

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

	[McpServerTool(Name = "set_entity_scale"), Description("Set an entity's local scale in the active authoring or Play-mode scene without saving, as one scene-authoring transform edit: undoable in the authoring scene, unrecorded in Play mode. Supports per-axis negative scales for mirrored-geometry validation.")]
	public async Task<string> SetEntityScale(string entityId, float x, float y, float z, CancellationToken cancellationToken)
	{
		await _controller.SetEntityScaleAsync(Guid.Parse(entityId), new System.Numerics.Vector3(x, y, z), cancellationToken);
		return $"Entity {entityId}: scale ({x}, {y}, {z}). Changed in memory only.";
	}

	[McpServerTool(Name = "set_entity_rotation"), Description("Set an entity's local Euler rotation in degrees in the active authoring or Play-mode scene, without saving, as one scene-authoring transform edit: undoable in the authoring scene, unrecorded in Play mode.")]
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

	[McpServerTool(Name = "preview_splash_screen"), Description("Hold the engine splash screen at a moment of its animation, covering the editor window, so capture_editor_window or capture_frame can record it. The splash is baked from the engine's sources on first use. Omit time_seconds to hide it again.")]
	public Task<SplashScreenPreviewResult> PreviewSplashScreen(
		[Description("Seconds into the splash animation: the trace runs 0.3-1.9, the logo fills in by 2.4 and the text by 3.05. Omit to hide the splash.")] float? timeSeconds = null,
		CancellationToken cancellationToken = default) =>
		_controller.PreviewSplashScreenAsync(timeSeconds, cancellationToken);

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
