using WolfEngine.Editor.Projects;
using WolfEngine.ECS;
using WolfEngine.Rendering.Passes;

namespace WolfEngine.Editor;

public interface IEditorSceneWorkspace
{
	EditorScene CurrentScene { get; }

	/// <summary>Render settings for a scene whose WorldSettings names no render config.</summary>
	RenderConfig DefaultRenderConfig { get; }

	void Initialize(EditorScene scene);
	void ReplaceCurrentScene(EditorScene scene);
	void ResetToNewScene();
	void SaveCurrentScene();
	EditorScene LoadSceneAsset(Guid assetId);
	void LoadScene(Guid assetId);
}

public sealed class EditorSceneWorkspace : IEditorSceneWorkspace
{
	private readonly IEditorSceneFactory _sceneFactory;
	private readonly ITerrainAssetPersistenceService _terrainAssetPersistenceService;
	private readonly IWorldManager _worldManager;
	private EditorScene _currentScene = new();

	public EditorSceneWorkspace(IEditorSceneFactory sceneFactory, IWorldManager worldManager)
		: this(sceneFactory, NoOpTerrainAssetPersistenceService.Instance, worldManager)
	{
	}

	public EditorSceneWorkspace(
		IEditorSceneFactory sceneFactory,
		ITerrainAssetPersistenceService terrainAssetPersistenceService,
		IWorldManager worldManager)
	{
		_sceneFactory = sceneFactory ?? throw new ArgumentNullException(nameof(sceneFactory));
		_terrainAssetPersistenceService = terrainAssetPersistenceService ?? throw new ArgumentNullException(nameof(terrainAssetPersistenceService));
		_worldManager = worldManager ?? throw new ArgumentNullException(nameof(worldManager));
	}

	public EditorScene CurrentScene => _currentScene;

	public RenderConfig DefaultRenderConfig { get; } = new();

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
		_terrainAssetPersistenceService.SaveDirtyTerrainAssets();
		_sceneFactory.Save(_currentScene);
	}

	public void LoadScene(Guid assetId)
	{
		ReplaceCurrentScene(LoadSceneAsset(assetId));
	}

	public EditorScene LoadSceneAsset(Guid assetId)
	{
		return _sceneFactory.Load(assetId);
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

	private sealed class NoOpTerrainAssetPersistenceService : ITerrainAssetPersistenceService
	{
		public static readonly NoOpTerrainAssetPersistenceService Instance = new();

		public void RecordPendingTerrainAssetState(IReadOnlyList<TerrainAssetSnapshot> snapshots)
		{
		}

		public void ApplyTerrainAssetStates(IReadOnlyList<TerrainAssetSnapshot> snapshots)
		{
		}

		public void SaveDirtyTerrainAssets()
		{
		}
	}
}
