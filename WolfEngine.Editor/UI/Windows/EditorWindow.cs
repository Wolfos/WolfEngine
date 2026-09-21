using System.Numerics;
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
	public virtual void OnHidden() { }

	internal bool IsSelectedTab { get; private set; }

	internal bool IsFocused { get; private set; }

	/// <summary>Dock node the window was submitted into, or zero while it floats. Automation reads this to
	/// tell which panels share a tab bar.</summary>
	internal uint DockId { get; private set; }

	internal bool IsHovered { get; private set; }

	internal Vector2 Position { get; private set; }

	internal Vector2 Size { get; private set; }

	internal void DrawInWorkspace(EditorScene scene, Guid workspaceId, string windowId, Action closeRequested)
	{
		_workspaceImGuiName = $"{Name}###{workspaceId:N}-{windowId}";
		_closeRequested = closeRequested;
		IsSelectedTab = false;
		IsFocused = false;
		IsHovered = false;
		DockId = 0;
		Position = Vector2.Zero;
		Size = Vector2.Zero;
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
		IsHovered = ImGui.IsWindowHovered(ImGuiHoveredFlags.ChildWindows);
		DockId = ImGui.GetWindowDockID();
		Position = ImGui.GetWindowPos();
		Size = ImGui.GetWindowSize();
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
