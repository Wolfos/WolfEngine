namespace WolfEngine.Editor.UI;

public enum EditorFocusedWindow
{
	None,
	Entities,
	Assets
}

public interface IEditorInteractionState
{
	EditorFocusedWindow FocusedWindow { get; }
	bool IsSceneDirty { get; }

	void BeginFrame();
	void SetFocusedWindow(EditorFocusedWindow window);
	void MarkSceneDirty();
	void ClearSceneDirty();
}

public sealed class EditorInteractionState : IEditorInteractionState
{
	public EditorFocusedWindow FocusedWindow { get; private set; }
	public bool IsSceneDirty { get; private set; }

	public void BeginFrame()
	{
	}

	public void SetFocusedWindow(EditorFocusedWindow window)
	{
		FocusedWindow = window;
	}

	public void MarkSceneDirty()
	{
		IsSceneDirty = true;
	}

	public void ClearSceneDirty()
	{
		IsSceneDirty = false;
	}
}
