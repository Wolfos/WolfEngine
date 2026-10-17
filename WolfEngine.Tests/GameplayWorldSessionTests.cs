using Microsoft.Extensions.DependencyInjection;
using WolfEngine.AssetPipeline;
using WolfEngine.ECS;
using WolfEngine.Gameplay;

namespace WolfEngine.Tests;

[TestFixture]
public sealed class GameplayWorldSessionTests
{
	[Test]
	public void RepeatedReplacement_UnloadsOldWorldAndRecreatesSystems()
	{
		var manager = new WorldManager();
		var events = new List<string>();
		var module = new Module(events);
		using var services = new ServiceCollection().BuildServiceProvider();
		World? rendered = null;
		var session = new GameplayWorldSession(manager, module, services,
			world => rendered = world, () => events.Add("audio"));
		var first = new World(WorldTag.Game);
		session.Replace(first);
		manager.Update(0, WorldTag.Game);
		events.Clear();
		var second = new World(WorldTag.Game);
		session.Replace(second);
		Assert.That(events, Is.EqualTo(new[] { $"unload:{first.Id}", "audio", $"remove:{first.Id}", $"load:{second.Id}" }));
		Assert.That(module.UnloadedGenerations, Is.EqualTo(new[] { 1 }), "OnUnloading must see the outgoing systems before CreateSystems replaces module fields");
		Assert.That(rendered, Is.SameAs(second));
		Assert.That(manager.RemoveWorld(first), Is.False);
		events.Clear();
		manager.Update(0, WorldTag.Game);
		Assert.That(events, Is.EqualTo(new[] { $"update:{second.Id}" }));
		session.Replace(new World(WorldTag.Game));
		session.Stop();
		session.Stop();
		Assert.That(module.Created, Is.EqualTo(3));
		Assert.That(session.World, Is.Null);
	}

	[Test]
	public void TeardownFailure_StillStopsAudioAndNotifiesAllRemovalListeners()
	{
		var manager = new WorldManager();
		var events = new List<string>();
		var module = new Module(events) { FailUnload = true };
		manager.AddSystem(new FailingListener());
		using var services = new ServiceCollection().BuildServiceProvider();
		var session = new GameplayWorldSession(manager, module, services, _ => { }, () => events.Add("audio"));
		var world = new World(WorldTag.Game);
		session.Replace(world);
		Assert.Throws<AggregateException>(session.Stop);
		Assert.That(events, Does.Contain("audio"));
		Assert.That(events, Does.Contain($"remove:{world.Id}"));
		Assert.That(manager.RemoveWorld(world), Is.False);
		events.Clear();
		manager.RegisterWorld(new World(WorldTag.Game));
		manager.Update(0, WorldTag.Game);
		Assert.That(events, Is.Empty, "old gameplay systems have been detached even when cleanup throws");
	}

	[Test]
	public void RenderBindingFailure_PreservesCurrentWorldAndView()
	{
		var manager = new WorldManager();
		var module = new Module([]);
		using var services = new ServiceCollection().BuildServiceProvider();
		World? rendered = null;
		var failBinding = false;
		var session = new GameplayWorldSession(manager, module, services, world =>
		{
			if (failBinding) throw new InvalidOperationException("view binding failed");
			rendered = world;
		}, () => { });
		var first = new World(WorldTag.Game);
		session.Replace(first);
		failBinding = true;
		Assert.Throws<InvalidOperationException>(() => session.Replace(new World(WorldTag.Game)));
		Assert.That(session.World, Is.SameAs(first));
		Assert.That(rendered, Is.SameAs(first));
		session.Stop();
	}

	[Test]
	public async Task Requests_AreDeferredAndStaleWorldRequestsAreCanceled()
	{
		var requests = new SceneLoadRequests();
		var first = new World(WorldTag.Game);
		var second = new World(WorldTag.Game);
		var scene = new AssetRef<SceneAsset> { NodeId = Guid.NewGuid() };
		var stale = requests.LoadAsync(first, scene);
		var current = requests.LoadAsync(second, scene);
		Assert.That(current.IsCompleted, Is.False);
		Assert.That(requests.TryTake(second, out var request), Is.True);
		Assert.That(stale.IsCanceled, Is.True);
		Assert.That(request.SceneId, Is.EqualTo(scene.NodeId));
		request.Complete();
		await current;
		var pending = requests.LoadAsync(second, scene);
		requests.CancelPending();
		Assert.That(pending.IsCanceled, Is.True);
	}

	[Test]
	public void Requests_RejectInvalidReferenceAndAuthoringWorld()
	{
		var requests = new SceneLoadRequests();
		Assert.Throws<ArgumentException>(() => requests.LoadAsync(new World(WorldTag.Game), default));
		Assert.Throws<ArgumentException>(() => requests.LoadAsync(new World(WorldTag.Authoring), new() { NodeId = Guid.NewGuid() }));
	}

	private sealed class Module(List<string> events) : IGameplayModule
	{
		public int Created;
		public bool FailUnload;
		public List<int> UnloadedGenerations { get; } = [];
		public IEnumerable<ISystem> CreateSystems(IServiceProvider services)
		{
			Created++;
			return [new System(events)];
		}
		public void OnLoaded(World world) => events.Add($"load:{world.Id}");
		public void OnUnloading(World world)
		{
			events.Add($"unload:{world.Id}");
			UnloadedGenerations.Add(Created);
			if (FailUnload) throw new InvalidOperationException("cleanup failed");
		}
		public void Update(float deltaTime, World world) { }
		public void PhysicsUpdate(float deltaTime, World world) { }
	}
	private sealed class System(List<string> events) : IUpdate, IWorldRemovedListener
	{
		public WorldTag GetTag() => WorldTag.Game;
		public void Update(float deltaTime, World world) => events.Add($"update:{world.Id}");
		public void OnWorldRemoved(World world) => events.Add($"remove:{world.Id}");
	}
	private sealed class FailingListener : IWorldRemovedListener
	{
		public void OnWorldRemoved(World world) => throw new InvalidOperationException("listener failed");
	}
}
