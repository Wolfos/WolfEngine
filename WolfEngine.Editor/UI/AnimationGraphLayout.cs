using System.Numerics;
using WolfEngine.Animation;

namespace WolfEngine.Editor.UI;

internal readonly record struct GraphRect(Vector2 Min, Vector2 Size)
{
	public Vector2 Max => Min + Size;
	public Vector2 Center => Min + Size * 0.5f;

	public bool Contains(Vector2 point)
	{
		return point.X >= Min.X && point.Y >= Min.Y && point.X <= Max.X && point.Y <= Max.Y;
	}
}

/// <summary>Graph geometry is shared by painting, hit testing, fitting and arrangement.</summary>
internal static class AnimationGraphLayout
{
	public const float NodeWidth = 228.0f;
	public const float HeaderHeight = 48.0f;
	public const float PortSpacing = 26.0f;
	public const float ColumnSpacing = 76.0f;
	public const float RowSpacing = 32.0f;
	public static Vector2 NodeSize(AnimationNode node)
	{
		return new Vector2(NodeWidth, HeaderHeight + Math.Max(1, node.Inputs.Count) * PortSpacing + 18.0f);
	}

	public static Vector2 InputPosition(AnimationNode node, Vector2 position, int index)
	{
		return position + new Vector2(0.0f, HeaderHeight + 16.0f + index * PortSpacing);
	}

	public static Vector2 OutputPosition(Vector2 position)
	{
		return position + new Vector2(NodeWidth, HeaderHeight + 16.0f);
	}

	public static Dictionary<Guid, Vector2> Positions(AnimationGraph graph)
	{
		var positions = new Dictionary<Guid, Vector2>();
		foreach (var layout in graph.Layout)
		{
			if (float.IsFinite(layout.X) && float.IsFinite(layout.Y))
			{
				positions.TryAdd(layout.NodeId, new Vector2(layout.X, layout.Y));
			}
		}

		Dictionary<Guid, Vector2>? fallback = null;
		foreach (var node in graph.Nodes)
		{
			if (positions.ContainsKey(node.Id))
			{
				continue;
			}

			fallback ??= Arrange(graph);
			positions[node.Id] = fallback[node.Id];
		}

		return positions;
	}

	public static GraphRect Bounds(AnimationGraph graph, IReadOnlyDictionary<Guid, Vector2> positions, Guid? selected = null)
	{
		var min = new Vector2(float.MaxValue);
		var max = new Vector2(float.MinValue);
		foreach (var node in graph.Nodes)
		{
			if (selected.HasValue && node.Id != selected.Value)
			{
				continue;
			}

			if (positions.TryGetValue(node.Id, out var position) == false)
			{
				continue;
			}

			min = Vector2.Min(min, position);
			max = Vector2.Max(max, position + NodeSize(node));
		}

		return min.X == float.MaxValue ? new GraphRect(Vector2.Zero, Vector2.One) : new GraphRect(min, max - min);
	}

	public static Dictionary<Guid, Vector2> Arrange(AnimationGraph graph)
	{
		var nodes = graph.Nodes.DistinctBy(node => node.Id).ToDictionary(node => node.Id);
		var consumers = nodes.Keys.ToDictionary(id => id, _ => new List<Guid>());
		var indegree = nodes.Keys.ToDictionary(id => id, _ => 0);
		foreach (var node in nodes.Values)
		{
			foreach (var input in node.Inputs.Distinct())
			{
				if (nodes.ContainsKey(input) == false)
				{
					continue;
				}

				consumers[input].Add(node.Id);
				indegree[node.Id]++;
			}
		}

		var queue = new Queue<Guid>(nodes.Keys.Where(id => indegree[id] == 0));
		var order = new List<Guid>(nodes.Count);
		while (queue.TryDequeue(out var id))
		{
			order.Add(id);
			foreach (var consumer in consumers[id])
			{
				if (--indegree[consumer] == 0)
				{
					queue.Enqueue(consumer);
				}
			}
		}

		// Invalid cyclic graphs must still be inspectable and repairable.
		var distances = nodes.Keys.ToDictionary(id => id, _ => 0);
		for (var index = order.Count - 1; index >= 0; index--)
		{
			foreach (var consumer in consumers[order[index]])
			{
				distances[order[index]] = Math.Max(distances[order[index]], distances[consumer] + 1);
			}
		}

		var depth = distances.Values.DefaultIfEmpty(0).Max();
		var columns = graph.Nodes.DistinctBy(node => node.Id).GroupBy(node => depth - distances[node.Id]).OrderBy(group => group.Key);
		var result = new Dictionary<Guid, Vector2>();
		foreach (var column in columns)
		{
			var entries = column.ToArray();
			var height = entries.Sum(node => NodeSize(node).Y + RowSpacing) - RowSpacing;
			var y = -height * 0.5f;
			foreach (var node in entries)
			{
				result[node.Id] = new Vector2(column.Key * (NodeWidth + ColumnSpacing), y);
				y += NodeSize(node).Y + RowSpacing;
			}
		}

		return result;
	}

