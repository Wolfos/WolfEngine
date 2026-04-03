using System.Numerics;
using NSubstitute;
using WolfEngine.ECS;
using WolfEngine.Editor.Projects;
using WolfEngine.Editor.UI;

namespace WolfEngine.Editor.Tests;

[TestFixture]
public sealed class EntityHierarchyEditorOperationsTests
{
	[Test]
	public void TryReparentEntity_ParentsEntityAndPreservesWorldTransform()
	{
		var scene = new EditorScene();
		var transformSystem = new TransformSystem();
		var parent = scene.World.CreateEntity("Parent", new Vector3(10.0f, 0.0f, 0.0f), Quaternion.Identity, Vector3.One);
		var child = scene.World.CreateEntity("Child", new Vector3(2.0f, 3.0f, 4.0f), Quaternion.Identity, Vector3.One);
		transformSystem.PreRender(0.0f, scene.World);
		var beforeWorldTransform = scene.World.GetComponent<WorldTransform>(child).LocalToWorld;
		var interactionState = Substitute.For<IEditorInteractionState>();
		var undoRedoService = Substitute.For<IEditorUndoRedoService>();

		var changed = EntityHierarchyEditorOperations.TryReparentEntity(
			scene,
			child,
			parent,
			new EditorSceneSnapshotService(Substitute.For<IProjectTypeResolver>()),
			undoRedoService,
			interactionState);
		transformSystem.PreRender(0.0f, scene.World);

		Assert.That(changed, Is.True);
		Assert.That(scene.World.HasComponent<Parent>(child), Is.True);
		Assert.That(scene.World.GetComponent<Parent>(child).Value, Is.EqualTo(parent));
		AssertMatrix(scene.World.GetComponent<WorldTransform>(child).LocalToWorld, beforeWorldTransform);
		interactionState.Received(1).MarkSceneDirty();
		undoRedoService.Received(1).BeginCapture("Reparent Entity");
		undoRedoService.Received(1).CommitCapture(Arg.Any<EntityHierarchyUndoRedoEntry>());
	}

	[Test]
	public void TryReparentEntity_UnparentsEntityAndPreservesWorldTransform()
	{
		var scene = new EditorScene();
		var transformSystem = new TransformSystem();
		var parent = scene.World.CreateEntity("Parent", new Vector3(10.0f, 0.0f, 0.0f), Quaternion.Identity, Vector3.One);
		var child = scene.World.CreateEntity("Child", new Vector3(2.0f, 3.0f, 4.0f), Quaternion.Identity, Vector3.One);
		scene.World.SetParent(child, parent);
		transformSystem.PreRender(0.0f, scene.World);
		var beforeWorldTransform = scene.World.GetComponent<WorldTransform>(child).LocalToWorld;
		var interactionState = Substitute.For<IEditorInteractionState>();
		var undoRedoService = Substitute.For<IEditorUndoRedoService>();

		var changed = EntityHierarchyEditorOperations.TryReparentEntity(
			scene,
			child,
			null,
			new EditorSceneSnapshotService(Substitute.For<IProjectTypeResolver>()),
			undoRedoService,
			interactionState);
		transformSystem.PreRender(0.0f, scene.World);

		Assert.That(changed, Is.True);
		Assert.That(scene.World.HasComponent<Parent>(child), Is.False);
		AssertMatrix(scene.World.GetComponent<WorldTransform>(child).LocalToWorld, beforeWorldTransform);
		interactionState.Received(1).MarkSceneDirty();
		undoRedoService.Received(1).BeginCapture("Unparent Entity");
		undoRedoService.Received(1).CommitCapture(Arg.Any<EntityHierarchyUndoRedoEntry>());
	}

