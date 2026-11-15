using WolfEngine.ECS;

namespace WolfEngine.Gameplay;

/// <summary>Lets gameplay close the game, such as from a main menu or pause menu Quit button.</summary>
public interface IApplicationQuitService
{
	/// <summary>
	/// Whether the platform lets a game close itself. iOS does not: apps there are closed by the user, so a game should
	/// hide its Quit button when this is false.
	/// </summary>
	bool IsQuitSupported { get; }

	/// <summary>
	/// Requests that the game close at the next frame boundary, after the caller's frame finishes. A built game unloads
	/// the running scene, stops audio, releases GPU resources and exits. In editor Play mode, Play mode stops instead of
	/// the editor closing.
	/// </summary>
	/// <remarks>
	/// The request belongs to <paramref name="world"/>: if that world is no longer running by the frame boundary, such
	/// as after a scene load replaced it, the request is dropped. Safe to call from any thread.
	/// </remarks>
	/// <exception cref="NotSupportedException">The platform does not let a game close itself; see <see cref="IsQuitSupported"/>.</exception>
	void Quit(World world);
}

/// <summary>Shared by the standalone host and editor Play mode, which each take the request once per frame.</summary>
public sealed class ApplicationQuitRequests : IApplicationQuitService
{
	private World? _requestedBy;

	public ApplicationQuitRequests() : this(isQuitSupported: true)
	{
	}

	public ApplicationQuitRequests(bool isQuitSupported)
	{
		IsQuitSupported = isQuitSupported;
	}

	public bool IsQuitSupported { get; }

	public void Quit(World world)
	{
		ArgumentNullException.ThrowIfNull(world);

		if (world.Tag != WorldTag.Game)
		{
			throw new ArgumentException("Quitting requires a runtime world.", nameof(world));
		}

		if (IsQuitSupported == false)
		{
			throw new NotSupportedException("This platform does not let a game close itself.");
		}

		Volatile.Write(ref _requestedBy, world);
	}

	/// <summary>
	/// Host, at a frame boundary: takes the pending request, returning true when it came from
	/// <paramref name="activeWorld"/>. A request from any other world is stale and is dropped.
	/// </summary>
	public bool TryTake(World? activeWorld)
	{
		var requestedBy = Interlocked.Exchange(ref _requestedBy, null);

		return requestedBy is not null && ReferenceEquals(requestedBy, activeWorld);
	}
}
