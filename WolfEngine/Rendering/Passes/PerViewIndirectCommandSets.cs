namespace WolfEngine.Rendering.Passes;

/// <summary>
/// Indirect command sets kept per render view. Encoded draw records bake the addresses of the buffers the CPU
/// fills per view — camera, lighting, environment — so two views cannot share one set: whichever view encoded it,
/// the other would draw with that view's camera. Sets are keyed by the same view slot that selects those buffers,
/// <see cref="GpuDrawResources.ActiveViewIndex"/>, so a set and the buffers it bakes always belong to one view.
/// </summary>
internal sealed class PerViewIndirectCommandSets
{
	private readonly int _setsPerView;
	private readonly List<SharedDrawIndirectCommandSet[]?> _views = new();

	/// <param name="setsPerView">How many sets each view has, such as one per shadow cascade.</param>
	public PerViewIndirectCommandSets(int setsPerView = 1)
	{
		ArgumentOutOfRangeException.ThrowIfLessThan(setsPerView, 1);
		_setsPerView = setsPerView;
	}

	public SharedDrawIndirectCommandSet Get(int viewIndex, int setIndex = 0)
	{
		ArgumentOutOfRangeException.ThrowIfNegative(viewIndex);
		if ((uint)setIndex >= (uint)_setsPerView)
		{
			throw new ArgumentOutOfRangeException(nameof(setIndex), setIndex, $"Each view has {_setsPerView} set(s).");
		}

		while (_views.Count <= viewIndex)
		{
			_views.Add(null);
		}

		var sets = _views[viewIndex];
		if (sets is null)
		{
			sets = new SharedDrawIndirectCommandSet[_setsPerView];
			for (var i = 0; i < sets.Length; i++)
			{
				sets[i] = new SharedDrawIndirectCommandSet();
			}

			_views[viewIndex] = sets;
		}

		return sets[setIndex];
	}

	/// <summary>Removes and returns a view's sets, for the caller to unregister and retire.</summary>
	public IReadOnlyList<SharedDrawIndirectCommandSet> Take(int viewIndex)
	{
		if (viewIndex < 0 || viewIndex >= _views.Count || _views[viewIndex] is not { } sets)
		{
			return Array.Empty<SharedDrawIndirectCommandSet>();
		}

		_views[viewIndex] = null;
		return sets;
	}

	public IEnumerable<SharedDrawIndirectCommandSet> All
	{
		get
		{
			foreach (var sets in _views)
			{
				if (sets is null)
				{
					continue;
				}

				foreach (var set in sets)
				{
					yield return set;
				}
			}
		}
	}
}
