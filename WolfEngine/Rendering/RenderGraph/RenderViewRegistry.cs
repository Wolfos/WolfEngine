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
