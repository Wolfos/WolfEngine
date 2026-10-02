using System.Collections.Concurrent;
using WolfEngine.AssetPipeline;
using WolfEngine.ECS;

namespace WolfEngine.Gameplay;

/// <summary>Asset-reference type for a scene. Scenes are loaded by the host, not resolved as asset instances.</summary>
[RuntimeAsset(AssetType.Scene, typeof(SceneAsset), typeof(SceneAssetResolver))]
public sealed class SceneAsset { }

public sealed class SceneAssetResolver : IRuntimeAssetResolver
{
	public object Resolve(RuntimeAssetResolveContext context) => new SceneAsset();
}

public interface ISceneLoadService
{
	/// <summary>
	/// Requests replacement of the caller's active world at the next frame boundary.
	/// Await asynchronously; blocking on this task from a gameplay callback would block the host.
	/// </summary>
	Task LoadAsync(World world, AssetRef<SceneAsset> scene);
}

/// <summary>Shared by the standalone host and editor Play mode. Requests belong to their originating world.</summary>
public sealed class SceneLoadRequests : ISceneLoadService
{
	private readonly ConcurrentQueue<SceneLoadRequest> _pending = new();

	public Task LoadAsync(World world, AssetRef<SceneAsset> scene)
	{
		ArgumentNullException.ThrowIfNull(world);
		if (!scene.IsValid) throw new ArgumentException("Scene reference cannot be empty.", nameof(scene));
		if (world.Tag != WorldTag.Game) throw new ArgumentException("Scene loads require a runtime world.", nameof(world));
		var request = new SceneLoadRequest(world, scene.NodeId);
		_pending.Enqueue(request);
		return request.Completion;
	}

	public bool TryTake(World? activeWorld, out SceneLoadRequest request)
	{
		while (_pending.TryDequeue(out request!))
		{
			if (ReferenceEquals(request.World, activeWorld)) return true;
			request.Cancel();
		}
		request = null!;
		return false;
	}

	public void CancelPending()
	{
		while (_pending.TryDequeue(out var request)) request.Cancel();
	}
}

public sealed class SceneLoadRequest
{
	private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
	internal SceneLoadRequest(World world, Guid sceneId) { World = world; SceneId = sceneId; }
	public World World { get; }
	public Guid SceneId { get; }
	public Task Completion => _completion.Task;
	public void Complete() => _completion.TrySetResult();
	public void Fail(Exception exception) => _completion.TrySetException(exception);
	internal void Cancel() => _completion.TrySetCanceled();
}
