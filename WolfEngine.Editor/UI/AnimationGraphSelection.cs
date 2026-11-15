using System.Numerics;
using WolfEngine.Animation;

namespace WolfEngine.Editor.UI;

/// <summary>Ordered selection keeps one stable inspector target while supporting group operations.</summary>
internal sealed class AnimationGraphSelection
{
	private readonly List<Guid> _ordered = new();
	private readonly HashSet<Guid> _selected = new();

	public Guid Primary => _ordered.Count > 0 ? _ordered[0] : Guid.Empty;
	public IReadOnlyList<Guid> Ids => _ordered;
	public int Count => _ordered.Count;

	public bool Contains(Guid id) => _selected.Contains(id);

	public void Set(IEnumerable<Guid> ids)
	{
		// Materialize before clearing; callers can pass the current selection.
		var next = ids.Where(id => id != Guid.Empty).Distinct().ToArray();
		_ordered.Clear();
		_selected.Clear();
		foreach (var id in next)
		{
			_ordered.Add(id);
			_selected.Add(id);
		}
	}

	public void SelectOnly(Guid id)
	{
		Set([id]);
	}

	public void Toggle(Guid id)
	{
		if (_selected.Remove(id))
		{
			_ordered.Remove(id);
		}
		else if (id != Guid.Empty)
		{
			_selected.Add(id);
			_ordered.Add(id);
		}
	}

	public void Clear()
	{
		_ordered.Clear();
		_selected.Clear();
	}

	public void Prune(AnimationGraph graph)
	{
		var available = graph.Nodes.Select(node => node.Id).ToHashSet();
		_ordered.RemoveAll(id => available.Contains(id) == false);
		_selected.IntersectWith(available);
	}

	public void SelectBox(
		AnimationGraph graph,
		IReadOnlyDictionary<Guid, Vector2> positions,
		Vector2 start,
		Vector2 end,
		IEnumerable<Guid> initialSelection)
	{
		var min = Vector2.Min(start, end);
		var max = Vector2.Max(start, end);
		var hits = new List<Guid>();
		foreach (var node in graph.Nodes)
		{
			if (positions.TryGetValue(node.Id, out var position) == false) continue;

			var nodeMax = position + AnimationGraphLayout.NodeSize(node);
			if (min.X <= nodeMax.X && max.X >= position.X && min.Y <= nodeMax.Y && max.Y >= position.Y)
			{
				hits.Add(node.Id);
			}
		}

		Set(initialSelection.Concat(hits));
	}
}