	public static void ApplyArrangement(AnimationGraph graph)
	{
		var positions = Arrange(graph);
		graph.Layout = graph.Nodes.DistinctBy(node => node.Id).Select(node => new AnimationNodeLayout { NodeId = node.Id, X = positions[node.Id].X, Y = positions[node.Id].Y }).ToList();
	}

	public static bool CanConnect(AnimationGraph graph, Guid sourceId, Guid targetId)
	{
		if (sourceId == Guid.Empty)
		{
			return true;
		}

		if (sourceId == targetId)
		{
			return false;
		}

		var nodes = graph.Nodes.DistinctBy(node => node.Id).ToDictionary(node => node.Id);
		if (nodes.TryGetValue(sourceId, out var source) == false || nodes.ContainsKey(targetId) == false)
		{
			return false;
		}

		if (source.Kind == AnimationNodeKind.Output)
		{
			return false;
		}

		var visited = new HashSet<Guid>();
		var pending = new Stack<Guid>();
		pending.Push(sourceId);
		while (pending.TryPop(out var id))
		{
			if (id == targetId)
			{
				return false;
			}

			if (visited.Add(id) == false || nodes.TryGetValue(id, out var node) == false)
			{
				continue;
			}

			foreach (var input in node.Inputs)
			{
				pending.Push(input);
			}
		}

		return true;
	}

	public static HashSet<Guid> ConnectionSources(AnimationGraph graph, Guid targetId)
	{
		var consumers = graph.Nodes.DistinctBy(node => node.Id).ToDictionary(node => node.Id, _ => new List<Guid>());
		foreach (var node in graph.Nodes)
		{
			foreach (var input in node.Inputs)
			{
				if (consumers.TryGetValue(input, out var list))
				{
					list.Add(node.Id);
				}
			}
		}

		var forbidden = new HashSet<Guid>();
		var pending = new Stack<Guid>();
		pending.Push(targetId);
		while (pending.TryPop(out var id))
		{
			if (forbidden.Add(id) == false || consumers.TryGetValue(id, out var list) == false)
			{
				continue;
			}

			foreach (var consumer in list)
			{
				pending.Push(consumer);
			}
		}

		return graph.Nodes.Where(node => forbidden.Contains(node.Id) == false && node.Kind != AnimationNodeKind.Output).Select(node => node.Id).ToHashSet();
	}

	public static string KindName(AnimationNodeKind kind)
	{
		return kind switch
		{
			AnimationNodeKind.BindPose => "Bind pose",
			AnimationNodeKind.Locomotion1D => "Locomotion blend",
			AnimationNodeKind.StateMachine => "State machine",
			AnimationNodeKind.MaskedBlend => "Masked blend",
			AnimationNodeKind.CurveRemap => "Curve remap",
			_ => kind.ToString()};
	}

	public static string Title(AnimationNode node)
	{
		return string.IsNullOrWhiteSpace(node.Name) ? KindName(node.Kind) : node.Name;
	}
}

internal readonly record struct AnimationGraphCamera(Vector2 Pan, float Zoom)
{
	public Vector2 ToScreen(Vector2 position) => Pan + position * Zoom;
	public Vector2 ToGraph(Vector2 position) => (position - Pan) / Zoom;
	public AnimationGraphCamera ZoomAt(Vector2 anchor, float factor)
	{
		var zoom = Math.Clamp(Zoom * factor, 0.000001f, 2.0f);
		return new AnimationGraphCamera(anchor - ToGraph(anchor) * zoom, zoom);
	}

	public static AnimationGraphCamera Fit(GraphRect bounds, Vector2 viewport, float padding = 40.0f)
	{
		var available = Vector2.Max(Vector2.One, viewport - new Vector2(padding * 2.0f));
		var zoom = Math.Min(
			1.0f,
			Math.Min(available.X / Math.Max(1.0f, bounds.Size.X), available.Y / Math.Max(1.0f, bounds.Size.Y)));
		zoom = Math.Max(0.000001f, zoom);
		return new AnimationGraphCamera(viewport * 0.5f - bounds.Center * zoom, zoom);
	}
}
