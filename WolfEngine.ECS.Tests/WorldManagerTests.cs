using NUnit.Framework;

namespace WolfEngine.ECS.Tests;

[TestFixture]
public sealed class WorldManagerTests
{
	[Test]
	public void Update_AllowsMultipleSystemsWithSameTagAndPreservesRegistrationOrder()
	{
		var manager = new WorldManager();
		manager.CreateWorld(WorldTag.Game);
		var calls = new List<string>();
		var first = new RecordingUpdateSystem(WorldTag.Game, calls, "first");
		var second = new RecordingUpdateSystem(WorldTag.Game, calls, "second");

		manager.AddSystem(first);
		manager.AddSystem(second);

		manager.Update(0.016f, WorldTag.Game, SystemExecutionGroup.Shared);

		Assert.That(calls, Is.EqualTo(new[] { "first", "second" }));
	}

	[Test]
	public void Update_GroupMaskRunsSharedSystemsWithoutGameplaySystems()
	{
		var manager = new WorldManager();
		manager.CreateWorld(WorldTag.Game);
		var shared = new CountingUpdateSystem(WorldTag.Game);
		var gameplay = new CountingUpdateSystem(WorldTag.Game);

		manager.AddSystem(shared, SystemExecutionGroup.Shared);
		manager.AddSystem(gameplay, SystemExecutionGroup.Gameplay);

		manager.Update(0.016f, WorldTag.Game, SystemExecutionGroup.Shared);

		Assert.That(shared.UpdateCount, Is.EqualTo(1));
		Assert.That(gameplay.UpdateCount, Is.EqualTo(0));
	}

	[Test]
	public void OnPreRender_GroupMaskRunsSharedSystemsWithoutGameplaySystems()
	{
		var manager = new WorldManager();
		manager.CreateWorld(WorldTag.Game);
		var shared = new CountingPreRenderSystem(WorldTag.Game);
		var gameplay = new CountingPreRenderSystem(WorldTag.Game);

		manager.AddSystem(shared, SystemExecutionGroup.Shared);
		manager.AddSystem(gameplay, SystemExecutionGroup.Gameplay);

		manager.OnPreRender(0.016f, WorldTag.Game, SystemExecutionGroup.Shared);

		Assert.That(shared.PreRenderCount, Is.EqualTo(1));
		Assert.That(gameplay.PreRenderCount, Is.EqualTo(0));
	}

	[Test]
	public void PhysicsUpdate_AllowsMultipleSystemsWithSameTagAndPreservesRegistrationOrder()
	{
		var manager = new WorldManager();
		manager.CreateWorld(WorldTag.Game);
		var calls = new List<string>();
		var first = new RecordingPhysicsSystem(WorldTag.Game, calls, "first");
		var second = new RecordingPhysicsSystem(WorldTag.Game, calls, "second");

		manager.AddSystem(first);
		manager.AddSystem(second);

		manager.PhysicsUpdate(1.0f / 60.0f, WorldTag.Game, SystemExecutionGroup.Shared);

		Assert.That(calls, Is.EqualTo(new[] { "first", "second" }));
	}

	[Test]
	public void PhysicsUpdate_GroupMaskRunsSharedSystemsWithoutGameplaySystems()
	{
		var manager = new WorldManager();
		manager.CreateWorld(WorldTag.Game);
		var shared = new CountingPhysicsSystem(WorldTag.Game);
		var gameplay = new CountingPhysicsSystem(WorldTag.Game);

		manager.AddSystem(shared, SystemExecutionGroup.Shared);
		manager.AddSystem(gameplay, SystemExecutionGroup.Gameplay);

		manager.PhysicsUpdate(1.0f / 60.0f, WorldTag.Game, SystemExecutionGroup.Shared);

		Assert.That(shared.UpdateCount, Is.EqualTo(1));
		Assert.That(gameplay.UpdateCount, Is.EqualTo(0));
	}

	[Test]
	public void RemoveSystem_StopsFutureExecution()
	{
		var manager = new WorldManager();
		manager.CreateWorld(WorldTag.Game);
		var system = new CountingUpdateSystem(WorldTag.Game);

		manager.AddSystem(system);
		manager.Update(0.016f, WorldTag.Game, SystemExecutionGroup.Shared);
		Assert.That(manager.RemoveSystem(system), Is.True);

		manager.Update(0.016f, WorldTag.Game, SystemExecutionGroup.Shared);

		Assert.That(system.UpdateCount, Is.EqualTo(1));
	}

	[Test]
	public void RemoveWorld_NotifiesWorldRemovedListeners()
	{
		var manager = new WorldManager();
		var world = manager.CreateWorld(WorldTag.Game);
		var listener = new WorldRemovedListenerSystem();
		manager.AddSystem(listener);

		Assert.That(manager.RemoveWorld(world), Is.True);
		Assert.That(listener.RemovedWorlds, Has.Count.EqualTo(1));
		Assert.That(listener.RemovedWorlds[0], Is.SameAs(world));
	}

