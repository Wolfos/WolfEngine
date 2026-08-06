using WolfEngine.ECS;

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

	/// <summary>
	/// Marks the scene dirty only when <paramref name="world"/> is an authoring world. Windows draw the
	/// isolated runtime scene while play mode is active, and those edits are discarded when play mode
	/// stops, so they must not leave the authoring scene reported as unsaved.
	/// </summary>
	void MarkSceneDirty(World world);

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

	public void MarkSceneDirty(World world)
	{
		if (world is null || world.Tag != WorldTag.Authoring)
		{
			return;
		}

		IsSceneDirty = true;
	}

	public void ClearSceneDirty()
	{
		IsSceneDirty = false;
	}
}
