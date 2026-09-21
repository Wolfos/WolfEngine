using System.Numerics;
using WolfEngine.ECS;
using WolfEngine.Input;
using WolfEngine.Mathematics;
using WolfEngine.Rendering;
using WolfEngine.Rendering.UI;

namespace WolfEngine.Editor.Tests;

[TestFixture]
public sealed class EditorCameraSystemTests
{
	[Test]
	public void InputAndPoseChangesAreScopedToTheChosenView()
	{
		var input = new InputSystem();
		var states = new EditorViewportStateBus();
		var system = new EditorCameraSystem(input, states);
		var primaryWorld = new World(WorldTag.Editor);
		var previewWorld = new World(WorldTag.Editor);
		var primary = CreateCamera(primaryWorld, RenderViewId.Primary);
		var previewView = RenderViewId.FromIndex(1);
		var preview = CreateCamera(previewWorld, previewView);

		states.PublishUiState(RenderViewId.Primary, VisibleState(rightStartedHere: false));
		states.PublishUiState(previewView, VisibleState(rightStartedHere: true));
		input.SetButton(InputActionBinding.MouseButtonRight, true);
		input.SetButton(InputActionBinding.KeyW, true);
		input.SetAxis2D(InputActionBinding.MouseDelta, new Vector2(20.0f, 0.0f));
		system.BeginFrame();
		system.Update(1.0f, primaryWorld); // Primary is deliberately visited first.
		system.Update(1.0f, previewWorld);

		Assert.That(primaryWorld.GetComponent<LocalTransform>(primary).LocalPosition, Is.EqualTo(Vector3.Zero));
		Assert.That(previewWorld.GetComponent<LocalTransform>(preview).LocalPosition.Length(), Is.GreaterThan(4.9f));
		Assert.That(primaryWorld.GetComponent<EditorCameraMover>(primary).Yaw, Is.EqualTo(0.0f));
		Assert.That(previewWorld.GetComponent<EditorCameraMover>(preview).Yaw, Is.GreaterThan(0.0f));
		var previewAfterInput = previewWorld.GetComponent<LocalTransform>(preview).LocalPosition;

		input.SetButton(InputActionBinding.MouseButtonRight, false);
		states.PublishUiState(previewView, VisibleState(rightStartedHere: false));
		states.PublishUiState(RenderViewId.Primary, VisibleState(rightStartedHere: true));
		input.SetButton(InputActionBinding.MouseButtonRight, true);
		input.SetAxis2D(InputActionBinding.MouseDelta, new Vector2(-20.0f, 0.0f));
		system.BeginFrame();
		system.Update(1.0f, previewWorld); // Reverse visitation order for the second drag.
		system.Update(1.0f, primaryWorld);
		Assert.That(previewWorld.GetComponent<LocalTransform>(preview).LocalPosition, Is.EqualTo(previewAfterInput));
		Assert.That(primaryWorld.GetComponent<LocalTransform>(primary).LocalPosition.Length(), Is.GreaterThan(4.9f));
		Assert.That(primaryWorld.GetComponent<EditorCameraMover>(primary).Yaw, Is.LessThan(0.0f));
		input.SetButton(InputActionBinding.MouseButtonRight, false);
		system.BeginFrame();

		var requestedForward = Vector3.Normalize(new Vector3(1, -1, 1));
		Assert.That(system.SetCameraPose(RenderViewId.Primary, new Vector3(3, 2, 1), requestedForward), Is.True);
		Assert.That(primaryWorld.GetComponent<LocalTransform>(primary).LocalPosition, Is.EqualTo(new Vector3(3, 2, 1)));
		system.Update(0.0f, primaryWorld);
		Assert.That(system.TryGetCameraPose(RenderViewId.Primary, out _, out var actualForward), Is.True);
		Assert.That(Vector3.Distance(actualForward, requestedForward), Is.LessThan(0.0001f));
		Assert.That(system.TryGetCameraPose(previewView, out var previewPosition, out _), Is.True);
		Assert.That(previewPosition, Is.EqualTo(previewAfterInput));

		states.PublishUiState(previewView, SceneViewportUiState.Hidden);
		system.BeginFrame();
		var before = previewWorld.GetComponent<LocalTransform>(preview).LocalPosition;
		system.Update(1.0f, previewWorld);
		Assert.That(previewWorld.GetComponent<LocalTransform>(preview).LocalPosition, Is.EqualTo(before));
	}

	private static Entity CreateCamera(World world, RenderViewId view)
	{
		var entity = world.CreateEntity($"Camera {view}", Matrix4x4.Identity);
		world.AddComponent(entity, new EditorCameraMover { View = view });
		return entity;
	}

	private static SceneViewportUiState VisibleState(bool rightStartedHere) => new(
		visible: true,
		contentSizePixels: new Int2(640, 480),
		resolutionScale: 1.0f,
		requestedDebugViewId: SceneDebugViewIds.FinalColor,
		hovered: true,
		focused: true,
		pointerAvailable: true,
		pointerCaptured: rightStartedHere,
		rightMousePressStartedHere: rightStartedHere,
		imageMin: Vector2.Zero,
		imageMax: new Vector2(640, 480));
}
