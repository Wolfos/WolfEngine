using NSubstitute;
using WolfEngine.ECS;
using WolfEngine.Editor.Projects;
using WolfEngine.Mathematics;

namespace WolfEngine.Editor.Tests;

[TestFixture]
public sealed class EditorPlaySessionTests
{
	[Test]
	public void EnterPlay_CreatesSeparateRuntimeSceneWithoutMutatingAuthoringScene()
	{
		var manager = new WorldManager();
		var workspace = new EditorSceneWorkspace(Substitute.For<IEditorSceneFactory>(), manager);
		var reloadService = new EditorSceneReloadService(new TestTypeResolver());
		var playSession = new EditorPlaySession(workspace, reloadService, manager);
		var authoringScene = CreateAuthoringScene(manager);
		workspace.Initialize(authoringScene);

		Assert.That(playSession.EnterPlay(), Is.True);

		var runtimeScene = playSession.RuntimeScene!;
		var runtimeEntity = FindEntityByName(runtimeScene.World, "Player");
		ref var runtimeComponent = ref runtimeScene.World.GetComponent<TestPlayComponent>(runtimeEntity);
		runtimeComponent.Count = 99;
		runtimeScene.World.CreateEntity("Runtime Spawned");

		var authoringEntity = FindEntityByName(authoringScene.World, "Player");
		Assert.That(authoringScene.World, Is.Not.SameAs(runtimeScene.World));
		Assert.That(authoringScene.World.Tag, Is.EqualTo(WorldTag.Authoring));
		Assert.That(runtimeScene.World.Tag, Is.EqualTo(WorldTag.Game));
		Assert.That(runtimeScene.EntityIds[runtimeEntity], Is.EqualTo(authoringScene.EntityIds[authoringEntity]));
		Assert.That(authoringScene.World.GetComponent<TestPlayComponent>(authoringEntity).Count, Is.EqualTo(1));
		Assert.That(HasEntityNamed(authoringScene.World, "Runtime Spawned"), Is.False);
	}

	[Test]
	public void StopPlay_DiscardsRuntimeChangesAndReturnsToAuthoringScene()
	{
		var manager = new WorldManager();
		var workspace = new EditorSceneWorkspace(Substitute.For<IEditorSceneFactory>(), manager);
		var reloadService = new EditorSceneReloadService(new TestTypeResolver());
		var playSession = new EditorPlaySession(workspace, reloadService, manager);
		var authoringScene = CreateAuthoringScene(manager);
		workspace.Initialize(authoringScene);

		playSession.EnterPlay();
		var runtimeScene = playSession.RuntimeScene!;
		var runtimeEntity = FindEntityByName(runtimeScene.World, "Player");
		runtimeScene.World.GetComponent<TestPlayComponent>(runtimeEntity).Count = 42;
		runtimeScene.World.CreateEntity("Runtime Spawned");

		Assert.That(playSession.Stop(), Is.True);

		var authoringEntity = FindEntityByName(authoringScene.World, "Player");
		Assert.That(playSession.State, Is.EqualTo(EditorPlayState.Edit));
		Assert.That(playSession.RuntimeScene, Is.Null);
		Assert.That(playSession.ActiveScene, Is.SameAs(authoringScene));
		Assert.That(authoringScene.World.GetComponent<TestPlayComponent>(authoringEntity).Count, Is.EqualTo(1));
		Assert.That(HasEntityNamed(authoringScene.World, "Runtime Spawned"), Is.False);
	}

	[TestCase(EditorPlayState.Playing)]
	[TestCase(EditorPlayState.Paused)]
	public void Restart_RecreatesFreshRuntimeSceneAndRestoresRequestedPlayState(EditorPlayState targetState)
	{
		var manager = new WorldManager();
		var workspace = new EditorSceneWorkspace(Substitute.For<IEditorSceneFactory>(), manager);
		var reloadService = new EditorSceneReloadService(new TestTypeResolver());
		var playSession = new EditorPlaySession(workspace, reloadService, manager);
		var authoringScene = CreateAuthoringScene(manager);
		workspace.Initialize(authoringScene);

		playSession.EnterPlay();
		var firstRuntimeScene = playSession.RuntimeScene!;
		var firstRuntimeEntity = FindEntityByName(firstRuntimeScene.World, "Player");
		firstRuntimeScene.World.GetComponent<TestPlayComponent>(firstRuntimeEntity).Count = 7;
		firstRuntimeScene.World.CreateEntity("Runtime Spawned");

		playSession.Restart(targetState);

		var restartedScene = playSession.RuntimeScene!;
		var restartedEntity = FindEntityByName(restartedScene.World, "Player");
		Assert.That(playSession.State, Is.EqualTo(targetState));
		Assert.That(restartedScene.World, Is.Not.SameAs(firstRuntimeScene.World));
		Assert.That(restartedScene.World.GetComponent<TestPlayComponent>(restartedEntity).Count, Is.EqualTo(1));
		Assert.That(HasEntityNamed(restartedScene.World, "Runtime Spawned"), Is.False);
		Assert.That(authoringScene.World.GetComponent<TestPlayComponent>(FindEntityByName(authoringScene.World, "Player")).Count, Is.EqualTo(1));
	}

	private static EditorScene CreateAuthoringScene(WorldManager manager)
	{
		var world = manager.CreateWorld(WorldTag.Authoring);
		var scene = new EditorScene
		{
			World = world,
			EntityIcons = new Dictionary<Entity, string>(),
			EntityIds = new Dictionary<Entity, Guid>(),
			EntityCellKeys = new Dictionary<Entity, SceneCellKey>(),
			SpatialCells = new Dictionary<Int2, Cell>(),
			GlobalCell = new Cell()
		};
		var entity = world.CreateEntity("Player");
		world.AddComponent(entity, new TestPlayComponent { Count = 1 });
		scene.EntityIds[entity] = Guid.NewGuid();
		return scene;
	}

	private static Entity FindEntityByName(World world, string name)
	{
		var entities = new List<Entity>();
		world.GetAllEntities(entities);
		return entities.Single(entity =>
			world.HasComponent<NameComponent>(entity) &&
			string.Equals(world.GetComponent<NameComponent>(entity).Name, name, StringComparison.Ordinal));
	}

	private static bool HasEntityNamed(World world, string name)
	{
		var entities = new List<Entity>();
		world.GetAllEntities(entities);
		return entities.Any(entity =>
			world.HasComponent<NameComponent>(entity) &&
			string.Equals(world.GetComponent<NameComponent>(entity).Name, name, StringComparison.Ordinal));
	}

	private struct TestPlayComponent : IEntityComponent
	{
		public int Count;
	}

	private sealed class TestTypeResolver : IProjectTypeResolver
	{
		public string GetTypeName(Type type) => type.AssemblyQualifiedName ?? type.FullName ?? type.Name;

		public string GetStableTypeId(Type type) => type.AssemblyQualifiedName ?? type.FullName ?? type.Name;

		public bool TryResolveType(string typeName, out Type type)
		{
			type = Type.GetType(typeName, throwOnError: false)!;
			return type is not null;
		}

		public bool TryResolveStableTypeId(string stableTypeId, out Type type)
		{
			type = Type.GetType(stableTypeId, throwOnError: false)!;
			return type is not null;
		}
	}
}
