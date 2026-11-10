using System.Numerics;
using WolfEngine.Editor.UI;
using WolfEngine.Modeling;

namespace WolfEngine.Editor.Automation;

/// <summary>One shape node as automation sees it. Rotation is Euler degrees, as the inspector shows it.</summary>
public sealed record ShapeNodeResult(
	string Id,
	string? ParentId,
	string Name,
	string Kind,
	string Operation,
	float Smoothness,
	float[] Position,
	float[] RotationDegrees,
	float Scale,
	string? Primitive,
	float[]? Dimensions);

/// <summary>The Model workspace as automation sees it: the open shape, its tree, history and the last compile.</summary>
/// <param name="AppliedCompileCount">Compiles applied to a document since the shape opened; increases when a compile lands.</param>
/// <param name="LastCompile">Measurements of the last finished compile, applied or not.</param>
public sealed record ShapeEditorStateResult(
	string? AssetId,
	string ShapeName,
	string? Diagnostic,
	string? CompileError,
	bool IsReadOnly,
	bool IsDirty,
	bool IsPromptPending,
	bool CanUndo,
	bool CanRedo,
	string? UndoDescription,
	string? SelectedNodeId,
	IReadOnlyList<ShapeNodeResult> Nodes,
	bool NeedsCompile,
	bool IsCompiling,
	int MeshTriangleCount,
	int MeshVertexCount,
	long AppliedCompileCount,
	ShapeCompileStats? LastCompile,
	bool HasPreview);

/// <summary>Shape editor automation. Every call runs on the editor thread and returns the editor's state afterwards.</summary>
public sealed partial class EditorRemoteAutomationController
{
	public Task<ShapeEditorStateResult> CreateShapeAsync(string folder, string name, CancellationToken token) =>
		Enqueue(() =>
		{
			_shapeEditor.CreateShape(folder, name, UnsavedChangesPolicy.Discard);
			return InspectShapeEditor();
		}, token);

	public Task<ShapeEditorStateResult> OpenShapeAsync(Guid assetId, bool discardUnsavedChanges, CancellationToken token) =>
		Enqueue(() =>
		{
			var asset = GetAsset(assetId);
			if (ShapeEditorSession.IsShapeAsset(asset) == false)
			{
				throw new InvalidOperationException($"Asset '{assetId}' is not a shape.");
			}

			_shapeEditor.Open(asset, discardUnsavedChanges ? UnsavedChangesPolicy.Discard : UnsavedChangesPolicy.Prompt);
			return InspectShapeEditor();
		}, token);

	/// <summary>Adds a primitive or, for kind "group", a group, next to the selection or inside a given group.</summary>
	public Task<ShapeEditorStateResult> AddShapeNodeAsync(string kind, Guid? parentId, CancellationToken token) =>
		Enqueue(() =>
		{
			RequireEditableShape();
			if (parentId is { } parent)
			{
				_shapeEditor.Select(parent);
			}

			var node = string.Equals(kind, "group", StringComparison.OrdinalIgnoreCase)
				? ShapeNode.CreateGroup("Group")
				: ShapeNode.CreatePrimitive(ParsePrimitive(kind));
			_shapeEditor.AddNode(node);
			return InspectShapeEditor();
		}, token);

