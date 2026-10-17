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

	[TestCase(EditorPlayState.Playing)]
	[TestCase(EditorPlayState.Paused)]
	public void SceneReplacement_PreservesAuthoringSceneAndPlayState(EditorPlayState state)
	{
		var manager = new WorldManager();
		var factory = Substitute.For<IEditorSceneFactory>();
		var workspace = new EditorSceneWorkspace(factory, manager);
		var reload = new EditorSceneReloadService(new TestTypeResolver());
		var play = new EditorPlaySession(workspace, reload, manager);
		var authoring = CreateAuthoringScene(manager);
		workspace.Initialize(authoring);
		play.EnterPlay();
		if (state == EditorPlayState.Paused) play.Pause();
		var oldWorld = play.RuntimeScene!.World;
		var assetId = Guid.NewGuid();
		var source = CreateAuthoringScene(new WorldManager());
		factory.Load(assetId).Returns(source);
		var next = play.PrepareSceneLoad(assetId);
		Assert.That(play.RuntimeScene!.World, Is.SameAs(oldWorld), "preparation does not replace the running world");
		World? unloading = null;
		play.RuntimeSceneUnloading += world =>
		{
			unloading = world;
			Assert.That(play.RuntimeScene!.World, Is.SameAs(world), "unloading fires while the old scene is still active");
		};
		play.ReplaceRuntimeScene(next);
		Assert.That(unloading, Is.SameAs(oldWorld));
		Assert.That(play.State, Is.EqualTo(state));
		Assert.That(next.World.Tag, Is.EqualTo(WorldTag.Game));
		Assert.That(next.World, Is.Not.SameAs(source.World));
		Assert.That(manager.RemoveWorld(oldWorld), Is.False);
		Assert.That(workspace.CurrentScene, Is.SameAs(authoring));
		play.Stop();
		Assert.That(play.ActiveScene, Is.SameAs(authoring));
		Assert.That(manager.RemoveWorld(next.World), Is.False);
	}

	[Test]
	public void FailedScenePreparation_PreservesRuntimeScene()
	{
		var manager = new WorldManager();
		var factory = Substitute.For<IEditorSceneFactory>();
		var workspace = new EditorSceneWorkspace(factory, manager);
		workspace.Initialize(CreateAuthoringScene(manager));
		var play = new EditorPlaySession(workspace, new EditorSceneReloadService(new TestTypeResolver()), manager);
		play.EnterPlay();
		var current = play.RuntimeScene;
		factory.Load(Arg.Any<Guid>()).Returns(_ => throw new InvalidOperationException("missing scene"));
		Assert.Throws<InvalidOperationException>(() => play.PrepareSceneLoad(Guid.NewGuid()));
		Assert.That(play.RuntimeScene, Is.SameAs(current));
		play.Stop();
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
