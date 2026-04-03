using System.Numerics;
using WolfEngine.ECS;

namespace WolfEngine.Editor.Tests;

[TestFixture]
public sealed class WolfEngineEditorPhysicsTests
{
	[Test]
	public void CanAdvancePhysics_OnlyAllowsPlayingStateWithMatchingRuntimeWorld()
	{
		var runtimeWorld = new World(WorldTag.Game);

		Assert.That(WolfEngineEditor.CanAdvancePhysics(EditorPlayState.Edit, runtimeWorld, runtimeWorld), Is.False);
		Assert.That(WolfEngineEditor.CanAdvancePhysics(EditorPlayState.Paused, runtimeWorld, runtimeWorld), Is.False);
		Assert.That(WolfEngineEditor.CanAdvancePhysics(EditorPlayState.Playing, null, runtimeWorld), Is.False);
		Assert.That(WolfEngineEditor.CanAdvancePhysics(EditorPlayState.Playing, runtimeWorld, new World(WorldTag.Game)), Is.False);
		Assert.That(WolfEngineEditor.CanAdvancePhysics(EditorPlayState.Playing, runtimeWorld, runtimeWorld), Is.True);
	}

	[Test]
	public void GetExecutionMask_PlayingExcludesEditorWorldAndPausedKeepsEditorWorld()
	{
		Assert.That(
			WolfEngineEditor.GetExecutionMask(EditorPlayState.Edit),
			Is.EqualTo((WorldTag.Editor | WorldTag.Authoring, SystemExecutionGroup.Shared)));
		Assert.That(
			WolfEngineEditor.GetExecutionMask(EditorPlayState.Playing),
			Is.EqualTo((WorldTag.Game, SystemExecutionGroup.All)));
		Assert.That(
			WolfEngineEditor.GetExecutionMask(EditorPlayState.Paused),
			Is.EqualTo((WorldTag.Editor | WorldTag.Game, SystemExecutionGroup.Shared)));
	}

	[Test]
	public void GetViewportCamera_PlayingUsesFirstActiveRuntimeCamera()
	{
		var world = new World(WorldTag.Game);
		var scene = new EditorScene { World = world };
		var firstCameraEntity = CreateCameraEntity(
			world,
			"First Camera",
			fov: 55.0f,
			Matrix4x4.CreateTranslation(new Vector3(1.0f, 2.0f, 3.0f)));
		CreateCameraEntity(
			world,
			"Second Camera",
			fov: 80.0f,
			Matrix4x4.CreateTranslation(new Vector3(4.0f, 5.0f, 6.0f)));
		var editorCamera = CreateCamera(70.0f);
		var editorCameraWorldTransform = new WorldTransform { LocalToWorld = Matrix4x4.CreateTranslation(new Vector3(10.0f, 20.0f, 30.0f)) };

		var (camera, cameraWorldTransform) = WolfEngineEditor.GetViewportCamera(
			EditorPlayState.Playing,
			scene,
			editorCamera,
			editorCameraWorldTransform);

		Assert.That(camera.Fov, Is.EqualTo(55.0f));
		Assert.That(cameraWorldTransform.LocalToWorld, Is.EqualTo(world.GetComponent<WorldTransform>(firstCameraEntity).LocalToWorld));
	}

	[Test]
	public void GetViewportCamera_PlayingFallsBackToEditorCameraWhenRuntimeSceneHasNoCamera()
	{
		var scene = new EditorScene { World = new World(WorldTag.Game) };
		var editorCamera = CreateCamera(70.0f);
		var editorCameraWorldTransform = new WorldTransform { LocalToWorld = Matrix4x4.CreateTranslation(new Vector3(10.0f, 20.0f, 30.0f)) };

		var (camera, cameraWorldTransform) = WolfEngineEditor.GetViewportCamera(
			EditorPlayState.Playing,
			scene,
			editorCamera,
			editorCameraWorldTransform);

		Assert.That(camera.Fov, Is.EqualTo(editorCamera.Fov));
		Assert.That(cameraWorldTransform.LocalToWorld, Is.EqualTo(editorCameraWorldTransform.LocalToWorld));
	}

	[Test]
	public void GetViewportCamera_PausedUsesEditorCameraEvenWhenRuntimeSceneHasCamera()
	{
		var world = new World(WorldTag.Game);
		var scene = new EditorScene { World = world };
		CreateCameraEntity(
			world,
			"Runtime Camera",
			fov: 55.0f,
			Matrix4x4.CreateTranslation(new Vector3(1.0f, 2.0f, 3.0f)));
		var editorCamera = CreateCamera(70.0f);
		var editorCameraWorldTransform = new WorldTransform { LocalToWorld = Matrix4x4.CreateTranslation(new Vector3(10.0f, 20.0f, 30.0f)) };

		var (camera, cameraWorldTransform) = WolfEngineEditor.GetViewportCamera(
			EditorPlayState.Paused,
			scene,
			editorCamera,
			editorCameraWorldTransform);

		Assert.That(camera.Fov, Is.EqualTo(editorCamera.Fov));
		Assert.That(cameraWorldTransform.LocalToWorld, Is.EqualTo(editorCameraWorldTransform.LocalToWorld));
	}

	private static Entity CreateCameraEntity(World world, string name, float fov, Matrix4x4 transform)
	{
		var entity = world.CreateEntity(name, transform);
		ref var worldTransform = ref world.GetComponent<WorldTransform>(entity);
		worldTransform.LocalToWorld = transform;
		worldTransform.WorldToLocal = Matrix4x4.Identity;
		world.AddComponent(entity, CreateCamera(fov));
		return entity;
	}

	private static Camera CreateCamera(float fov)
	{
		var camera = new Camera
		{
			ScreenResolution = new(1920, 1080)
		};
		camera.SetPerspective(fov);
		return camera;
	}
}
