using System.Numerics;
using ImGuiNET;
using WolfEngine.Animation;

namespace WolfEngine.Editor.UI;

public sealed partial class AnimationWindow
{
	private bool _boxSelecting;
	private bool _boxMoved;
	private bool _boxAdditive;
	private Vector2 _boxStart;
	private Vector2 _boxEnd;
	private Guid[] _boxInitialSelection = [];
	private Vector2 _dragStart;
	private readonly Dictionary<Guid, Vector2> _dragPositions = new();

	private void BeginBoxSelection(Vector2 mouse, bool additive)
	{
		_boxSelecting = true;
		_boxMoved = false;
		_boxAdditive = additive;
		_boxStart = _view.Camera.ToGraph(mouse - _canvasRect.Min);
		_boxEnd = _boxStart;
		_boxInitialSelection = _nodeSelection.Ids.ToArray();
		if (additive == false)
		{
			_nodeSelection.Clear();
		}
	}

	private void UpdateBoxSelection(AnimationGraph graph, Vector2 mouse)
	{
		if (_boxSelecting == false) return;
		if (_boxMoved == false && ImGui.IsMouseDragging(ImGuiMouseButton.Left) == false) return;

		_boxMoved = true;
		_boxEnd = _view.Camera.ToGraph(Vector2.Clamp(mouse, _canvasRect.Min, _canvasRect.Max) - _canvasRect.Min);
		_nodeSelection.SelectBox(
			graph,
			_canvasPositions,
			_boxStart,
			_boxEnd,
			_boxAdditive ? _boxInitialSelection : []);
		_requestedTab = SidebarTab.Node;
	}

	private void CancelBoxSelection()
	{
		if (_boxSelecting == false) return;

		_nodeSelection.Set(_boxInitialSelection);
		_boxSelecting = false;
	}

	private void DrawBoxSelection(ImDrawListPtr draw)
	{
		if (_boxSelecting == false || _boxMoved == false) return;

		var min = ToScreen(Vector2.Min(_boxStart, _boxEnd));
		var max = ToScreen(Vector2.Max(_boxStart, _boxEnd));
		var fill = EditorPalette.Primary;
		fill.W = 0.12f;
		draw.AddRectFilled(min, max, ImGui.GetColorU32(fill));
		draw.AddRect(min, max, ImGui.GetColorU32(EditorPalette.Primary));
	}

	private void BeginNodeDrag(Guid nodeId, Vector2 mouse)
	{
		_dragNode = nodeId;
		_dragStart = _view.Camera.ToGraph(mouse - _canvasRect.Min);
		_dragPositions.Clear();
		foreach (var id in _nodeSelection.Ids)
		{
			if (_canvasPositions.TryGetValue(id, out var position))
			{
				_dragPositions.Add(id, position);
			}
		}
	}

	private void DragSelectedNodes(AnimationGraph graph, Vector2 mouse)
	{
		var delta = _view.Camera.ToGraph(mouse - _canvasRect.Min) - _dragStart;
		var layouts = graph.Layout.DistinctBy(layout => layout.NodeId).ToDictionary(layout => layout.NodeId);
		foreach (var (id, start) in _dragPositions)
		{
			var position = start + delta;
			if (layouts.TryGetValue(id, out var layout) == false)
			{
				layout = new AnimationNodeLayout
				{
					NodeId = id
				};
				graph.Layout.Add(layout);
				layouts.Add(id, layout);
			}

			layout.X = position.X;
			layout.Y = position.Y;
			_canvasPositions[id] = position;
		}

		_view.AutoFrame = false;
	}
}
