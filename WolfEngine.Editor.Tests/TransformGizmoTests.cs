using System.Numerics;
using WolfEngine.Editor.UI;
using WolfEngine.Mathematics;
using WolfEngine.Rendering;
using WolfEngine.Rendering.UI;

namespace WolfEngine.Editor.Tests;

[TestFixture]
public sealed class TransformGizmoTests
{
	private const float Tolerance = 1e-3f;

	private TransformGizmoView _view;

	[SetUp]
	public void SetUp()
	{
		_view = CreateView(new Vector3(0.0f, 0.0f, -10.0f));
	}

	[Test]
	public void TranslateDrag_MovesTargetAlongTheGrabbedAxis()
	{
		var gizmo = new TransformGizmo();
		var target = new RecordingTarget(new TransformGizmoTargetState(Vector3.Zero, Quaternion.Identity, Vector3.One));
		var options = Options(TransformGizmoMode.Translate);

		gizmo.Update(target, _view, Press(new Vector3(0.75f, 0.0f, 0.0f)), options);
		gizmo.Update(target, _view, Hold(new Vector3(1.75f, 0.0f, 0.0f)), options);

		Assert.That(gizmo.IsDragging, Is.True);
		Assert.That(target.BeginCount, Is.EqualTo(1));
		AssertVector(target.Positions[0], new Vector3(1.0f, 0.0f, 0.0f));
		Assert.That(target.ChangedCount, Is.GreaterThan(0));
	}

	[Test]
	public void Release_CommitsTheDragOnce()
	{
		var gizmo = new TransformGizmo();
		var target = new RecordingTarget(new TransformGizmoTargetState(Vector3.Zero, Quaternion.Identity, Vector3.One));
		var options = Options(TransformGizmoMode.Translate);

		gizmo.Update(target, _view, Press(new Vector3(0.75f, 0.0f, 0.0f)), options);
		gizmo.Update(target, _view, Release(new Vector3(1.0f, 0.0f, 0.0f)), options);
		gizmo.Update(target, _view, Idle(new Vector3(1.0f, 0.0f, 0.0f)), options);

		Assert.That(gizmo.IsDragging, Is.False);
		Assert.That(target.EndCalls, Is.EqualTo(new[] { true }));
	}

	[Test]
	public void Cancel_EndsTheDragWithoutCommitting()
	{
		var gizmo = new TransformGizmo();
		var target = new RecordingTarget(new TransformGizmoTargetState(Vector3.Zero, Quaternion.Identity, Vector3.One));

		gizmo.Update(target, _view, Press(new Vector3(0.75f, 0.0f, 0.0f)), Options(TransformGizmoMode.Translate));
		gizmo.Cancel();

		Assert.That(gizmo.IsDragging, Is.False);
		Assert.That(target.EndCalls, Is.EqualTo(new[] { false }));
	}

	[Test]
	public void MissingTarget_AbandonsAnActiveDrag()
	{
		var gizmo = new TransformGizmo();
		var target = new RecordingTarget(new TransformGizmoTargetState(Vector3.Zero, Quaternion.Identity, Vector3.One));
		var options = Options(TransformGizmoMode.Translate);

		gizmo.Update(target, _view, Press(new Vector3(0.75f, 0.0f, 0.0f)), options);
		gizmo.Update(null, _view, Hold(new Vector3(1.0f, 0.0f, 0.0f)), options);

		Assert.That(target.EndCalls, Is.EqualTo(new[] { false }));
	}

	[Test]
	public void DragKeepsTheTargetItStartedWith()
	{
		var gizmo = new TransformGizmo();
		var original = new RecordingTarget(new TransformGizmoTargetState(Vector3.Zero, Quaternion.Identity, Vector3.One));
		var replacement = new RecordingTarget(new TransformGizmoTargetState(Vector3.Zero, Quaternion.Identity, Vector3.One));
		var options = Options(TransformGizmoMode.Translate);

		gizmo.Update(original, _view, Press(new Vector3(0.75f, 0.0f, 0.0f)), options);
		gizmo.Update(replacement, _view, Hold(new Vector3(1.75f, 0.0f, 0.0f)), options);

		AssertVector(original.Positions[0], new Vector3(1.0f, 0.0f, 0.0f));
		Assert.That(replacement.BeginCount, Is.Zero);
		Assert.That(replacement.Positions[0], Is.EqualTo(Vector3.Zero));
	}

	[Test]
	public void PressAwayFromHandles_DoesNotStartADrag()
	{
		var gizmo = new TransformGizmo();
		var target = new RecordingTarget(new TransformGizmoTargetState(Vector3.Zero, Quaternion.Identity, Vector3.One));

		gizmo.Update(target, _view, Press(new Vector3(3.0f, 3.0f, 0.0f)), Options(TransformGizmoMode.Translate));

		Assert.That(gizmo.IsDragging, Is.False);
		Assert.That(target.BeginCount, Is.Zero);
	}

