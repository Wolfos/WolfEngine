namespace WolfEngine.Rendering.Passes;

/// <summary>
/// Indirect command sets kept per render view. On Metal, encoded draw records bake the addresses of the buffers the CPU
/// fills per view — camera, lighting, environment — so two views cannot share one set: whichever view encoded it,
/// the other would draw with that view's camera. Sets are keyed by the same view slot that selects those buffers,
/// <see cref="GpuDrawResources.ActiveViewIndex"/>, so a set and the buffers it bakes always belong to one view.
/// </summary>
internal sealed class PerViewIndirectCommandSets
{
	private readonly IndirectDrawRecords _records;
	private readonly int _setsPerView;
	private readonly List<DrawRecordSet[]?> _views = new();

	/// <param name="records">Creates the sets, so it can keep every one of them current.</param>
	/// <param name="setsPerView">How many sets each view has, such as one per shadow cascade.</param>
	public PerViewIndirectCommandSets(IndirectDrawRecords records, int setsPerView = 1)
	{
		ArgumentNullException.ThrowIfNull(records);
		ArgumentOutOfRangeException.ThrowIfLessThan(setsPerView, 1);
		_records = records;
		_setsPerView = setsPerView;
	}

	public DrawRecordSet Get(int viewIndex, int setIndex = 0)
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
			sets = new DrawRecordSet[_setsPerView];
			for (var i = 0; i < sets.Length; i++)
			{
				sets[i] = _records.CreateSet();
			}

			_views[viewIndex] = sets;
		}

		return sets[setIndex];
	}

	/// <summary>Removes and returns a view's sets, for the caller to unregister and retire.</summary>
	public IReadOnlyList<DrawRecordSet> Take(int viewIndex)
	{
		if (viewIndex < 0 || viewIndex >= _views.Count || _views[viewIndex] is not { } sets)
		{
			return Array.Empty<DrawRecordSet>();
		}

		_views[viewIndex] = null;
		return sets;
	}

	public IEnumerable<DrawRecordSet> All
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
