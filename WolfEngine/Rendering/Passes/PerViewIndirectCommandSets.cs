using WolfEngine.Rendering.Abstraction;

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
	private readonly DrawPassParticipation _participation;
	private readonly ILaneBindingProvider[] _lanesPerSet;
	private readonly List<DrawRecordSet[]?> _views = new();

	/// <param name="records">Creates the sets, so it can keep every one of them current.</param>
	/// <param name="participation">The pass whose lanes the sets draw.</param>
	/// <param name="lanesPerSet">
	/// The lanes of each set index, shared by every view: one entry per set a view has, such as one per shadow cascade.
	/// </param>
	public PerViewIndirectCommandSets(
		IndirectDrawRecords records,
		DrawPassParticipation participation,
		params ILaneBindingProvider[] lanesPerSet)
	{
		ArgumentNullException.ThrowIfNull(records);
		ArgumentNullException.ThrowIfNull(lanesPerSet);
		ArgumentOutOfRangeException.ThrowIfLessThan(lanesPerSet.Length, 1);
		_records = records;
		_participation = participation;
		_lanesPerSet = lanesPerSet;
	}

	public DrawRecordSet Get(int viewIndex, int setIndex = 0)
	{
		ArgumentOutOfRangeException.ThrowIfNegative(viewIndex);
		if ((uint)setIndex >= (uint)_lanesPerSet.Length)
		{
			throw new ArgumentOutOfRangeException(nameof(setIndex), setIndex, $"Each view has {_lanesPerSet.Length} set(s).");
		}

		while (_views.Count <= viewIndex)
		{
			_views.Add(null);
		}

		var sets = _views[viewIndex];
		if (sets is null)
		{
			sets = new DrawRecordSet[_lanesPerSet.Length];
			for (var i = 0; i < sets.Length; i++)
			{
				sets[i] = _records.CreateSet(_participation, _lanesPerSet[i]);
			}

			_views[viewIndex] = sets;
		}

		return sets[setIndex];
	}

	/// <summary>
	/// Releases a destroyed view's sets and retires them on <paramref name="device"/>, so frames still in flight keep
	/// their records. The view's next use starts from fresh sets.
	/// </summary>
	/// <returns>How many sets were released.</returns>
	public int ReleaseView(int viewIndex, IGfxDevice device)
	{
		ArgumentNullException.ThrowIfNull(device);
		if (viewIndex < 0 || viewIndex >= _views.Count || _views[viewIndex] is not { } sets)
		{
			return 0;
		}

		_views[viewIndex] = null;
		foreach (var set in sets)
		{
			_records.Release(set);
			device.Retire(set, $"Draw record set for view {viewIndex}");
		}

		return sets.Length;
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
