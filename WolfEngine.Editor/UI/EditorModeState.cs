namespace WolfEngine.Editor.UI;

public interface IEditorModeState
{
	EditorMode CurrentMode { get; }
	void SetMode(EditorMode mode);
}

public sealed class EditorModeState : IEditorModeState
{
	public EditorMode CurrentMode { get; private set; } = EditorMode.Scene;

	public void SetMode(EditorMode mode)
	{
		CurrentMode = mode;
	}
}