	[Test]
	public void TryReparentEntity_WithCurrentParent_ReturnsFalseAndDoesNotRecordUndo()
	{
		var scene = new EditorScene();
		var parent = scene.World.CreateEntity("Parent", Vector3.Zero, Quaternion.Identity, Vector3.One);
		var child = scene.World.CreateEntity("Child", Vector3.One, Quaternion.Identity, Vector3.One);
		scene.World.SetParent(child, parent);
		var interactionState = Substitute.For<IEditorInteractionState>();
		var undoRedoService = Substitute.For<IEditorUndoRedoService>();

		var changed = EntityHierarchyEditorOperations.TryReparentEntity(
			scene,
			child,
			parent,
			new EditorSceneSnapshotService(Substitute.For<IProjectTypeResolver>()),
			undoRedoService,
			interactionState);

		Assert.That(changed, Is.False);
		interactionState.DidNotReceive().MarkSceneDirty();
		undoRedoService.DidNotReceive().BeginCapture(Arg.Any<string>());
		undoRedoService.DidNotReceive().CommitCapture(Arg.Any<IEditorUndoRedoEntry>());
	}

	[Test]
	public void TryReparentEntity_WithDescendantParent_ReturnsFalseAndKeepsHierarchy()
	{
		var scene = new EditorScene();
		var parent = scene.World.CreateEntity("Parent", Vector3.Zero, Quaternion.Identity, Vector3.One);
		var child = scene.World.CreateEntity("Child", Vector3.One, Quaternion.Identity, Vector3.One);
		var grandchild = scene.World.CreateEntity("Grandchild", Vector3.One * 2.0f, Quaternion.Identity, Vector3.One);
		scene.World.SetParent(child, parent);
		scene.World.SetParent(grandchild, child);
		var interactionState = Substitute.For<IEditorInteractionState>();
		var undoRedoService = Substitute.For<IEditorUndoRedoService>();

		var changed = EntityHierarchyEditorOperations.TryReparentEntity(
			scene,
			parent,
			grandchild,
			new EditorSceneSnapshotService(Substitute.For<IProjectTypeResolver>()),
			undoRedoService,
			interactionState);

		Assert.That(changed, Is.False);
		Assert.That(scene.World.HasComponent<Parent>(parent), Is.False);
		Assert.That(scene.World.GetComponent<Parent>(child).Value, Is.EqualTo(parent));
		Assert.That(scene.World.GetComponent<Parent>(grandchild).Value, Is.EqualTo(child));
		interactionState.DidNotReceive().MarkSceneDirty();
		undoRedoService.DidNotReceive().BeginCapture(Arg.Any<string>());
		undoRedoService.DidNotReceive().CommitCapture(Arg.Any<IEditorUndoRedoEntry>());
	}

	private static void AssertMatrix(Matrix4x4 actual, Matrix4x4 expected, float tolerance = 0.0001f)
	{
		Assert.That(actual.M11, Is.EqualTo(expected.M11).Within(tolerance));
		Assert.That(actual.M12, Is.EqualTo(expected.M12).Within(tolerance));
		Assert.That(actual.M13, Is.EqualTo(expected.M13).Within(tolerance));
		Assert.That(actual.M14, Is.EqualTo(expected.M14).Within(tolerance));
		Assert.That(actual.M21, Is.EqualTo(expected.M21).Within(tolerance));
		Assert.That(actual.M22, Is.EqualTo(expected.M22).Within(tolerance));
		Assert.That(actual.M23, Is.EqualTo(expected.M23).Within(tolerance));
		Assert.That(actual.M24, Is.EqualTo(expected.M24).Within(tolerance));
		Assert.That(actual.M31, Is.EqualTo(expected.M31).Within(tolerance));
		Assert.That(actual.M32, Is.EqualTo(expected.M32).Within(tolerance));
		Assert.That(actual.M33, Is.EqualTo(expected.M33).Within(tolerance));
		Assert.That(actual.M34, Is.EqualTo(expected.M34).Within(tolerance));
		Assert.That(actual.M41, Is.EqualTo(expected.M41).Within(tolerance));
		Assert.That(actual.M42, Is.EqualTo(expected.M42).Within(tolerance));
		Assert.That(actual.M43, Is.EqualTo(expected.M43).Within(tolerance));
		Assert.That(actual.M44, Is.EqualTo(expected.M44).Within(tolerance));
	}
}
