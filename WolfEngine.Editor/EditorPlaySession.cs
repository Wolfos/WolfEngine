using WolfEngine.ECS;

namespace WolfEngine.Editor;

public enum EditorPlayState
{
	Edit,
	Playing,
	Paused
}

public interface IEditorPlaySession
{
	EditorPlayState State { get; }
	bool IsActive { get; }
	EditorScene AuthoringScene { get; }
	EditorScene ActiveScene { get; }
	EditorScene? RuntimeScene { get; }
	bool EnterPlay();
	bool Pause();
	bool Resume();
	bool Stop();
	void Restart(EditorPlayState targetState);
}

public sealed class EditorPlaySession : IEditorPlaySession
{
	private readonly IEditorSceneWorkspace _sceneWorkspace;
	private readonly IEditorSceneReloadService _sceneReloadService;
	private readonly IWorldManager _worldManager;
	private EditorScene? _runtimeScene;

	public EditorPlaySession(
		IEditorSceneWorkspace sceneWorkspace,
		IEditorSceneReloadService sceneReloadService,
		IWorldManager worldManager)
	{
		_sceneWorkspace = sceneWorkspace ?? throw new ArgumentNullException(nameof(sceneWorkspace));
		_sceneReloadService = sceneReloadService ?? throw new ArgumentNullException(nameof(sceneReloadService));
		_worldManager = worldManager ?? throw new ArgumentNullException(nameof(worldManager));
	}

	public EditorPlayState State { get; private set; } = EditorPlayState.Edit;

	public bool IsActive => State != EditorPlayState.Edit;

	public EditorScene AuthoringScene => _sceneWorkspace.CurrentScene;

	public EditorScene ActiveScene => _runtimeScene ?? AuthoringScene;

	public EditorScene? RuntimeScene => _runtimeScene;

	public bool EnterPlay()
	{
		if (State != EditorPlayState.Edit)
		{
			return false;
		}

		var snapshot = _sceneReloadService.Capture(AuthoringScene);
		_runtimeScene = _sceneReloadService.Restore(snapshot, WorldTag.Game);
		_worldManager.RegisterWorld(_runtimeScene.World);
		State = EditorPlayState.Playing;
		return true;
	}

	public bool Pause()
	{
		if (State != EditorPlayState.Playing)
		{
			return false;
		}

		State = EditorPlayState.Paused;
		return true;
	}

	public bool Resume()
	{
		if (State != EditorPlayState.Paused)
		{
			return false;
		}

		State = EditorPlayState.Playing;
		return true;
	}

	public bool Stop()
	{
		if (_runtimeScene is null)
		{
			State = EditorPlayState.Edit;
			return false;
		}

		_worldManager.RemoveWorld(_runtimeScene.World);
		_runtimeScene = null;
		State = EditorPlayState.Edit;
		return true;
	}

	public void Restart(EditorPlayState targetState)
	{
		Stop();
		if (targetState == EditorPlayState.Edit)
		{
			return;
		}

		EnterPlay();
		if (targetState == EditorPlayState.Paused)
		{
			Pause();
		}
	}
}
