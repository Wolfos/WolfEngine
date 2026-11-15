using System.Numerics;
using NSubstitute;
using WolfEngine.ECS;
using WolfEngine.Editor.Projects;
using WolfEngine.Editor.UI;
using WolfEngine.Rendering;

namespace WolfEngine.Editor.Tests;

[TestFixture]
public sealed class SceneEntityGizmoTargetTests
{
	private EditorScene _scene = null!;
	private Entity _entity;
	private IEditorInteractionState _interactionState = null!;
	private IEditorUndoRedoService _undoRedoService = null!;
	private SceneEntityGizmoTarget _target = null!;

	[SetUp]
	public void SetUp()
	{
		_scene = new EditorScene();
		_entity = _scene.World.CreateEntity("Moved", Matrix4x4.CreateTranslation(1.0f, 2.0f, 3.0f));
		new TransformSystem().PreRender(0.0f, _scene.World);
		_interactionState = Substitute.For<IEditorInteractionState>();
		_undoRedoService = Substitute.For<IEditorUndoRedoService>();

		var resolver = Substitute.For<IProjectTypeResolver>();
		resolver.GetTypeName(Arg.Any<Type>()).Returns(call => call.Arg<Type>().AssemblyQualifiedName);
		resolver.GetStableTypeId(Arg.Any<Type>()).Returns(call => call.Arg<Type>().FullName);

		_target = new SceneEntityGizmoTarget(
			_scene,
			_scene.World,
			[_entity],
			_interactionState,
			new EditorSceneSnapshotService(resolver),
			_undoRedoService);
	}

	[Test]
	public void GetState_ReportsWorldPositionAndLocalScale()
	{
		var state = _target.GetState(0);

		Assert.That(state.WorldPosition, Is.EqualTo(new Vector3(1.0f, 2.0f, 3.0f)));
		Assert.That(state.WorldRotation, Is.EqualTo(Quaternion.Identity));
		Assert.That(state.LocalScale, Is.EqualTo(Vector3.One));
	}

	[Test]
	public void CommittedDrag_RecordsOneTransformUndoStep()
	{
		_target.BeginEdit();
		_target.SetWorldPosition(0, new Vector3(5.0f, 2.0f, 3.0f));
		_target.OnTransformsChanged();
		_target.EndEdit(commit: true);

		Assert.That(_scene.World.GetComponent<LocalTransform>(_entity).LocalPosition, Is.EqualTo(new Vector3(5.0f, 2.0f, 3.0f)));
		_interactionState.Received(1).MarkSceneDirty(_scene.World);
		_undoRedoService.Received(1).BeginCapture("Transform");
		_undoRedoService.Received(1).CommitCapture(Arg.Any<SceneComponentEditUndoRedoEntry>());
	}

	[Test]
	public void CommittedDragThatChangedNothing_RecordsNoUndoStep()
	{
		_target.BeginEdit();
		_target.EndEdit(commit: true);

		_undoRedoService.DidNotReceiveWithAnyArgs().CommitCapture(default!);
	}

	[Test]
	public void AbandonedDrag_RecordsNoUndoStep()
	{
		_target.BeginEdit();
		_target.SetWorldPosition(0, new Vector3(5.0f, 2.0f, 3.0f));
		_target.EndEdit(commit: false);

		_undoRedoService.DidNotReceiveWithAnyArgs().CommitCapture(default!);
	}

	[Test]
	public void DestroyedEntity_IsSkipped()
	{
		_target.BeginEdit();
		_scene.World.DestroyEntity(_entity);

		Assert.DoesNotThrow(() => _target.SetWorldPosition(0, Vector3.Zero));
		Assert.DoesNotThrow(() => _target.EndEdit(commit: true));
		_undoRedoService.DidNotReceiveWithAnyArgs().CommitCapture(default!);
	}
}
