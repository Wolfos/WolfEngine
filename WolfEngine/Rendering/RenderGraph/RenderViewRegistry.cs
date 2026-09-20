using WolfEngine.ECS;
using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering;

/// <summary>
/// Owns the cross-frame state of every live view. One registry is shared by the render graph, which needs a
/// view's output target and render size, and the frame builder, which needs its temporal history — both are
/// the same view's state and must not be two copies.
/// </summary>
internal sealed class RenderViewRegistry
{
	private readonly Dictionary<RenderViewId, RenderViewState> _views = new();

	/// <summary>The state for <paramref name="view"/>, created on first use.</summary>
	public RenderViewState GetOrCreate(RenderViewId view)
	{
		if (view.IsValid == false)
		{
			throw new ArgumentException("View state needs a valid view id.", nameof(view));
		}

		if (_views.TryGetValue(view, out var state) == false)
		{
			state = new RenderViewState(view);
			_views.Add(view, state);
		}

		return state;
	}

	public bool TryGet(RenderViewId view, out RenderViewState state) => _views.TryGetValue(view, out state!);

	public IReadOnlyCollection<RenderViewState> Views => _views.Values;

	/// <summary>Live view ids, lowest slot first.</summary>
	public IReadOnlyList<RenderViewId> ViewIds
	{
		get
		{
			var ids = new List<RenderViewId>(_views.Keys);
			ids.Sort();
			return ids;
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
		if (TryGetViewForWorld(world, out var existing))
		{
			throw new InvalidOperationException(
				$"World {world.Id} already backs {existing}. A world may back at most one view.");
		}

		var state = GetOrCreate(AllocateSlot());
		state.World = world;
		state.Name = string.IsNullOrWhiteSpace(name) ? state.View.ToString() : name;
		state.Output = output;
		return state;
	}

	public bool TryGetViewForWorld(World world, out RenderViewId view)
	{
		foreach (var state in _views.Values)
		{
			if (ReferenceEquals(state.World, world))
			{
				view = state.View;
				return true;
			}
		}

		view = RenderViewId.None;
		return false;
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

	/// <summary>
	/// Retires a view's GPU resources and forgets it, so a later view given the same id starts clean rather
	/// than inheriting history that belongs to a camera and target size that no longer exist.
	/// </summary>
	public bool Release(RenderViewId view, IGfxDevice? device)
	{
		if (_views.TryGetValue(view, out var state) == false)
		{
			return false;
		}

		state.ReleaseAll(device);
		_views.Remove(view);
		return true;
	}
}