	[Test]
	public void Update_GameplayExceptionHandlerAllowsLaterSystemsToRun()
	{
		var manager = new WorldManager();
		manager.CreateWorld(WorldTag.Game);
		var exception = new InvalidOperationException("Gameplay failed.");
		var throwing = new ThrowingUpdateSystem(exception);
		var following = new CountingUpdateSystem(WorldTag.Game);
		ISystem? reportedSystem = null;
		Exception? reportedException = null;

		manager.SetExceptionHandler(SystemExecutionGroup.Gameplay, (system, caughtException) =>
		{
			reportedSystem = system;
			reportedException = caughtException;
		});
		manager.AddSystem(throwing, SystemExecutionGroup.Gameplay);
		manager.AddSystem(following, SystemExecutionGroup.Gameplay);
		manager.Update(0.016f, WorldTag.Game);

		Assert.That(reportedSystem, Is.SameAs(throwing));
		Assert.That(reportedException, Is.SameAs(exception));
		Assert.That(following.UpdateCount, Is.EqualTo(1));
	}

	[Test]
	public void Update_SharedExceptionIsNotHandledByGameplayBoundary()
	{
		var manager = new WorldManager();
		manager.CreateWorld(WorldTag.Game);
		var exception = new InvalidOperationException("Engine failed.");
		var throwing = new ThrowingUpdateSystem(exception);
		var handlerCalled = false;

		manager.SetExceptionHandler(SystemExecutionGroup.Gameplay, (_, _) => handlerCalled = true);
		manager.AddSystem(throwing, SystemExecutionGroup.Shared);

		var thrown = Assert.Throws<InvalidOperationException>(() => manager.Update(0.016f, WorldTag.Game));

		Assert.That(thrown, Is.SameAs(exception));
		Assert.That(handlerCalled, Is.False);
	}

	[Test]
	public void GameplayExceptionBoundaryCoversAllSystemCallbacks()
	{
		var manager = new WorldManager();
		var world = manager.CreateWorld(WorldTag.Game);
		var throwing = new ThrowingAllCallbacksSystem();
		var reportedExceptions = new List<Exception>();
		void HandleException(ISystem _, Exception exception) => reportedExceptions.Add(exception);

		manager.SetExceptionHandler(SystemExecutionGroup.Gameplay, HandleException);
		manager.AddSystem(throwing, SystemExecutionGroup.Gameplay);
		manager.PhysicsUpdate(1.0f / 60.0f, WorldTag.Game);
		manager.OnPreRender(0.016f, WorldTag.Game);
		manager.OnDrawGizmos(WorldTag.Game);
		manager.RemoveWorld(world);

		Assert.That(reportedExceptions, Has.Count.EqualTo(4));
	}

	private sealed class RecordingUpdateSystem : IUpdate
	{
		private readonly WorldTag _tag;
		private readonly List<string> _calls;
		private readonly string _label;

		public RecordingUpdateSystem(WorldTag tag, List<string> calls, string label)
		{
			_tag = tag;
			_calls = calls;
			_label = label;
		}

		public void Update(float deltaTime, World world)
		{
			_calls.Add(_label);
		}

		public WorldTag GetTag() => _tag;
	}

	private sealed class CountingUpdateSystem : IUpdate
	{
		private readonly WorldTag _tag;

		public CountingUpdateSystem(WorldTag tag)
		{
			_tag = tag;
		}

		public int UpdateCount { get; private set; }

		public void Update(float deltaTime, World world)
		{
			UpdateCount++;
		}

		public WorldTag GetTag() => _tag;
	}

	private sealed class ThrowingUpdateSystem : IUpdate
	{
		private readonly Exception _exception;

		public ThrowingUpdateSystem(Exception exception)
		{
			_exception = exception;
		}

		public void Update(float deltaTime, World world) => throw _exception;

		public WorldTag GetTag() => WorldTag.Game;
	}

	private sealed class ThrowingAllCallbacksSystem : IPhysicsUpdate, IPreRender, IOnDrawGizmos, IWorldRemovedListener
	{
		public void PhysicsUpdate(float fixedDeltaTime, World world) => throw new InvalidOperationException();

		public void PreRender(float deltaTime, World world) => throw new InvalidOperationException();

		public void OnDrawGizmos(World world) => throw new InvalidOperationException();

		public void OnWorldRemoved(World world) => throw new InvalidOperationException();

		public WorldTag GetTag() => WorldTag.Game;
	}

	private sealed class CountingPreRenderSystem : IPreRender
	{
		private readonly WorldTag _tag;

		public CountingPreRenderSystem(WorldTag tag)
		{
			_tag = tag;
		}

		public int PreRenderCount { get; private set; }

		public void PreRender(float deltaTime, World world)
		{
			PreRenderCount++;
		}

		public WorldTag GetTag() => _tag;
	}

	private sealed class RecordingPhysicsSystem : IPhysicsUpdate
	{
		private readonly WorldTag _tag;
		private readonly List<string> _calls;
		private readonly string _label;

		public RecordingPhysicsSystem(WorldTag tag, List<string> calls, string label)
		{
			_tag = tag;
			_calls = calls;
			_label = label;
		}

		public void PhysicsUpdate(float fixedDeltaTime, World world)
		{
			_calls.Add(_label);
		}

		public WorldTag GetTag() => _tag;
	}

	private sealed class CountingPhysicsSystem : IPhysicsUpdate
	{
		private readonly WorldTag _tag;

		public CountingPhysicsSystem(WorldTag tag)
		{
			_tag = tag;
		}

		public int UpdateCount { get; private set; }

		public void PhysicsUpdate(float fixedDeltaTime, World world)
		{
			UpdateCount++;
		}

		public WorldTag GetTag() => _tag;
	}

	private sealed class WorldRemovedListenerSystem : IWorldRemovedListener
	{
		public List<World> RemovedWorlds { get; } = new();

		public void OnWorldRemoved(World world)
		{
			RemovedWorlds.Add(world);
		}
	}
}