	[Test]
	public void PressWhenPointerIsUnavailable_DoesNotStartADrag()
	{
		var gizmo = new TransformGizmo();
		var target = new RecordingTarget(new TransformGizmoTargetState(Vector3.Zero, Quaternion.Identity, Vector3.One));
		var press = Press(new Vector3(0.75f, 0.0f, 0.0f)) with
		{
			CanBeginDrag = false
		};

		gizmo.Update(target, _view, press, Options(TransformGizmoMode.Translate));

		Assert.That(gizmo.IsDragging, Is.False);
	}

	[Test]
	public void LocalSpace_AxesFollowThePrimaryTargetRotation()
	{
		var gizmo = new TransformGizmo();
		var rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI * 0.5f);
		var target = new RecordingTarget(new TransformGizmoTargetState(Vector3.Zero, rotation, Vector3.One));
		var options = Options(TransformGizmoMode.Translate);

		// The local X handle now points along world +Y.
		gizmo.Update(target, _view, Press(new Vector3(0.0f, 0.75f, 0.0f)), options);
		gizmo.Update(target, _view, Hold(new Vector3(0.0f, 1.75f, 0.0f)), options);

		AssertVector(target.Positions[0], new Vector3(0.0f, 1.0f, 0.0f));
	}

	[Test]
	public void WorldSpace_AxesIgnoreThePrimaryTargetRotation()
	{
		var gizmo = new TransformGizmo();
		var rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI * 0.5f);
		var target = new RecordingTarget(new TransformGizmoTargetState(Vector3.Zero, rotation, Vector3.One));
		var options = Options(TransformGizmoMode.Translate) with
		{
			Space = TransformSpace.World
		};

		gizmo.Update(target, _view, Press(new Vector3(0.75f, 0.0f, 0.0f)), options);
		gizmo.Update(target, _view, Hold(new Vector3(1.75f, 0.0f, 0.0f)), options);

		AssertVector(target.Positions[0], new Vector3(1.0f, 0.0f, 0.0f));
	}

	[Test]
	public void RotateDrag_TurnsTargetAroundTheGrabbedRing()
	{
		var gizmo = new TransformGizmo();
		var target = new RecordingTarget(new TransformGizmoTargetState(Vector3.Zero, Quaternion.Identity, Vector3.One));
		var options = Options(TransformGizmoMode.Rotate);

		// The ring radius is 1.2 at this camera distance; grab the Z ring at 45 degrees.
		var ringPoint = 1.2f / MathF.Sqrt(2.0f);
		gizmo.Update(target, _view, Press(new Vector3(ringPoint, ringPoint, 0.0f)), options);
		gizmo.Update(target, _view, Hold(new Vector3(-ringPoint, ringPoint, 0.0f)), options);

		var expected = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI * 0.5f);
		Assert.That(MathF.Abs(Quaternion.Dot(target.Rotations[0], expected)), Is.EqualTo(1.0f).Within(Tolerance));
		Assert.That(target.Positions[0], Is.EqualTo(Vector3.Zero));
	}

	[Test]
	public void RotateAroundPivot_OrbitsTargetPositions()
	{
		var gizmo = new TransformGizmo();
		var target = new RecordingTarget(
			new TransformGizmoTargetState(new Vector3(1.0f, 0.0f, 0.0f), Quaternion.Identity, Vector3.One),
			new TransformGizmoTargetState(new Vector3(-1.0f, 0.0f, 0.0f), Quaternion.Identity, Vector3.One));
		var options = Options(TransformGizmoMode.Rotate) with
		{
			RotateAroundPivot = true
		};

		var ringPoint = 1.2f / MathF.Sqrt(2.0f);
		gizmo.Update(target, _view, Press(new Vector3(ringPoint, ringPoint, 0.0f)), options);
		gizmo.Update(target, _view, Hold(new Vector3(-ringPoint, ringPoint, 0.0f)), options);

		AssertVector(target.Positions[0], new Vector3(0.0f, 1.0f, 0.0f));
		AssertVector(target.Positions[1], new Vector3(0.0f, -1.0f, 0.0f));
	}

	[Test]
	public void ScaleDrag_ScalesOnlyTheGrabbedAxis()
	{
		var gizmo = new TransformGizmo();
		var target = new RecordingTarget(new TransformGizmoTargetState(Vector3.Zero, Quaternion.Identity, new Vector3(2.0f, 1.0f, 1.0f)));
		var options = Options(TransformGizmoMode.Scale);

		// The handle is 1.5 long, so dragging 0.75 along it scales by 1.5.
		gizmo.Update(target, _view, Press(new Vector3(0.75f, 0.0f, 0.0f)), options);
		gizmo.Update(target, _view, Hold(new Vector3(1.5f, 0.0f, 0.0f)), options);

		AssertVector(target.Scales[0], new Vector3(3.0f, 1.0f, 1.0f));
	}

	[Test]
	public void InvalidProjection_HidesAndCancels()
	{
		var gizmo = new TransformGizmo();
		var target = new RecordingTarget(new TransformGizmoTargetState(Vector3.Zero, Quaternion.Identity, Vector3.One));
		var options = Options(TransformGizmoMode.Translate);

		gizmo.Update(target, _view, Press(new Vector3(0.75f, 0.0f, 0.0f)), options);
		gizmo.Update(target, _view with { ViewProjection = default }, Hold(new Vector3(1.0f, 0.0f, 0.0f)), options);

		Assert.That(gizmo.IsDragging, Is.False);
		Assert.That(target.EndCalls, Is.EqualTo(new[] { false }));
	}

	private static TransformGizmoOptions Options(TransformGizmoMode mode) =>
		new(mode, TransformSpace.Local, Vector3.Zero, RotateAroundPivot: false);

	private TransformGizmoPointer Press(Vector3 worldPoint) =>
		new(Project(worldPoint), CanBeginDrag: true, LeftDown: true, LeftClicked: true, LeftReleased: false);

	private TransformGizmoPointer Hold(Vector3 worldPoint) =>
		new(Project(worldPoint), CanBeginDrag: true, LeftDown: true, LeftClicked: false, LeftReleased: false);

	private TransformGizmoPointer Release(Vector3 worldPoint) =>
		new(Project(worldPoint), CanBeginDrag: true, LeftDown: false, LeftClicked: false, LeftReleased: true);

	private TransformGizmoPointer Idle(Vector3 worldPoint) =>
		new(Project(worldPoint), CanBeginDrag: true, LeftDown: false, LeftClicked: false, LeftReleased: false);

	private Vector2 Project(Vector3 worldPoint)
	{
		var projected = TransformGizmo.TryProjectToScreen(
			worldPoint,
			_view.ViewProjection.WorldToClip,
			_view.Viewport.ImageMin,
			_view.Viewport.ImageMax,
			out var screenPoint);
		Assert.That(projected, Is.True);

		return screenPoint;
	}

	private static void AssertVector(Vector3 actual, Vector3 expected)
	{
		Assert.That(Vector3.Distance(actual, expected), Is.LessThan(Tolerance), $"Expected {expected}, got {actual}.");
	}

	private static TransformGizmoView CreateView(Vector3 cameraPosition)
	{
		var camera = new Camera
		{
			ScreenResolution = new Int2(800, 600)
		};
		camera.SetPerspective(60.0f);

		var view = CreateLookAtLeftHanded(cameraPosition, Vector3.Zero, Vector3.UnitY);
		Assert.That(ViewProjection.TryCreate(view, camera.Perspective, out var viewProjection), Is.True);

		var viewport = new SceneViewportUiState(
			visible: true,
			contentSizePixels: new Int2(800, 600),
			resolutionScale: 1.0f,
			requestedDebugViewId: SceneDebugViewIds.FinalColor,
			hovered: true,
			focused: true,
			pointerAvailable: true,
			pointerCaptured: true,
			rightMousePressStartedHere: false,
			imageMin: Vector2.Zero,
			imageMax: new Vector2(800.0f, 600.0f));

		return new TransformGizmoView(viewport, viewProjection);
	}

	private static Matrix4x4 CreateLookAtLeftHanded(Vector3 position, Vector3 target, Vector3 up)
	{
		var zAxis = Vector3.Normalize(target - position);
		var xAxis = Vector3.Normalize(Vector3.Cross(up, zAxis));
		var yAxis = Vector3.Cross(zAxis, xAxis);

		return new Matrix4x4(
			xAxis.X, yAxis.X, zAxis.X, 0.0f,
			xAxis.Y, yAxis.Y, zAxis.Y, 0.0f,
			xAxis.Z, yAxis.Z, zAxis.Z, 0.0f,
			-Vector3.Dot(xAxis, position), -Vector3.Dot(yAxis, position), -Vector3.Dot(zAxis, position), 1.0f);
	}

	private sealed class RecordingTarget : ITransformGizmoTarget
	{
		private readonly TransformGizmoTargetState[] _states;

		public RecordingTarget(params TransformGizmoTargetState[] states)
		{
			_states = states;
			Positions = states.Select(state => state.WorldPosition).ToArray();
			Rotations = states.Select(state => state.WorldRotation).ToArray();
			Scales = states.Select(state => state.LocalScale).ToArray();
		}

		public Vector3[] Positions { get; }
		public Quaternion[] Rotations { get; }
		public Vector3[] Scales { get; }
		public int BeginCount { get; private set; }
		public int ChangedCount { get; private set; }
		public List<bool> EndCalls { get; } = new();

		public int Count => _states.Length;

		public TransformGizmoTargetState GetState(int index) => _states[index];

		public void BeginEdit()
		{
			BeginCount++;
		}

		public void SetWorldPosition(int index, Vector3 worldPosition)
		{
			Positions[index] = worldPosition;
		}

		public void SetWorldRotation(int index, Quaternion worldRotation)
		{
			Rotations[index] = worldRotation;
		}

		public void SetLocalScale(int index, Vector3 localScale)
		{
			Scales[index] = localScale;
		}

		public void OnTransformsChanged()
		{
			ChangedCount++;
		}

		public void EndEdit(bool commit)
		{
			EndCalls.Add(commit);
		}
	}
}
