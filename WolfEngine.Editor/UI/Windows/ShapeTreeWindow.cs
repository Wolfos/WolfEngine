using ImGuiNET;
using WolfEngine.Modeling;

namespace WolfEngine.Editor.UI;

/// <summary>The open shape's operation tree: selection, adding primitives and groups, and drag-to-reparent.</summary>
public sealed class ShapeTreeWindow : EditorWindow
{
	private const string DragPayload = "SHAPE_NODE";

	private readonly ShapeEditorSession _session;
	private Guid _draggedNodeId;
	private int _revealedSelection = -1;

	public ShapeTreeWindow(ShapeEditorSession session)
	{
		_session = session;
	}

	public override string Name => "Shape Tree";

	public override void Draw(EditorScene scene)
	{
		Begin();
		_session.Tick();
		ShapeEditorPanels.HandleFocus(_session);

		if (_session.Document is not { } document)
		{
			ImGui.TextDisabled("Open a shape from Assets, or create one with Create > Shape.");
			ImGui.End();
			return;
		}

		ImGui.TextUnformatted(document.IsDirty ? $"{_session.ShapeName} *" : _session.ShapeName);
		if (_session.CanEdit)
		{
			DrawAddButtons();
		}

		ImGui.Separator();
		if (ImGui.BeginChild("##ShapeTreeNodes"))
		{
			DrawNode(document.Tree.Root, document.Tree, isRoot: true);
			_revealedSelection = _session.SelectionRevision;

			// Clicking empty space clears the selection, like the entity hierarchy.
			if (ImGui.IsWindowHovered() &&
			    ImGui.IsMouseClicked(ImGuiMouseButton.Left) &&
			    ImGui.IsAnyItemHovered() == false)
			{
				_session.Select(null);
			}
		}

		ImGui.EndChild();
		ImGui.End();
	}

	private void DrawAddButtons()
	{
		if (ImGui.Button("Add Primitive"))
		{
			ImGui.OpenPopup("##AddShapePrimitive");
		}

		if (ImGui.BeginPopup("##AddShapePrimitive"))
		{
			foreach (var kind in ShapePrimitives.All)
			{
				if (ImGui.MenuItem(ShapePrimitives.GetDisplayName(kind)))
				{
					_session.AddPrimitive(kind);
				}
			}

			ImGui.EndPopup();
		}

		ImGui.SameLine();
		if (ImGui.Button("Add Group"))
		{
			_session.AddGroup();
		}
	}

	private void DrawNode(ShapeNode node, ShapeTree tree, bool isRoot)
	{
		var flags = ImGuiTreeNodeFlags.OpenOnArrow |
		            ImGuiTreeNodeFlags.SpanAvailWidth |
		            ImGuiTreeNodeFlags.DefaultOpen;
		if (node.IsGroup == false || node.Children.Count == 0)
		{
			flags |= ImGuiTreeNodeFlags.Leaf;
		}

		if (_session.SelectedNodeId == node.Id)
		{
			flags |= ImGuiTreeNodeFlags.Selected;
			if (_revealedSelection != _session.SelectionRevision)
			{
				ImGui.SetScrollHereY();
			}
		}

		var label = isRoot ? node.Name : $"{GetOperationSymbol(node.Operation)} {node.Name}";
		var open = ImGui.TreeNodeEx($"{label}##{node.Id}", flags);
		if (ImGui.IsItemClicked(ImGuiMouseButton.Left) && ImGui.IsItemToggledOpen() == false)
		{
			_session.Select(node.Id);
		}

		if (isRoot == false && _session.CanEdit && ImGui.BeginDragDropSource())
		{
			_draggedNodeId = node.Id;
			ImGui.SetDragDropPayload(DragPayload, IntPtr.Zero, 0);
			ImGui.TextUnformatted(node.Name);
			ImGui.EndDragDropSource();
		}

		if (_session.CanEdit && ImGui.BeginDragDropTarget())
		{
			if (AcceptDrop())
			{
				// Dropped on a group: append to it. Dropped on a primitive: place after it in its group.
				if (node.IsGroup)
				{
					_session.MoveNode(_draggedNodeId, node.Id, node.Children.Count);
				}
				else if (tree.FindParent(node.Id) is { } parent)
				{
					_session.MoveNode(_draggedNodeId, parent.Id, parent.Children.IndexOf(node) + 1);
				}
			}

			ImGui.EndDragDropTarget();
		}

		if (open == false)
		{
			return;
		}

		// Iterate over a copy: a drop handled above can reorder the list being drawn.
		foreach (var child in node.Children.ToArray())
		{
			DrawNode(child, tree, isRoot: false);
		}

		ImGui.TreePop();
	}

	private static unsafe bool AcceptDrop()
	{
		var payload = ImGui.AcceptDragDropPayload(DragPayload);
		return payload.NativePtr != null;
	}

	private static string GetOperationSymbol(ShapeOperation operation)
	{
		return operation switch
		{
			ShapeOperation.Union => "+",
			ShapeOperation.Subtract => "-",
			ShapeOperation.Intersect => "&",
			_ => "?"
		};
	}
}
