using ImGuiNET;

namespace WolfEngine.Editor.UI;

public abstract class EditorWindow
{
	private string? _workspaceImGuiName;
	private Action? _closeRequested;
	private bool _focusRequested;

	public abstract string Name { get; }
	public abstract void Draw(EditorScene scene);
	public virtual bool CanOpen(out string? reason) { reason = null; return true; }
	public virtual void OnOpened() { }

	internal void DrawInWorkspace(EditorScene scene, Guid workspaceId, string windowId, Action closeRequested)
	{
		_workspaceImGuiName = $"{Name}###{workspaceId:N}-{windowId}";
		_closeRequested = closeRequested;
		try { Draw(scene); }
		finally { _workspaceImGuiName = null; _closeRequested = null; }
	}

	internal string GetWorkspaceImGuiName(Guid workspaceId, string windowId) => $"{Name}###{workspaceId:N}-{windowId}";
	internal void RequestFocus() => _focusRequested = true;
	protected void CloseCurrentWorkspaceWindow() => _closeRequested?.Invoke();

	protected void Begin()
	{
		if (_focusRequested) { ImGui.SetNextWindowFocus(); _focusRequested = false; }
		var isOpen = true;
		ImGui.Begin(_workspaceImGuiName ?? Name, ref isOpen);
		if (!isOpen) _closeRequested?.Invoke();
		FocusOnRightClickStart();
	}

	protected void Begin(ref bool isOpen)
	{
		if (_focusRequested) { ImGui.SetNextWindowFocus(); _focusRequested = false; }
		ImGui.Begin(_workspaceImGuiName ?? Name, ref isOpen);
		if (!isOpen) _closeRequested?.Invoke();
		FocusOnRightClickStart();
	}

	protected static void FocusOnRightClickStart()
	{
		if (ImGui.IsWindowHovered(ImGuiHoveredFlags.AllowWhenBlockedByActiveItem) &&
		    ImGui.IsMouseClicked(ImGuiMouseButton.Right))
		{
			ImGui.SetWindowFocus();
		}
	}
}
