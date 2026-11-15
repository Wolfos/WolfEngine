using System.Numerics;
using ImGuiNET;
using WolfEngine.Animation;

namespace WolfEngine.Editor.UI;

public sealed partial class AnimationWindow
{
	private GraphRect _canvasRect;
	private Dictionary<Guid, Vector2> _canvasPositions = new();
	private Guid _dragNode;
	private string? _canvasMessage;
	private Guid _hoverNode;
	private double _hoverStarted;
	private readonly record struct GraphHit(Guid NodeId, int Input = -1, bool Output = false);
	private void DrawCanvas(AnimationGraph graph, Vector2 size, bool readOnly)
	{
		ImGui.PushStyleColor(ImGuiCol.ChildBg, ImGui.GetStyle().Colors[(int)ImGuiCol.PopupBg]);
		ImGui.BeginChild(
			"PoseCanvas",
			size,
			ImGuiChildFlags.Borders,
			ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
		try
		{
			_canvasRect = new GraphRect(
				ImGui.GetCursorScreenPos(),
				Vector2.Max(Vector2.One, ImGui.GetContentRegionAvail() - new Vector2(0.0f, 26.0f)));
			ImGui.InvisibleButton(
				"##GraphCanvas",
				_canvasRect.Size,
				ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonMiddle | ImGuiButtonFlags.MouseButtonRight);
			if (_arrangeRequested && readOnly == false)
			{
				AnimationGraphLayout.ApplyArrangement(graph);
				_view.FitRequested = true;
			}

			_arrangeRequested = false;
			_canvasPositions = AnimationGraphLayout.Positions(graph);
			_nodeSelection.Prune(graph);
			if (_createKind is { } kind && readOnly == false)
			{
				var position = _view.Camera.ToGraph(_canvasRect.Size * 0.5f);
				var created = CreateNode(graph, kind, position);
				_canvasPositions[created.Id] = position;
				SelectNode(created.Id);
			}

			_createKind = null;
			if (_view.FitRequested || (_view.AutoFrame && _view.ViewportSize != _canvasRect.Size))
			{
				_view.Camera = AnimationGraphCamera.Fit(AnimationGraphLayout.Bounds(graph, _canvasPositions), _canvasRect.Size);
				_view.FitRequested = false;
				_view.AutoFrame = true;
			}

			if (_focusNode != Guid.Empty && _canvasPositions.ContainsKey(_focusNode))
			{
				_view.Camera = AnimationGraphCamera.Fit(AnimationGraphLayout.Bounds(graph, _canvasPositions, _focusNode), _canvasRect.Size);
				_focusNode = Guid.Empty;
				_view.AutoFrame = false;
			}

			_view.ViewportSize = _canvasRect.Size;
			var hovered = ImGui.IsItemHovered();
			HandleCanvasInput(graph, hovered, readOnly);
			var draw = ImGui.GetWindowDrawList();
			draw.PushClipRect(_canvasRect.Min, _canvasRect.Max, true);
			DrawGrid(draw);
			var nodes = graph.Nodes.DistinctBy(node => node.Id).ToDictionary(node => node.Id);
			foreach (var node in nodes.Values)
			{
				for (var index = 0; index < node.Inputs.Count; index++)
				{
					if (nodes.TryGetValue(node.Inputs[index], out var source) == false)
					{
						continue;
					}

					var from = ToScreen(AnimationGraphLayout.OutputPosition(_canvasPositions[source.Id]));
					var to = ToScreen(AnimationGraphLayout.InputPosition(node, _canvasPositions[node.Id], index));
					var selected = _nodeSelection.Contains(node.Id) || _nodeSelection.Contains(source.Id);
					var color = NodeColor(source.Kind);
					color.W = selected ? 1.0f : 0.65f;
					DrawConnection(draw, from, to, ImGui.GetColorU32(color), selected ? 2.5f : 1.5f);
				}
			}

			if (_connectFrom != Guid.Empty && _canvasPositions.TryGetValue(_connectFrom, out var start))
			{
				DrawConnection(
					draw,
					ToScreen(AnimationGraphLayout.OutputPosition(start)),
					ImGui.GetMousePos(),
					ImGui.GetColorU32(EditorPalette.Primary),
					2.0f);
			}

			foreach (var node in nodes.Values.Where(node => _nodeSelection.Contains(node.Id) == false))
			{
				DrawCanvasNode(draw, node, nodes);
			}

			foreach (var node in nodes.Values.Where(node => _nodeSelection.Contains(node.Id) && node.Id != _selectedNode))
			{
				DrawCanvasNode(draw, node, nodes);
			}

			if (nodes.TryGetValue(_selectedNode, out var selectedNode))
			{
				DrawCanvasNode(draw, selectedNode, nodes);
			}

			DrawBoxSelection(draw);
			draw.PopClipRect();
			var hint = _canvasMessage ?? (_connectFrom != Guid.Empty
				? "Choose an input to connect  /  Esc to cancel"
				: "Drag background to select  /  Shift-click to add  /  Trackpad, middle-drag or Space-drag to pan  /  Pinch or wheel to zoom");
			draw.AddText(
				new Vector2(_canvasRect.Min.X + 6.0f, _canvasRect.Max.Y + 7.0f),
				ImGui.GetColorU32(ImGuiCol.TextDisabled),
				hint);
			var zoomText = $"{_view.Camera.Zoom:P0}  /  {graph.Nodes.Count} nodes";
			if (_nodeSelection.Count > 1)
			{
				zoomText += $"  /  {_nodeSelection.Count} selected";
			}
			draw.AddText(
				new Vector2(_canvasRect.Max.X - ImGui.CalcTextSize(zoomText).X - 8.0f, _canvasRect.Min.Y + 8.0f),
				ImGui.GetColorU32(ImGuiCol.TextDisabled),
				zoomText);
			DrawCanvasContextMenu(graph, readOnly);
			if (hovered && _dragNode == Guid.Empty && _connectFrom == Guid.Empty && _boxSelecting == false)
			{
				var hit = HitGraph(graph, ImGui.GetMousePos());
				if (hit.NodeId != _hoverNode || ImGui.GetIO().MouseDelta.LengthSquared() > 1.0f)
				{
					_hoverNode = hit.NodeId;
					_hoverStarted = ImGui.GetTime();
				}

				if (hit.NodeId != Guid.Empty && ImGui.GetTime() - _hoverStarted > 0.6 && nodes.TryGetValue(hit.NodeId, out var node))
				{
					ImGui.SetTooltip($"{AnimationGraphLayout.Title(node)}\n{AnimationGraphLayout.KindName(node.Kind)}");
				}
			}
			else
			{
				_hoverNode = Guid.Empty;
			}
		}
		finally
		{
			ImGui.EndChild();
			ImGui.PopStyleColor();
		}
	}

	private void HandleCanvasInput(AnimationGraph graph, bool hovered, bool readOnly)
	{
		var io = ImGui.GetIO();
		var mouse = ImGui.GetMousePos();
		if (ImGui.IsMouseDown(ImGuiMouseButton.Left) == false && ImGui.IsMouseReleased(ImGuiMouseButton.Left) == false)
		{
			CancelBoxSelection();
			_dragNode = Guid.Empty;
		}

		var panGesture = ImGui.IsMouseDown(ImGuiMouseButton.Middle) ||
			(ImGui.IsKeyDown(ImGuiKey.Space) && ImGui.IsMouseDown(ImGuiMouseButton.Left) && io.WantTextInput == false);
		if ((hovered || ImGui.IsItemActive()) && panGesture)
		{
			CancelBoxSelection();
			_view.Camera = _view.Camera with
			{
				Pan = _view.Camera.Pan + io.MouseDelta
			};
			_view.AutoFrame = false;

			_dragNode = Guid.Empty;
			ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeAll);
		}

		if (hovered)
		{
			var scroll = new Vector2(io.MouseWheelH, io.MouseWheel);
			var zoom = _imgui.MouseZoomFactor;
			var pan = _imgui.MousePanDelta;
			if (io.KeyCtrl || io.KeySuper)
			{
				zoom *= MathF.Pow(1.15f, scroll.Y);
				pan = Vector2.Zero;
			}
			else
			{
				zoom *= MathF.Pow(1.15f, _imgui.MouseWheelZoomDelta.Y);
				pan.X += _imgui.MouseWheelZoomDelta.X * 35.0f;
			}

			if (pan != Vector2.Zero)
			{
				_view.Camera = _view.Camera with
				{
					Pan = _view.Camera.Pan + pan
				};
				_view.AutoFrame = false;
			}

			if (zoom != 1.0f)
			{
				_view.Camera = _view.Camera.ZoomAt(mouse - _canvasRect.Min, zoom);
				_view.AutoFrame = false;
			}
		}

		if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
		{
			CancelBoxSelection();
			var hit = HitGraph(graph, mouse);
			_contextNode = hit.NodeId;
			_contextInput = hit.Input;
			_contextCanvasPosition = _view.Camera.ToGraph(mouse - _canvasRect.Min);
			_connectFrom = Guid.Empty;
			_dragNode = Guid.Empty;
			_canvasMessage = null;
			ImGui.OpenPopup("Graph context menu");
		}

		if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left) && panGesture == false)
		{
			ImGui.SetWindowFocus();
			_canvasMessage = null;
			var hit = HitGraph(graph, mouse);
			if (hit.NodeId == Guid.Empty)
			{
				BeginBoxSelection(mouse, io.KeyShift || io.KeyCtrl || io.KeySuper);
				_connectFrom = Guid.Empty;
			}
			else
			{
				if (hit.Output || hit.Input >= 0)
				{
					SelectNode(hit.NodeId);
					if (readOnly == false && hit.Output)
					{
						_connectFrom = hit.NodeId;
					}
					else if (readOnly == false && hit.Input >= 0)
					{
						if (_connectFrom != Guid.Empty)
						{
							Connect(graph, hit.NodeId, hit.Input);
						}
						else
						{
							_connectFrom = graph.Nodes.First(node => node.Id == hit.NodeId).Inputs[hit.Input];
						}
					}
				}
				else
				{
					_connectFrom = Guid.Empty;
					if (io.KeyShift || io.KeyCtrl || io.KeySuper)
					{
						_nodeSelection.Toggle(hit.NodeId);
					}
					else if (_nodeSelection.Contains(hit.NodeId) == false)
					{
						SelectNode(hit.NodeId);
					}

					_requestedTab = SidebarTab.Node;
					if (readOnly == false && _nodeSelection.Contains(hit.NodeId))
					{
						BeginNodeDrag(hit.NodeId, mouse);
					}
				}
			}
		}

		UpdateBoxSelection(graph, mouse);
		if (_dragNode != Guid.Empty && ImGui.IsMouseDragging(ImGuiMouseButton.Left) && panGesture == false && readOnly == false)
		{
			DragSelectedNodes(graph, mouse);
		}

		if (ImGui.IsMouseReleased(ImGuiMouseButton.Left))
		{
			_boxSelecting = false;
			if (_connectFrom != Guid.Empty && hovered && readOnly == false)
			{
				var hit = HitGraph(graph, mouse);
				if (hit.Input >= 0)
				{
					Connect(graph, hit.NodeId, hit.Input);
				}
			}

			_dragNode = Guid.Empty;
		}

		if (ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows) && io.WantTextInput == false)
		{
			if (ImGui.IsKeyPressed(ImGuiKey.Escape))
			{
				CancelBoxSelection();
				_connectFrom = Guid.Empty;
				_canvasMessage = null;
			}

			if (hovered && ImGui.IsKeyPressed(ImGuiKey.Home))
			{
				_view.FitRequested = true;
			}

			if (hovered && ImGui.IsKeyPressed(ImGuiKey.F))
			{
				if (_selectedNode != Guid.Empty)
				{
					_focusNode = _selectedNode;
				}
				else
				{
					_view.FitRequested = true;
				}
			}

			if (hovered && readOnly == false && ImGui.IsKeyPressed(ImGuiKey.Delete) && _selectedNode != Guid.Empty)
			{
				DeleteNodes(graph, _nodeSelection.Ids.ToHashSet());
				_nodeSelection.Clear();
				_boxSelecting = false;
				_connectFrom = Guid.Empty;
				_dragNode = Guid.Empty;
			}
		}
	}

	private void Connect(AnimationGraph graph, Guid target, int input)
	{
		if (AnimationGraphLayout.CanConnect(graph, _connectFrom, target))
		{
			graph.Nodes.First(node => node.Id == target).Inputs[input] = _connectFrom;
			_connectFrom = Guid.Empty;
			_canvasMessage = null;
		}
		else
		{
			_canvasMessage = "That connection would create a cycle.";
		}
	}

	private GraphHit HitGraph(AnimationGraph graph, Vector2 mouse)
	{
		var ordered = graph.Nodes.Where(node => _nodeSelection.Contains(node.Id) == false)
			.Concat(graph.Nodes.Where(node => _nodeSelection.Contains(node.Id) && node.Id != _selectedNode))
			.Concat(graph.Nodes.Where(node => node.Id == _selectedNode))
			.Reverse();
		var radius = MathF.Max(7.0f, 7.0f * _view.Camera.Zoom);
		foreach (var node in ordered)
		{
			if (_canvasPositions.TryGetValue(node.Id, out var position) == false)
			{
				continue;
			}

			for (var index = 0; index < node.Inputs.Count; index++)
			{
				if (Vector2.Distance(mouse, ToScreen(AnimationGraphLayout.InputPosition(node, position, index))) < radius)
				{
					return new GraphHit(node.Id, index);
				}
			}

			if (node.Kind != AnimationNodeKind.Output && Vector2.Distance(mouse, ToScreen(AnimationGraphLayout.OutputPosition(position))) < radius)
			{
				return new GraphHit(node.Id, Output: true);
			}

			if (new GraphRect(ToScreen(position), AnimationGraphLayout.NodeSize(node) * _view.Camera.Zoom).Contains(mouse))
			{
				return new GraphHit(node.Id);
			}
		}

		return default;
	}

	private Vector2 ToScreen(Vector2 point) => _canvasRect.Min + _view.Camera.ToScreen(point);
	private void DrawGrid(ImDrawListPtr draw)
	{
		var spacing = 32.0f * _view.Camera.Zoom;
		while (spacing < 18.0f)
		{
			spacing *= 2.0f;
		}

		var color = ImGui.GetColorU32(new Vector4(1.0f, 1.0f, 1.0f, 0.035f));
		for (var x = _view.Camera.Pan.X % spacing; x < _canvasRect.Size.X; x += spacing)
		{
			draw.AddLine(
				_canvasRect.Min + new Vector2(x, 0.0f),
				new Vector2(_canvasRect.Min.X + x, _canvasRect.Max.Y),
				color);
		}

		for (var y = _view.Camera.Pan.Y % spacing; y < _canvasRect.Size.Y; y += spacing)
		{
			draw.AddLine(
				_canvasRect.Min + new Vector2(0.0f, y),
				new Vector2(_canvasRect.Max.X, _canvasRect.Min.Y + y),
				color);
		}
	}

	private static void DrawConnection(ImDrawListPtr draw, Vector2 from, Vector2 to, uint color, float thickness)
	{
		var bend = MathF.Max(40.0f, MathF.Abs(to.X - from.X) * 0.45f);
		draw.AddBezierCubic(from, from + new Vector2(bend, 0.0f), to - new Vector2(bend, 0.0f), to, color, thickness);
	}

	private void DrawCanvasNode(ImDrawListPtr draw, AnimationNode node, IReadOnlyDictionary<Guid, AnimationNode> nodes)
	{
		var zoom = _view.Camera.Zoom;
		var min = ToScreen(_canvasPositions[node.Id]);
		var size = AnimationGraphLayout.NodeSize(node) * zoom;
		var max = min + size;
		if (max.X < _canvasRect.Min.X || max.Y < _canvasRect.Min.Y || min.X > _canvasRect.Max.X || min.Y > _canvasRect.Max.Y)
		{
			return;
		}

		var accent = NodeColor(node.Kind);
		var selected = _nodeSelection.Contains(node.Id);
		var rounding = MathF.Max(3.0f, 7.0f * zoom);
		var surface = ImGui.GetStyle().Colors[(int)ImGuiCol.WindowBg];
		draw.AddRectFilled(min + new Vector2(2.0f, 4.0f), max + new Vector2(2.0f, 4.0f), 0x40000000, rounding);
		draw.AddRectFilled(min, max, ImGui.GetColorU32(surface), rounding);
		draw.AddRectFilled(
			min,
			new Vector2(max.X, min.Y + AnimationGraphLayout.HeaderHeight * zoom),
			ImGui.GetColorU32(EditorPalette.Tint(surface, accent, 0.18f)),
			rounding,
			ImDrawFlags.RoundCornersTop);
		draw.AddRect(
			min,
			max,
			ImGui.GetColorU32(selected ? EditorPalette.Primary : new Vector4(accent.X, accent.Y, accent.Z, 0.55f)),
			rounding,
			ImDrawFlags.None,
			selected ? 2.0f : 1.0f);
		var titleSize = MathF.Min(MathF.Max(11.0f, ImGui.GetFontSize() * zoom), size.Y - 3.0f);
		DrawGraphText(
			draw,
			min + new Vector2(12.0f, 6.0f) * zoom,
			AnimationGraphLayout.Title(node),
			titleSize,
			ImGui.GetColorU32(ImGuiCol.Text),
			size.X - 20.0f * zoom);
		if (zoom >= 0.60f)
		{
			DrawGraphText(
				draw,
				min + new Vector2(12.0f, 28.0f) * zoom,
				AnimationGraphLayout.KindName(node.Kind),
				11.0f * zoom,
				ImGui.GetColorU32(accent),
				size.X - 20.0f * zoom);
		}

		var radius = MathF.Max(3.0f, 5.0f * zoom);
		for (var index = 0; index < node.Inputs.Count; index++)
		{
			var input = ToScreen(AnimationGraphLayout.InputPosition(node, _canvasPositions[node.Id], index));
			var connected = nodes.ContainsKey(node.Inputs[index]);
			draw.AddCircleFilled(input, radius + 1.5f, ImGui.GetColorU32(ImGuiCol.PopupBg));
			if (connected)
			{
				draw.AddCircleFilled(input, radius, ImGui.GetColorU32(accent));
			}
			else
			{
				draw.AddCircle(input, radius, ImGui.GetColorU32(accent), 0, 1.5f);
			}

			if (zoom >= 0.46f)
			{
				var label = InputLabel(node, index, nodes);
				DrawGraphText(
					draw,
					input + new Vector2(12.0f * zoom, -6.0f * zoom),
					label,
					MathF.Max(9.0f, 12.0f * zoom),
					ImGui.GetColorU32(ImGuiCol.TextDisabled),
					size.X - 34.0f * zoom);
			}
		}

		if (node.Kind != AnimationNodeKind.Output)
		{
			var output = ToScreen(AnimationGraphLayout.OutputPosition(_canvasPositions[node.Id]));
			draw.AddCircleFilled(output, radius + 1.5f, ImGui.GetColorU32(ImGuiCol.PopupBg));
			draw.AddCircleFilled(output, radius, ImGui.GetColorU32(accent));
		}

		if (node.Inputs.Count == 0 && zoom >= 0.46f)
		{
			var label = node.Kind == AnimationNodeKind.Clip ? $"{node.ClipSlot}  /  {node.Speed:G3}x" : "Rest pose";
			DrawGraphText(
				draw,
				min + new Vector2(12.0f, 58.0f) * zoom,
				label,
				MathF.Max(9.0f, 12.0f * zoom),
				ImGui.GetColorU32(ImGuiCol.TextDisabled),
				size.X - 30.0f * zoom);
		}
	}

	private static string InputLabel(AnimationNode node, int index, IReadOnlyDictionary<Guid, AnimationNode> nodes)
	{
		var source = nodes.TryGetValue(node.Inputs[index], out var input) ? AnimationGraphLayout.Title(input) : "Unconnected";
		return node.Kind switch
		{
			AnimationNodeKind.StateMachine => $"{index}  {source}",
			AnimationNodeKind.Locomotion1D => $"{(index < node.Thresholds.Count ? node.Thresholds[index] : index):G3}  {source}",
			AnimationNodeKind.Blend or AnimationNodeKind.MaskedBlend => index == 0 ? "Base pose" : "Blend pose",
			AnimationNodeKind.Output => "Final pose",
			AnimationNodeKind.Select => $"Choice {index}",
			_ => "Pose"
		};
	}

	private static Vector4 NodeColor(AnimationNodeKind kind)
	{
		return kind switch
		{
			AnimationNodeKind.Clip => new Vector4(0.50f, 0.84f, 0.68f, 1.0f),
			AnimationNodeKind.StateMachine or AnimationNodeKind.Select => new Vector4(0.94f, 0.76f, 0.48f, 1.0f),
			AnimationNodeKind.Blend or AnimationNodeKind.MaskedBlend or AnimationNodeKind.Locomotion1D => new Vector4(0.48f, 0.78f, 0.91f, 1.0f),
			_ => EditorPalette.Primary
		};
	}

	private static void DrawGraphText(ImDrawListPtr draw, Vector2 position, string text, float fontSize, uint color, float width)
	{
		if (fontSize <= 0.0f || width <= 0.0f)
		{
			return;
		}

		var scale = fontSize / ImGui.GetFontSize();
		if (ImGui.CalcTextSize(text).X * scale > width)
		{
			var length = Math.Max(
				0,
				Math.Min(
				text.Length,
				(int)(text.Length * width / Math.Max(1.0f, ImGui.CalcTextSize(text).X * scale)) - 2));
			while (length > 0 && ImGui.CalcTextSize(text[..length] + "...").X * scale > width)
			{
				length--;
			}

			if (length > 0 && char.IsHighSurrogate(text[length - 1]))
			{
				length--;
			}

			text = text[..length] + "...";
		}

		draw.PushClipRect(position, position + new Vector2(width, fontSize + 3.0f), true);
		draw.AddText(ImGui.GetFont(), fontSize, position, color, text);
		draw.PopClipRect();
	}

	private void DrawCanvasContextMenu(AnimationGraph graph, bool readOnly)
	{
		if (ImGui.BeginPopup("Graph context menu") == false)
		{
			return;
		}

		if (_contextNode != Guid.Empty)
		{
			if (ImGui.MenuItem("Focus node"))
			{
				SelectNode(_contextNode, focus: true);
			}

			ImGui.BeginDisabled(readOnly);
			if (_contextInput >= 0 && ImGui.MenuItem("Disconnect input"))
			{
				graph.Nodes.First(node => node.Id == _contextNode).Inputs[_contextInput] = Guid.Empty;
			}

			if (ImGui.MenuItem("Delete node"))
			{
				DeleteNode(graph, _contextNode);
				_nodeSelection.Prune(graph);

				_connectFrom = Guid.Empty;
			}

			ImGui.EndDisabled();
		}
		else
		{
			ImGui.BeginDisabled(readOnly);
			if (ImGui.BeginMenu("Add node"))
			{
				foreach (var kind in Enum.GetValues<AnimationNodeKind>())
				{
					if (ImGui.MenuItem(AnimationGraphLayout.KindName(kind)))
					{
						var node = CreateNode(graph, kind, _contextCanvasPosition);
						SelectNode(node.Id);
					}
				}

				ImGui.EndMenu();
			}

			if (ImGui.MenuItem("Arrange graph"))
			{
				_arrangeRequested = true;
			}

			ImGui.EndDisabled();
			if (ImGui.MenuItem("Fit all"))
			{
				_view.FitRequested = true;
			}
		}

		ImGui.EndPopup();
	}
}