	/// <summary>Edits a node as the inspector does, as one undo step. Omitted values keep theirs.</summary>
	public Task<ShapeEditorStateResult> SetShapeNodeAsync(
		Guid nodeId,
		string? name,
		string? operation,
		float? smoothness,
		Vector3? position,
		Vector3? rotationDegrees,
		float? scale,
		string? primitive,
		Vector4? dimensions,
		CancellationToken token) =>
		Enqueue(() =>
		{
			RequireEditableShape();
			var parsedOperation = operation is null ? (ShapeOperation?)null : Enum.Parse<ShapeOperation>(operation, ignoreCase: true);
			var parsedPrimitive = primitive is null ? (ShapePrimitiveKind?)null : ParsePrimitive(primitive);
			var edited = _shapeEditor.EditNode(nodeId, "Edit Shape Node", node =>
			{
				if (name is not null)
				{
					node.Name = name;
				}

				if (parsedOperation is { } newOperation)
				{
					node.Operation = newOperation;
				}

				if (smoothness is { } newSmoothness)
				{
					node.Smoothness = MathF.Max(0.0f, newSmoothness);
				}

				if (position is { } newPosition)
				{
					node.Position = newPosition;
				}

				if (rotationDegrees is { } newRotation)
				{
					node.Rotation = EulerAngles.FromDegrees(newRotation);
				}

				if (scale is { } newScale)
				{
					node.Scale = MathF.Max(ShapeNode.MinScale, newScale);
				}

				if (parsedPrimitive is { } newPrimitive && node.IsGroup == false)
				{
					node.Primitive = newPrimitive;
				}

				if (dimensions is { } newDimensions && node.IsGroup == false)
				{
					node.Dimensions = ShapePrimitives.Sanitize(node.Primitive, newDimensions);
				}
			});

			if (edited == false)
			{
				throw new InvalidOperationException($"The open shape has no node '{nodeId}'.");
			}

			return InspectShapeEditor();
		}, token);

	public Task<ShapeEditorStateResult> SelectShapeNodeAsync(Guid? nodeId, CancellationToken token) =>
		Enqueue(() =>
		{
			RequireShape();
			if (nodeId is { } id && _shapeEditor.Document!.Tree.Find(id) is null)
			{
				throw new InvalidOperationException($"The open shape has no node '{id}'.");
			}

			_shapeEditor.Select(nodeId);
			return InspectShapeEditor();
		}, token);

	public Task<ShapeEditorStateResult> DeleteShapeNodeAsync(Guid nodeId, CancellationToken token) =>
		Enqueue(() =>
		{
			RequireEditableShape();
			_shapeEditor.Select(nodeId);
			if (_shapeEditor.DeleteSelection() == false)
			{
				throw new InvalidOperationException($"Node '{nodeId}' could not be deleted; the root cannot be.");
			}

			return InspectShapeEditor();
		}, token);

	public Task<ShapeEditorStateResult> ShapeHistoryAsync(string operation, CancellationToken token) =>
		Enqueue(() =>
		{
			RequireShape();
			var done = operation.ToLowerInvariant() switch
			{
				"undo" => _shapeEditor.Undo(),
				"redo" => _shapeEditor.Redo(),
				_ => throw new ArgumentException("operation must be undo or redo.", nameof(operation))
			};
			if (done == false)
			{
				throw new InvalidOperationException($"Nothing to {operation}.");
			}

			return InspectShapeEditor();
		}, token);

	/// <summary>Brings the compiled mesh up to date and waits for it. Cold discards the warm-start history first.</summary>
	public Task<ShapeEditorStateResult> CompileShapeAsync(bool cold, CancellationToken token) =>
		Enqueue(() =>
		{
			RequireShape();
			if (cold)
			{
				_shapeEditor.RecompileCold();
			}

			if (_shapeEditor.CompileNow() == false)
			{
				throw new InvalidOperationException($"The shape did not compile: {_shapeEditor.CompileError}");
			}

			return InspectShapeEditor();
		}, token);

	public Task<ShapeEditorStateResult> SaveShapeAsync(CancellationToken token) =>
		Enqueue(() =>
		{
			RequireEditableShape();
			if (_shapeEditor.Save() == false)
			{
				throw new InvalidOperationException(_shapeEditor.Diagnostic ?? "The shape could not be saved.");
			}

			return InspectShapeEditor();
		}, token);

	public Task<ShapeEditorStateResult> GetShapeEditorStateAsync(CancellationToken token) =>
		Enqueue(InspectShapeEditor, token);

