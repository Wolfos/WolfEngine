using System.Numerics;
using ImGuiNET;
using WolfEngine.AssetPipeline;
using WolfEngine.Modeling;
using WolfEngine.Rendering;

namespace WolfEngine.Editor.UI;

/// <summary>The selected node's parameters, the shape's compile settings, and the last compile's measurements.</summary>
public sealed class ShapeInspectorWindow : EditorWindow
{
	private static readonly string[] OperationNames = ["Union", "Subtract", "Intersect"];

	private readonly ShapeEditorSession _session;
	private readonly IPropertyDrawerRegistry _drawers;
	private readonly ShapeFieldEdit _fieldEdit = new();

	public ShapeInspectorWindow(ShapeEditorSession session, IPropertyDrawerRegistry drawers)
	{
		_session = session;
		_drawers = drawers;
	}

	public override string Name => "Shape Inspector";

	public override void OnHidden()
	{
		_fieldEdit.End();
	}

	public override void Draw(EditorScene scene)
	{
		Begin();
		_session.Tick();
		ShapeEditorPanels.HandleFocus(_session);
		ShapeEditorPanels.DrawDiagnostic(_session);

		if (_session.Document is not { } document)
		{
			_fieldEdit.End();
			ImGui.TextDisabled("Open a shape to inspect it.");
			ImGui.End();
			return;
		}

		ImGui.BeginDisabled(_session.CanEdit == false);
		if (_session.SelectedNode is { } node)
		{
			if (ImGui.CollapsingHeader("Node", ImGuiTreeNodeFlags.DefaultOpen))
			{
				DrawNode(node, isRoot: ReferenceEquals(node, document.Tree.Root));
			}
		}
		else
		{
			ImGui.TextDisabled("Select a node in the Shape Tree or the viewport.");
		}

		if (ImGui.CollapsingHeader("Compile", ImGuiTreeNodeFlags.DefaultOpen))
		{
			DrawSettings(document.Settings);
		}

		ImGui.EndDisabled();

		if (ImGui.CollapsingHeader("Metrics", ImGuiTreeNodeFlags.DefaultOpen))
		{
			DrawMetrics(document);
		}

		ImGui.End();
	}

	private void DrawNode(ShapeNode node, bool isRoot)
	{
		var id = node.Id;

		var name = node.Name;
		if (ImGui.InputText("Name", ref name, 128))
		{
			_fieldEdit.Changed(_session, "Rename Shape Node");
			_session.EditNode(id, "Rename Shape Node", edited => edited.Name = name);
		}

		_fieldEdit.AfterItem();

		// The root is the result itself, so how it would combine with a parent means nothing.
		if (isRoot == false)
		{
			var operation = (int)node.Operation;
			if (ImGui.Combo("Operation", ref operation, OperationNames, OperationNames.Length))
			{
				_session.EditNode(id, "Change Operation", edited => edited.Operation = (ShapeOperation)operation);
			}

			var smoothness = node.Smoothness;
			if (ImGui.DragFloat("Blend Radius", ref smoothness, 0.005f, 0.0f, 10.0f, "%.3f"))
			{
				_fieldEdit.Changed(_session, "Change Blend Radius");
				_session.EditNode(id, "Change Blend Radius", edited => edited.Smoothness = MathF.Max(0.0f, smoothness));
			}

			_fieldEdit.AfterItem();
		}

		var position = node.Position;
		if (ImGui.DragFloat3("Position", ref position, 0.01f))
		{
			_fieldEdit.Changed(_session, "Move Shape Node");
			_session.EditNode(id, "Move Shape Node", edited => edited.Position = position);
		}

		_fieldEdit.AfterItem();

		var euler = EulerAngles.ToDegrees(node.Rotation);
		if (ImGui.DragFloat3("Rotation", ref euler, 0.5f))
		{
			_fieldEdit.Changed(_session, "Rotate Shape Node");
			var rotation = EulerAngles.FromDegrees(euler);
			_session.EditNode(id, "Rotate Shape Node", edited => edited.Rotation = rotation);
		}

		_fieldEdit.AfterItem();

		var scale = node.Scale;
		if (ImGui.DragFloat("Scale", ref scale, 0.005f, ShapeNode.MinScale, 1000.0f, "%.3f"))
		{
			_fieldEdit.Changed(_session, "Scale Shape Node");
			_session.EditNode(id, "Scale Shape Node", edited => edited.Scale = MathF.Max(ShapeNode.MinScale, scale));
		}

		_fieldEdit.AfterItem();

		if (node.IsGroup)
		{
			ImGui.TextDisabled($"Group with {node.Children.Count} child node(s).");
			return;
		}

		DrawPrimitive(node);
	}

