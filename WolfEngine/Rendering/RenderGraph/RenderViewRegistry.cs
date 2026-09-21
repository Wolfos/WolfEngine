using WolfEngine.ECS;
using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering;

/// <summary>
/// Owns the cross-frame state of every live view. One registry is shared by the render graph, which needs a
/// view's output target and render size, and the frame builder, which needs its temporal history — both are
/// the same view's state and must not be two copies.
/// </summary>
/// <remarks>
/// Threading: the table and each view's binding (world, generation, name, output) are guarded by a lock, because
/// the game thread reads bindings when it publishes a snapshot while the render thread reads them every frame.
/// Everything else on a <see cref="RenderViewState"/> is render-thread state; <see cref="RenderGraph"/> runs view
/// creation, destruction and rebinding on the render thread so that state is never touched concurrently.
/// </remarks>
internal sealed class RenderViewRegistry
{
	private readonly object _sync = new();
	private readonly Dictionary<RenderViewId, RenderViewState> _views = new();
	private long _nextBindingGeneration;

	/// <summary>The state for <paramref name="view"/>, created on first use.</summary>
	public RenderViewState GetOrCreate(RenderViewId view)
	{
		if (view.IsValid == false)
		{
			throw new ArgumentException("View state needs a valid view id.", nameof(view));
		}

		lock (_sync)
		{
			if (_views.TryGetValue(view, out var state) == false)
			{
				state = new RenderViewState(view);
				_views.Add(view, state);
			}

			return state;
		}
	}

	public bool TryGet(RenderViewId view, out RenderViewState state)
	{
		lock (_sync)
		{
			return _views.TryGetValue(view, out state!);
		}
	}

	/// <summary>Every live view's state, as a copy safe to enumerate while views are created or destroyed.</summary>
	public IReadOnlyList<RenderViewState> Views
	{
		get
		{
			lock (_sync)
			{
				return new List<RenderViewState>(_views.Values);
			}
		}
	}

	/// <summary>Live view ids, lowest slot first.</summary>
	public IReadOnlyList<RenderViewId> ViewIds
	{
		get
		{
			lock (_sync)
			{
				var ids = new List<RenderViewId>(_views.Keys);
				ids.Sort();
				return ids;
			}
		}
	}

	/// <summary>
	/// Binds a new view to <paramref name="world"/>. A world may back at most one view: the renderer's
	/// transform-change tracking prunes each change once every draw database has consumed it, and a world
	/// gathered by two views' databases would have changes pruned early and lose draws with nothing logged.
	/// </summary>
	public RenderViewState Create(World world, string name, RenderViewOutput output)
	{
		ArgumentNullException.ThrowIfNull(world);
		lock (_sync)
		{
			ThrowIfBound(world, except: RenderViewId.None);
			var state = _views.Values
				.Where(candidate => candidate.World is null)
				.OrderBy(candidate => candidate.View)
				.FirstOrDefault() ?? GetOrCreate(AllocateSlot());
			state.World = world;
			state.BindingGeneration = ++_nextBindingGeneration;
			state.Name = string.IsNullOrWhiteSpace(name) ? state.View.ToString() : name;
			state.Output = output;
			return state;
		}
	}

	/// <summary>
	/// Points an existing view at a different world, such as the editor's scene view when a scene loads or play
	/// mode starts. A new binding generation tells the snapshot to drop the old world's draw records and camera
	/// history, and tells the render thread to drop the view's temporal history, which belongs to the old world.
	/// </summary>
	public bool Rebind(RenderViewId view, World world)
	{
		ArgumentNullException.ThrowIfNull(world);
		lock (_sync)
		{
			if (_views.TryGetValue(view, out var state) == false || state.World is null)
			{
				return false;
			}

			if (ReferenceEquals(state.World, world))
			{
				return true;
			}

			ThrowIfBound(world, except: view);
			state.World = world;
			state.BindingGeneration = ++_nextBindingGeneration;
			return true;
		}
	}

	public bool TryGetViewForWorld(World world, out RenderViewId view)
	{
		lock (_sync)
		{
			foreach (var state in _views.Values)
			{
				if (ReferenceEquals(state.World, world))
				{
					view = state.View;
					return true;
				}
			}
		}

		view = RenderViewId.None;
		return false;
	}

	/// <summary>A view's current world and binding generation, read together so they cannot be torn.</summary>
	public bool TryGetBinding(RenderViewId view, out World world, out long bindingGeneration)
	{
		lock (_sync)
		{
			if (_views.TryGetValue(view, out var state) && state.World is not null)
			{
				world = state.World;
				bindingGeneration = state.BindingGeneration;
				return true;
			}
		}

		world = null!;
		bindingGeneration = 0;
		return false;
	}

	/// <summary>
	/// Retires a view's GPU resources and forgets it, so a later view given the same id starts clean rather
	/// than inheriting history that belongs to a camera and target size that no longer exist. Render thread only.
	/// </summary>
	public bool Release(RenderViewId view, IGfxDevice? device)
	{
		RenderViewState? state;
		lock (_sync)
		{
			if (_views.Remove(view, out state) == false)
			{
				return false;
			}
		}

		state.ReleaseAll(device);
		return true;
	}

	private void ThrowIfBound(World world, RenderViewId except)
	{
		foreach (var state in _views.Values)
		{
			if (state.View != except && ReferenceEquals(state.World, world))
			{
				throw new InvalidOperationException(
					$"World {world.Id} already backs {state.View}. A world may back at most one view.");
			}
		}
	}

	/// <summary>
	/// Lowest free slot. Slots are reused after a view is released, because the UI sentinel block is indexed
	/// by slot and is deliberately small; <see cref="Release"/> clears the state so a reused slot starts empty
	/// rather than inheriting the released view's history.
	/// </summary>
	private RenderViewId AllocateSlot()
	{
		for (var index = 0; index < UI.UiTextureIds.MaxViewports; index++)
		{
			var candidate = RenderViewId.FromIndex(index);
			if (_views.ContainsKey(candidate) == false)
			{
				return candidate;
			}
		}

		throw new InvalidOperationException(
			$"All {UI.UiTextureIds.MaxViewports} view slots are in use.");
	}
}
