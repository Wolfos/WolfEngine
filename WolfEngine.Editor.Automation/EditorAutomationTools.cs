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

	[McpServerTool(Name = "shutdown_editor"), Description("Gracefully shut down the running WolfEngine Editor while leaving this MCP server available.")]
	public async Task<string> ShutdownEditor(CancellationToken cancellationToken)
	{
		await _controller.ShutdownAsync(cancellationToken).ConfigureAwait(false);
		return "WolfEngine Editor has shut down.";
	}
}
