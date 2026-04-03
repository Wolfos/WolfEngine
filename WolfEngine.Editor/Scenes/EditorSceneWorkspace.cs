using System;
using WolfEngine.ECS;

namespace WolfEngine.Editor;

public interface IEditorSceneWorkspace
{
	EditorScene CurrentScene { get; }
	void Initialize(EditorScene scene);
	void ReplaceCurrentScene(EditorScene scene);
	void ResetToNewScene();
	void SaveCurrentScene();
	void LoadScene(Guid assetId);
}

public sealed class EditorSceneWorkspace : IEditorSceneWorkspace
{
	private readonly IEditorSceneFactory _sceneFactory;
	private readonly IWorldManager _worldManager;
	private EditorScene _currentScene = new();

	public EditorSceneWorkspace(IEditorSceneFactory sceneFactory, IWorldManager worldManager)
	{
		_sceneFactory = sceneFactory ?? throw new ArgumentNullException(nameof(sceneFactory));
		_worldManager = worldManager ?? throw new ArgumentNullException(nameof(worldManager));
	}

	public EditorScene CurrentScene => _currentScene;

	public void Initialize(EditorScene scene)
	{
		ArgumentNullException.ThrowIfNull(scene);
		ArgumentNullException.ThrowIfNull(scene.World);
		_currentScene = scene;
	}

	public void ReplaceCurrentScene(EditorScene scene)
	{
		ArgumentNullException.ThrowIfNull(scene);
		ArgumentNullException.ThrowIfNull(scene.World);
		SwapWorld(scene.World);
		_currentScene = scene;
	}

	public void ResetToNewScene()
	{
		ReplaceCurrentScene(_sceneFactory.New());
	}

	public void SaveCurrentScene()
	{
		_sceneFactory.Save(_currentScene);
	}

	public void LoadScene(Guid assetId)
	{
		var loadedScene = _sceneFactory.Load(assetId);
		SwapWorld(loadedScene.World);
		_currentScene = loadedScene;
	}

	private void SwapWorld(World nextWorld)
	{
		ArgumentNullException.ThrowIfNull(nextWorld);

		if (_currentScene.World is not null)
		{
			_worldManager.RemoveWorld(_currentScene.World);
		}

		_worldManager.RegisterWorld(nextWorld);
	}
}
