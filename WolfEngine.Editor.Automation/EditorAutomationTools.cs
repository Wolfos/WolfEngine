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

	[McpServerTool(Name = "load_scene"), Description("Load a scene from the open project's asset database through the editor's normal scene-replacement path. The persistent editor and renderer remain running until the scene load has completed.")]
	public Task<SceneLoadResult> LoadScene(
		[Description("Absolute or project-relative path of a .scene.json asset.")] string scenePath,
		CancellationToken cancellationToken) =>
		_controller.LoadSceneAsync(scenePath, cancellationToken);

	[McpServerTool(Name = "wait_for_render_frames"), Description("Wait for completed render-graph frames, rather than editor update ticks, and return the editor and render sequence numbers.")]
	public Task<RenderFrameWaitResult> WaitForRenderFrames(
		[Description("Positive number of completed render frames to wait for.")] int frameCount,
		CancellationToken cancellationToken) =>
		_controller.WaitForRenderFramesAsync(frameCount, cancellationToken);

	[McpServerTool(Name = "get_ray_tracing_scene_state"), Description("Return the latest renderer-side TLAS, BLAS, terrain, retirement, and GPU-submission diagnostics without synchronizing or restarting the renderer.")]
	public Task<RayTracingSceneStateResult> GetRayTracingSceneState(CancellationToken cancellationToken) =>
		_controller.GetRayTracingSceneStateAsync(cancellationToken);

	[McpServerTool(Name = "profile_gpu_frames"), Description("Enable the existing GPU profiler and aggregate timing statistics over multiple completed GPU frames. Results include median, p95, and maximum per render pass and nested scope.")]
	public Task<GpuFrameProfileResult> ProfileGpuFrames(
		[Description("Positive number of completed GPU-profile frames to aggregate.")] int frameCount,
		CancellationToken cancellationToken) =>
		_controller.ProfileGpuFramesAsync(frameCount, cancellationToken);

	[McpServerTool(Name = "capture_frame"), Description("Capture a PNG from the currently running editor on its next rendered frame. This never launches a separate editor process.")]
	public Task<FrameCaptureResult> CaptureFrame(
		[Description("Absolute or project-relative PNG output path.")] string outputPath,
		CancellationToken cancellationToken) =>
		_controller.CaptureFrameAsync(outputPath, cancellationToken);

	[McpServerTool(Name = "shutdown_editor"), Description("Gracefully shut down the running WolfEngine Editor while leaving this MCP server available.")]
	public async Task<string> ShutdownEditor(CancellationToken cancellationToken)
	{
		await _controller.ShutdownAsync(cancellationToken).ConfigureAwait(false);
		return "WolfEngine Editor has shut down.";
	}
}