	/// <summary>Picks like a click in the viewport, at a point in normalised device coordinates, and selects the result.</summary>
	public Task<ShapeEditorStateResult> PickShapeAsync(float ndcX, float ndcY, CancellationToken token) =>
		Enqueue(() =>
		{
			RequireShape();
			var preview = _shapeEditor.Preview
				?? throw new InvalidOperationException("No shape preview is showing. Keep the Shape Viewport visible.");
			if (preview.TryGetRay(new Vector2(ndcX, ndcY), out var origin, out var direction) == false)
			{
				throw new InvalidOperationException("The shape preview has not rendered yet; wait for render frames.");
			}

			_shapeEditor.Select(_shapeEditor.Pick(origin, direction));
			return InspectShapeEditor();
		}, token);

	public async Task<FrameCaptureResult> CaptureShapePreviewAsync(string path, CancellationToken token)
	{
		var view = await Enqueue(
			() => _shapeEditor.Preview?.View ?? throw new InvalidOperationException("No shape preview is showing. Open a shape with the Shape Viewport visible."),
			token);
		var previous = _renderGraph.SceneCaptureView;
		_renderGraph.SetSceneCaptureView(view);
		try
		{
			return await CaptureFrameAsync(path, token);
		}
		finally
		{
			_renderGraph.SetSceneCaptureView(previous);
		}
	}

	private ShapeEditorStateResult InspectShapeEditor()
	{
		var session = _shapeEditor;
		var document = session.Document;
		var nodes = new List<ShapeNodeResult>();
		if (document is not null)
		{
			foreach (var node in document.Tree.EnumerateDepthFirst())
			{
				var euler = EulerAngles.ToDegrees(node.Rotation);
				nodes.Add(new ShapeNodeResult(
					node.Id.ToString("D"),
					document.Tree.FindParent(node.Id)?.Id.ToString("D"),
					node.Name,
					node.Kind.ToString(),
					node.Operation.ToString(),
					node.Smoothness,
					[node.Position.X, node.Position.Y, node.Position.Z],
					[euler.X, euler.Y, euler.Z],
					node.Scale,
					node.IsGroup ? null : node.Primitive.ToString(),
					node.IsGroup ? null : [node.Dimensions.X, node.Dimensions.Y, node.Dimensions.Z, node.Dimensions.W]));
			}
		}

		return new ShapeEditorStateResult(
			session.HasShape ? session.AssetId.ToString("D") : null,
			session.ShapeName,
			session.Diagnostic,
			session.CompileError,
			session.IsReadOnly,
			session.IsDirty,
			session.IsPromptPending,
			session.CanUndo,
			session.CanRedo,
			document?.UndoDescription,
			session.SelectedNodeId?.ToString("D"),
			nodes,
			document?.NeedsCompile ?? false,
			session.IsCompiling,
			document?.Mesh?.TriangleCount ?? 0,
			document?.Mesh?.VertexCount ?? 0,
			session.AppliedCompileCount,
			session.LastCompileStats,
			session.Preview is not null);
	}

	private void RequireShape()
	{
		if (_shapeEditor.HasShape == false)
		{
			throw new InvalidOperationException("No shape is open. Use open_shape or create_shape first.");
		}
	}

	private void RequireEditableShape()
	{
		RequireShape();
		if (_shapeEditor.CanEdit == false)
		{
			throw new InvalidOperationException("The open shape is read-only.");
		}
	}

	private static ShapePrimitiveKind ParsePrimitive(string kind)
	{
		var normalized = kind.Replace(" ", string.Empty, StringComparison.Ordinal);
		if (Enum.TryParse<ShapePrimitiveKind>(normalized, ignoreCase: true, out var primitive) == false)
		{
			throw new ArgumentException(
				$"'{kind}' is not a primitive. Use group or one of: {string.Join(", ", ShapePrimitives.All)}.",
				nameof(kind));
		}

		return primitive;
	}
}
