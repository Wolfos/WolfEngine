using System.Numerics;
using WolfEngine.Animation;

namespace WolfEngine.Editor.UI;

public sealed record AnimationGraphEditorRect(float X, float Y, float Width, float Height);
public sealed record AnimationGraphEditorNode(
	Guid Id,
	string Name,
	string Kind,
	AnimationGraphEditorRect Bounds,
	float[] Position,
	Guid[] Sources,
	float[][] Inputs,
	float[]? Output);
public sealed record AnimationGraphEditorState(
	Guid AssetId,
	Guid SelectedNodeId,
	Guid[] SelectedNodeIds,
	Guid ConnectingNodeId,
	float Zoom,
	bool Dirty,
	bool CanUndo,
	bool CanRedo,
	string? Message,
	AnimationGraphEditorRect Canvas,
	IReadOnlyDictionary<string, AnimationGraphEditorRect> Toolbar,
	IReadOnlyList<AnimationGraphEditorNode> Nodes);
public sealed partial class AnimationWindow
{
	public AnimationGraphEditorState GetEditorState()
	{
		var graph = _document?.Asset as AnimationGraph;
		var nodes = new List<AnimationGraphEditorNode>();
		if (graph is not null)
		{
			foreach (var node in graph.Nodes)
			{
				if (_canvasPositions.TryGetValue(node.Id, out var position) == false)
				{
					continue;
				}

				var inputs = Enumerable.Range(0, node.Inputs.Count).Select(index => Point(ToScreen(AnimationGraphLayout.InputPosition(node, position, index)))).ToArray();
				nodes.Add(new AnimationGraphEditorNode(
					node.Id,
					AnimationGraphLayout.Title(node),
					node.Kind.ToString(),
					Rect(new GraphRect(ToScreen(position), AnimationGraphLayout.NodeSize(node) * _view.Camera.Zoom)),
					Point(position),
					node.Inputs.ToArray(),
					inputs,
					node.Kind == AnimationNodeKind.Output ? null : Point(ToScreen(AnimationGraphLayout.OutputPosition(position)))));
			}
		}

		return new AnimationGraphEditorState(
			_assetId,
			_selectedNode,
			_nodeSelection.Ids.ToArray(),
			_connectFrom,
			_view.Camera.Zoom,
			_document?.Dirty ?? false,
			_document?.CanUndo ?? false,
			_document?.CanRedo ?? false,
			_canvasMessage,
			Rect(_canvasRect),
			_toolbarBounds.ToDictionary(entry => entry.Key, entry => Rect(entry.Value)),
			nodes);
	}

	private static AnimationGraphEditorRect Rect(GraphRect rect) => new(rect.Min.X, rect.Min.Y, rect.Size.X, rect.Size.Y);
	private static float[] Point(Vector2 point) => [point.X, point.Y];
}
