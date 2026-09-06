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

	[McpServerTool(Name = "capture_frame"), Description("Capture a PNG from the currently running editor on its next rendered frame. This never launches a separate editor process.")]
	public Task<FrameCaptureResult> CaptureFrame(
		[Description("Absolute or project-relative PNG output path.")] string outputPath,
		CancellationToken cancellationToken) =>
		_controller.CaptureFrameAsync(outputPath, cancellationToken);

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