	private void DrawPrimitive(ShapeNode node)
	{
		var id = node.Id;
		ImGui.Separator();

		var primitiveNames = ShapePrimitives.All.Select(ShapePrimitives.GetDisplayName).ToArray();
		var primitive = Array.IndexOf(ShapePrimitives.All, node.Primitive);
		if (ImGui.Combo("Primitive", ref primitive, primitiveNames, primitiveNames.Length))
		{
			var kind = ShapePrimitives.All[primitive];
			_session.EditNode(id, "Change Primitive", edited =>
			{
				edited.Primitive = kind;
				edited.Dimensions = ShapePrimitives.GetDefaultDimensions(kind);
			});
		}

		var labels = ShapePrimitives.GetDimensionLabels(node.Primitive);
		var dimensions = node.Dimensions;
		for (var i = 0; i < labels.Length; i++)
		{
			var value = GetComponent(dimensions, i);
			if (ImGui.DragFloat(labels[i], ref value, 0.005f, ShapePrimitives.MinDimension, 1000.0f, "%.3f"))
			{
				_fieldEdit.Changed(_session, $"Change {labels[i]}");
				var component = i;
				var kind = node.Primitive;
				_session.EditNode(id, $"Change {labels[i]}", edited =>
				{
					edited.Dimensions = ShapePrimitives.Sanitize(kind, SetComponent(edited.Dimensions, component, value));
				});
			}

			_fieldEdit.AfterItem();
		}
	}

	private void DrawSettings(ShapeCompileSettings settings)
	{
		var resolution = settings.ReferenceResolution;
		if (ImGui.SliderInt(
			    "Reference Resolution",
			    ref resolution,
			    ShapeCompileSettings.MinReferenceResolution,
			    ShapeCompileSettings.MaxReferenceResolution))
		{
			_fieldEdit.Changed(_session, "Change Reference Resolution");
			_session.EditSettings("Change Reference Resolution", edited => edited.ReferenceResolution = resolution);
		}

		_fieldEdit.AfterItem();

		var budget = settings.TriangleBudget;
		if (ImGui.DragInt("Triangle Budget", ref budget, 50.0f, 100, 1_000_000))
		{
			_fieldEdit.Changed(_session, "Change Triangle Budget");
			_session.EditSettings("Change Triangle Budget", edited => edited.TriangleBudget = Math.Max(100, budget));
		}

		_fieldEdit.AfterItem();

		var tolerance = settings.ErrorTolerance;
		if (ImGui.DragFloat("Error Tolerance", ref tolerance, 0.0005f, 0.0001f, 1.0f, "%.4f"))
		{
			_fieldEdit.Changed(_session, "Change Error Tolerance");
			_session.EditSettings("Change Error Tolerance", edited => edited.ErrorTolerance = MathF.Max(0.0001f, tolerance));
		}

		_fieldEdit.AfterItem();

		var crease = settings.CreaseAngleDegrees;
		if (ImGui.SliderFloat("Crease Angle", ref crease, 0.0f, 180.0f, "%.0f deg"))
		{
			_fieldEdit.Changed(_session, "Change Crease Angle");
			_session.EditSettings("Change Crease Angle", edited => edited.CreaseAngleDegrees = crease);
		}

		_fieldEdit.AfterItem();

		var material = new AssetRef<Material>
		{
			NodeId = settings.MaterialId
		};
		if (_drawers.DrawAssetRef("Material", ref material))
		{
			var materialId = material.NodeId;
			_session.EditSettings("Change Material", edited => edited.MaterialId = materialId);
		}

		ImGui.TextDisabled("Budget, tolerance and crease angle take effect once the remesher lands (milestone 2).");
	}

	private void DrawMetrics(ShapeDocument document)
	{
		var status = _session.IsCompiling
			? "Compiling..."
			: document.NeedsCompile ? "Out of date" : "Up to date";
		ImGui.TextUnformatted($"Compile: {status}");

		if (document.Mesh is { } mesh)
		{
			ImGui.TextUnformatted($"Mesh: {mesh.TriangleCount:N0} triangles, {mesh.VertexCount:N0} vertices");
		}

		if (_session.LastCompileStats is { } stats)
		{
			ImGui.TextUnformatted($"Grid: {stats.GridSizeX} x {stats.GridSizeY} x {stats.GridSizeZ} ({stats.Sampler}), cell {stats.CellSize:0.0000}");
			ImGui.TextUnformatted($"Time: {stats.TotalMilliseconds:0.0} ms (sample {stats.SampleMilliseconds:0.0}, extract {stats.ExtractMilliseconds:0.0})");
			ImGui.TextUnformatted($"Program: {stats.InstructionCount} instruction(s)");
		}

		ImGui.BeginDisabled(_session.CanEdit == false);
		if (ImGui.Button("Recompile From Scratch"))
		{
			_session.RecompileCold();
		}

		ImGui.EndDisabled();
	}

	private static float GetComponent(Vector4 value, int index)
	{
		return index switch
		{
			0 => value.X,
			1 => value.Y,
			2 => value.Z,
			_ => value.W
		};
	}

	private static Vector4 SetComponent(Vector4 value, int index, float component)
	{
		switch (index)
		{
			case 0:
				value.X = component;
				break;

			case 1:
				value.Y = component;
				break;

			case 2:
				value.Z = component;
				break;

			default:
				value.W = component;
				break;
		}

		return value;
	}
}
