using WolfEngine.ECS;

namespace WolfEngine.Gameplay;

/// <summary>Owns one runtime world and the gameplay systems bound to it. Called only between host frames.</summary>
public sealed class GameplayWorldSession(
	IWorldManager manager,
	IGameplayModule gameplay,
	IServiceProvider services,
	Action<World> bindView,
	Action stopAudio)
{
	private readonly List<ISystem> _systems = [];
	public World? World { get; private set; }

	public void Replace(World next)
	{
		ArgumentNullException.ThrowIfNull(next);
		if (ReferenceEquals(next, World)) throw new ArgumentException("Replacement requires a new world.", nameof(next));
		// Verify the render binding before teardown. Loaders prepare the world before calling Replace.
		bindView(next);
		Stop();
		World = next;
		manager.RegisterWorld(next);
		// Modules can keep system references in fields; unload must precede CreateSystems overwriting them.
		foreach (var system in gameplay.CreateSystems(services))
		{
			manager.AddSystem(system, SystemExecutionGroup.Gameplay);
			_systems.Add(system);
		}
		gameplay.OnLoaded(next);
	}

	public void Stop()
	{
		if (World is not { } old) return;
		World = null;
		var failures = new List<Exception>();
		void Clean(Action action)
		{
			try { action(); }
			catch (Exception exception) { failures.Add(exception); }
		}
		Clean(() => gameplay.OnUnloading(old));
		Clean(stopAudio);
		// Removal listeners must run before gameplay systems are detached.
		Clean(() => manager.RemoveWorld(old));
		foreach (var system in _systems) Clean(() => manager.RemoveSystem(system));
		_systems.Clear();
		if (failures.Count != 0) throw new AggregateException("World teardown failed.", failures);
	}
}
