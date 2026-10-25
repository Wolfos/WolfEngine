using System.Collections.Concurrent;
using WolfEngine.ECS;
using WolfEngine.Logging;

namespace WolfEngine.Rendering.Features;

/// <summary>
/// Game-thread registry of render features. A feature runs in every view that renders the world it is registered
/// to, until it is unregistered or that world's gameplay is torn down.
/// </summary>
public interface IRenderFeatureRegistry
{
	void Register(World world, RenderFeature feature);

	/// <summary>Stops the feature and releases its GPU resources once no frame on the GPU uses them.</summary>
	bool Unregister(RenderFeature feature);

	/// <summary>Unregisters every feature of <paramref name="world"/>. The engine calls this when gameplay unloads.</summary>
	void UnregisterWorld(World world);
}

public sealed class RenderFeatureRegistry : IRenderFeatureRegistry
{
	private readonly Dictionary<World, List<RenderFeature>> _featuresByWorld = new(ReferenceEqualityComparer.Instance);
	private readonly HashSet<RenderFeature> _registered = new(ReferenceEqualityComparer.Instance);
	private readonly ConcurrentQueue<RenderFeature> _retired = new();
	private readonly ILogService? _log;

	public RenderFeatureRegistry(ILogService? log = null)
	{
		_log = log;
	}

	public void Register(World world, RenderFeature feature)
	{
		ArgumentNullException.ThrowIfNull(world);
		ArgumentNullException.ThrowIfNull(feature);
		if (feature.IsRetired)
		{
			throw new InvalidOperationException("An unregistered render feature cannot be registered again. Create a new instance.");
		}

		if (_registered.Add(feature) == false)
		{
			throw new InvalidOperationException("The render feature is already registered.");
		}

		if (_featuresByWorld.TryGetValue(world, out var features) == false)
		{
			_featuresByWorld[world] = features = [];
		}

		features.Add(feature);
	}

	public bool Unregister(RenderFeature feature)
	{
		ArgumentNullException.ThrowIfNull(feature);
		if (_registered.Remove(feature) == false)
		{
			return false;
		}

		foreach (var (world, features) in _featuresByWorld)
		{
			if (features.Remove(feature))
			{
				if (features.Count == 0)
				{
					_featuresByWorld.Remove(world);
				}

				break;
			}
		}

		Retire(feature);
		return true;
	}

	public void UnregisterWorld(World world)
	{
		ArgumentNullException.ThrowIfNull(world);
		if (_featuresByWorld.Remove(world, out var features) == false)
		{
			return;
		}

		foreach (var feature in features)
		{
			_registered.Remove(feature);
			Retire(feature);
		}
	}

	/// <summary>Game thread: the features registered to <paramref name="world"/>, in registration order.</summary>
	internal IReadOnlyList<RenderFeature> GetFeatures(World world) =>
		_featuresByWorld.TryGetValue(world, out var features) ? features : [];

	/// <summary>Render thread: takes the next feature whose GPU resources can be released.</summary>
	internal bool TryTakeRetired(out RenderFeature feature) => _retired.TryDequeue(out feature!);

	/// <summary>
	/// Either thread: disables a feature whose own code threw and reports it once. It stays registered, so its
	/// resources are released as usual when it is unregistered.
	/// </summary>
	internal void Fault(RenderFeature feature, Exception exception)
	{
		if (feature.TryMarkFaulted() == false)
		{
			return;
		}

		var message = $"Render feature '{feature.GetType().Name}' threw and was disabled.";
		if (_log is null)
		{
			Console.Error.WriteLine($"{message} {exception}");
		}
		else
		{
			_log.Error(message, exception);
		}
	}

	private void Retire(RenderFeature feature)
	{
		feature.MarkRetired();
		_retired.Enqueue(feature);
	}
}
