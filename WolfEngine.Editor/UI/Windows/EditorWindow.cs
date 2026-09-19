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

	internal bool IsSelectedTab { get; private set; }

	internal bool IsFocused { get; private set; }

	internal void DrawInWorkspace(EditorScene scene, Guid workspaceId, string windowId, Action closeRequested)
	{
		_workspaceImGuiName = $"{Name}###{workspaceId:N}-{windowId}";
		_closeRequested = closeRequested;
		IsSelectedTab = false;
		IsFocused = false;
		try { Draw(scene); }
		finally { _workspaceImGuiName = null; _closeRequested = null; }
	}

	internal string GetWorkspaceImGuiName(Guid workspaceId, string windowId) => $"{Name}###{workspaceId:N}-{windowId}";
	internal void RequestFocus() => _focusRequested = true;
	protected void CloseCurrentWorkspaceWindow() => _closeRequested?.Invoke();

	protected void Begin()
	{
		var isOpen = true;
		Begin(ref isOpen);
	}

	protected void Begin(ref bool isOpen)
	{
		if (_focusRequested) { ImGui.SetNextWindowFocus(); _focusRequested = false; }
		var visible = ImGui.Begin(_workspaceImGuiName ?? Name, ref isOpen);
		IsSelectedTab = visible && ImGui.IsWindowDocked();
		IsFocused = ImGui.IsWindowFocused();
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
